#!/usr/bin/env node
// NFR-PERF2 (SPEC §8): "project list p95 < 300 ms with badges (denormalised versions). The execution-runs and
// review-cycles endpoints are p95 < 300 ms for 10 cycles × 30 steps."
//
// 1. Seeds (idempotently) one perf user with 50 "NFR-PERF2 project NN" projects (Fake LLM + Local Git, each with its
//    in-development version badge), so page 1 of the list is 50 rows with badges.
// 2. Seeds one history project "NFR-PERF2-HISTORY" through the real lifecycle with the Fake LLM: advisory mode,
//    compliance threshold 0.99 and max 10 review cycles. The Fake Code Review scores 0.86 then 0.93 (< 0.99): cycles
//    1–2 are review_required with a one-step optimisation plan (approved by the script), then the flat score escalates
//    and the script decides each escalation gate with `continue` (FR-14) until cycle 10. The Fake's step counts aren't
//    configurable at runtime, so the history is 10 cycles with ~13 steps in total — the largest the public API + Fake
//    can produce. The backend test
//    NFR_PERF2_FR57_FR56_… seeds 10 × 30 directly in the database.
// 3. Measures p50/p95/max over REQUESTS (default 50, ≥ 30) sequential requests, plus CONCURRENCY parallel clients, of
//    GET /api/projects (page 1, 50 rows), GET …/execution/runs and GET …/compliance/reports.
//
// Usage (Node 24, from the repo root):  node scripts/perf/v2-perf.mjs          # http://localhost:8080
// Env: BASE_URL, REQUESTS=50, CONCURRENCY=4, CYCLES=10, PERF_EMAIL=nfr-perf2@perf.aide.local, PERF_PASSWORD=…
// NOTE: creates data in the target database (the perf user and its projects). Re-runs reuse them. The per-run API
// token is revoked when the run ends.

import { performance } from 'node:perf_hooks'

const BASE = (process.env.BASE_URL ?? 'http://localhost:8080').replace(/\/$/, '')
const REQUESTS = Math.max(30, Number(process.env.REQUESTS ?? 50))
const CONCURRENCY = Number(process.env.CONCURRENCY ?? 4)
const CYCLES = Number(process.env.CYCLES ?? 10)
const PROJECTS = 50
const EMAIL = process.env.PERF_EMAIL ?? 'nfr-perf2@perf.aide.local'
const PASSWORD = process.env.PERF_PASSWORD ?? 'nfr-perf2-password-1'
const P95_TARGET_MS = 300
const HISTORY_NAME = 'NFR-PERF2-HISTORY'

const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

// ---- HTTP -----------------------------------------------------------------------------------------------------------

async function bearerToken() {
  const send = (method, path, body, headers = {}) =>
    fetch(BASE + path, {
      method,
      headers: { ...(body === undefined ? {} : { 'content-type': 'application/json' }), ...headers },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  let response = await send('POST', '/api/auth/login', { email: EMAIL, password: PASSWORD })
  if (response.status === 401) {
    response = await send('POST', '/api/auth/register', { email: EMAIL, name: 'NFR-PERF2 load user', password: PASSWORD })
  }
  if (!response.ok) {
    throw new Error(`sign-in failed: ${response.status} ${await response.text()}`)
  }
  const cookies = response.headers.getSetCookie().map((c) => c.split(';')[0])
  const xsrf = decodeURIComponent(cookies.find((c) => c.startsWith('XSRF-TOKEN='))?.slice('XSRF-TOKEN='.length) ?? '')
  const session = { cookie: cookies.join('; '), 'X-XSRF-TOKEN': xsrf }
  response = await send(
    'POST',
    '/api/me/tokens',
    { name: `nfr-perf2 ${new Date().toISOString()}`, expiresAt: new Date(Date.now() + 86_400_000).toISOString() },
    session,
  )
  if (!response.ok) {
    throw new Error(`token creation failed: ${response.status} ${await response.text()}`)
  }
  const created = await response.json()
  return {
    token: created.token,
    async revoke() {
      const revoked = await send('DELETE', `/api/me/tokens/${created.id}`, undefined, session)
      if (!revoked.ok) {
        console.error(`warning: revoking the perf API token ${created.id} failed: ${revoked.status}`)
      }
    },
  }
}

let token = ''

async function raw(method, path, body) {
  return fetch(BASE + path, {
    method,
    headers: { authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'content-type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
}

async function call(method, path, body) {
  const response = await raw(method, path, body)
  const text = await response.text()
  if (!response.ok) {
    throw new Error(`${method} ${path} → ${response.status} ${text.slice(0, 300)}`)
  }
  return text ? JSON.parse(text) : null
}

async function pool(items, size, fn) {
  const queue = [...items]
  await Promise.all(
    Array.from({ length: Math.min(size, queue.length) }, async () => {
      while (queue.length > 0) {
        await fn(queue.shift())
      }
    }),
  )
}

/** Polls `probe` (returns a value or undefined) until it yields; throws with `what` after `timeoutMs`. */
async function until(what, probe, timeoutMs = 180_000) {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const value = await probe()
    if (value !== undefined && value !== null && value !== false) {
      return value
    }
    if (Date.now() > deadline) {
      throw new Error(`timed out waiting for ${what}`)
    }
    await pause(500)
  }
}

const projectBody = (name, extra = {}) => ({
  name,
  idea: `${name}: seeded by scripts/perf/v2-perf.mjs to measure read latency. Safe to delete.`,
  approvalMode: 'advisory',
  complianceThreshold: 0.9,
  maxFilesPerStep: 20,
  maxReviewCycles: 3,
  tokenBudget: null,
  source: 'scratch',
  initialVersion: '0.1.0',
  git: { mode: 'local', defaultBranch: 'main' },
  ...extra,
})

// ---- Seed: 50 projects ----------------------------------------------------------------------------------------------

async function seedProjects() {
  const list = async () =>
    (await call('GET', '/api/projects?page=1&limit=100&search=NFR-PERF2%20project')).items.filter((p) => p.name.startsWith('NFR-PERF2 project '))
  let projects = await list()
  const missing = PROJECTS - projects.length
  if (missing > 0) {
    await pool(Array.from({ length: missing }, (_, i) => projects.length + i + 1), 4, async (n) => {
      await call('POST', '/api/projects', projectBody(`NFR-PERF2 project ${String(n).padStart(2, '0')}`))
    })
    projects = await list()
  }
  return projects.length
}

// ---- Seed: the history project (10 review cycles through the Fake lifecycle) ----------------------------------------

const latest = async (id, type) => {
  const r = await raw('GET', `/api/projects/${id}/artifacts/${type}/latest`)
  return r.ok ? r.json() : null
}
const state = (id) => call('GET', `/api/projects/${id}/state`)
const approveDraft = async (id, type) => {
  const draft = await until(`${type} draft`, async () => {
    const a = await latest(id, type)
    return a?.status === 'draft' ? a : undefined
  })
  await call('POST', `/api/projects/${id}/artifacts/${draft.id}/approve`, {})
}
const approveGate = async (id, type) => {
  const gate = await until(`${type} gate`, async () =>
    (await call('GET', `/api/projects/${id}/gates`)).find((g) => g.status === 'pending' && g.type === type),
  )
  await call('POST', `/api/projects/${id}/gates/${gate.id}/approve`, {})
}

async function seedHistory() {
  const found = (await call('GET', `/api/projects?page=1&limit=20&search=${encodeURIComponent(HISTORY_NAME)}`)).items.find(
    (p) => p.name === HISTORY_NAME,
  )
  const project =
    found ?? (await call('POST', '/api/projects', projectBody(HISTORY_NAME, { complianceThreshold: 0.99, maxReviewCycles: CYCLES })))
  const id = project.id
  let s = await state(id)
  if (s.phase === 'refinement') {
    if (!(await latest(id, 'project_request'))) {
      await call('POST', `/api/projects/${id}/refinement/run`, { answers: [] })
    }
    await approveDraft(id, 'project_request')
    await call('POST', `/api/projects/${id}/phase/advance`, {})
    s = await state(id)
  }
  if (s.phase === 'specification') {
    if ((await latest(id, 'technical_specification'))?.status !== 'locked') {
      await approveDraft(id, 'technical_specification')
    }
    await call('POST', `/api/projects/${id}/phase/advance`, {})
    await approveGate(id, 'spec_approval') // advisory: spec_approval is gated, plan_approval isn't
    await until('planning', async () => (await state(id)).phase === 'planning')
    s = await state(id)
  }
  if (s.phase === 'planning') {
    if ((await latest(id, 'implementation_plan'))?.status !== 'locked') {
      await approveDraft(id, 'implementation_plan')
    }
    await call('POST', `/api/projects/${id}/phase/advance`, {})
    await until('execution', async () => (await state(id)).phase === 'execution')
    s = await state(id)
  }
  if (s.phase === 'execution' && s.review.cycle === 0) {
    const started = await raw('POST', `/api/projects/${id}/execution/start`)
    if (![200, 202, 409].includes(started.status)) {
      throw new Error(`execution/start → ${started.status} ${await started.text()}`)
    }
  }
  // Each cycle: review → review_required (0.86 / 0.93 < 0.99) → approve the one-step optimisation plan → execution
  // re-enters → the next review. When the Fake's flat score escalates (no progress, §4.5), the escalation gate is
  // approved with `continue` (FR-14: one more cycle). Stops at CYCLES, or when the loop can't go on.
  const t0 = performance.now()
  for (;;) {
    s = await until('a review decision', async () => {
      const x = await state(id)
      return x.phase === 'review' && x.review.latestDecision !== null ? x : undefined
    }, 300_000)
    const cycle = s.review.cycle
    process.stdout.write(`  cycle ${cycle}: ${s.review.latestDecision} (${((performance.now() - t0) / 1000).toFixed(0)} s)
`)
    if (cycle >= CYCLES) {
      break
    }
    if (s.review.latestDecision === 'escalation_required') {
      const gate = (await call('GET', `/api/projects/${id}/gates`)).find((g) => g.status === 'pending' && g.type === 'escalation')
      if (!gate) {
        break
      }
      await call('POST', `/api/projects/${id}/gates/${gate.id}/approve`, { decision: 'continue', comment: 'NFR-PERF2 seed: one more cycle' })
    } else if (s.review.latestDecision !== 'review_required') {
      break
    }
    // review_required, or continue after an escalation: the optimisation plan (if one is drafted) needs approval.
    const next = await until(`cycle ${cycle + 1} or an optimisation plan draft`, async () => {
      const x = await state(id)
      if (x.review.cycle > cycle) {
        return 'next'
      }
      return (await latest(id, 'optimization_plan'))?.status === 'draft' ? 'draft' : undefined
    }, 300_000).catch(() => 'stuck')
    if (next === 'stuck') {
      break
    }
    if (next === 'draft') {
      await approveDraft(id, 'optimization_plan')
      const moved = await until(`cycle ${cycle + 1}`, async () => (await state(id)).review.cycle > cycle, 300_000).catch(() => false)
      if (!moved) {
        break
      }
    }
  }
  return id
}

// ---- Latency --------------------------------------------------------------------------------------------------------

const percentile = (sorted, p) => sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)]

async function measure(name, path, concurrency) {
  for (let i = 0; i < 5; i++) {
    await call('GET', path) // warm-up (JIT, connection pool, EF query cache)
  }
  const samples = []
  let errors = 0
  await pool(Array.from({ length: REQUESTS }, (_, i) => i), concurrency, async () => {
    const start = performance.now()
    const response = await raw('GET', path)
    await response.arrayBuffer()
    if (response.ok) {
      samples.push(performance.now() - start)
    } else {
      errors++
    }
  })
  samples.sort((a, b) => a - b)
  return { name, concurrency, n: samples.length, errors, p50: percentile(samples, 50), p95: percentile(samples, 95), max: samples.at(-1) }
}

const fmt = (ms) => (ms === undefined ? '—' : ms.toFixed(1))

async function run() {
  const t0 = performance.now()
  const count = await seedProjects()
  console.log(`Seed: ${count} NFR-PERF2 projects (${((performance.now() - t0) / 1000).toFixed(1)} s)`)
  const historyId = await seedHistory()
  const runs = await call('GET', `/api/projects/${historyId}/execution/runs`)
  const reports = await call('GET', `/api/projects/${historyId}/compliance/reports`)
  const steps = runs.reduce((n, r) => n + (r.steps?.length ?? 0), 0)
  const listPage = await call('GET', '/api/projects?page=1&limit=50')
  const st = await state(historyId)
  console.log(
    `History project ${historyId}: phase ${st.phase}, ${reports.length} review cycles, ${runs.length} runs, ${steps} steps (target 10 cycles × 30 steps)`,
  )
  console.log(`Project list page 1: ${listPage.items.length} rows (of ${listPage.total ?? '?'})`)

  const endpoints = [
    ['GET /api/projects?page=1&limit=50', '/api/projects?page=1&limit=50'],
    ['GET /api/projects/{id}/execution/runs', `/api/projects/${historyId}/execution/runs`],
    ['GET /api/projects/{id}/compliance/reports', `/api/projects/${historyId}/compliance/reports`],
  ]
  const rows = []
  for (const concurrency of [1, CONCURRENCY]) {
    for (const [name, path] of endpoints) {
      rows.push(await measure(name, path, concurrency))
    }
  }
  console.log(`\nRead latency (ms), ${REQUESTS} requests per endpoint, target p95 < ${P95_TARGET_MS} ms`)
  console.log('| Endpoint | Clients | n | errors | p50 | p95 | max | p95 OK |')
  console.log('| --- | --- | --- | --- | --- | --- | --- | --- |')
  for (const r of rows) {
    console.log(`| ${r.name} | ${r.concurrency} | ${r.n} | ${r.errors} | ${fmt(r.p50)} | ${fmt(r.p95)} | ${fmt(r.max)} | ${r.p95 < P95_TARGET_MS && r.errors === 0 ? 'yes' : 'NO'} |`)
  }
  const ok = rows.every((r) => r.p95 < P95_TARGET_MS && r.errors === 0)
  console.log(`\nNFR-PERF2: ${ok ? 'PASS' : 'FAIL'}`)
  process.exitCode = ok ? 0 : 1
}

async function main() {
  console.log(`NFR-PERF2 against ${BASE} as ${EMAIL}`)
  const credentials = await bearerToken()
  token = credentials.token
  try {
    await run()
  } finally {
    await credentials.revoke()
  }
}

main().catch((error) => {
  console.error(error)
  process.exitCode = 2
})
