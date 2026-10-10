#!/usr/bin/env node
// NFR-PERF (SPEC §8): "API p95 < 300 ms for reads with 50 projects and 1,000 artifacts locally (excluding agent calls).
// Live events reach browsers within 2 s."
//
// 1. Seeds (idempotently) one clearly named perf user with 50 projects and 20 human project-request draft versions
//    each (= 1,000 artifact versions) through the public API. No agent runs, no LLM calls.
// 2. Measures p50/p95/p99/max of the main read endpoints over N requests each (default 200), sequentially and with
//    parallel clients.
// 3. Measures live-event latency: time from a mutation (a human draft) to the SignalR `ARTIFACT_CREATED` projectEvent for
//    the artifact that request created (matched by artifact id), with the
//    @microsoft/signalr client the frontend already depends on (bearer token, so no browser Origin is needed).
//
// Usage (Node 24; run from the repo root after `npm --prefix frontend ci`):
//   node scripts/perf/nfr-perf.mjs                       # against http://localhost:8080 (Docker Desktop Kubernetes)
//   BASE_URL=http://localhost:5173 node scripts/perf/nfr-perf.mjs
// Options (env): REQUESTS=200 CONCURRENCY=8 EVENTS=30 PERF_EMAIL=nfr-perf@perf.aide.local PERF_PASSWORD=…
// NOTE: this creates data in the target database (the perf user and its 50 "NFR-PERF …" projects). Re-runs reuse them.
// The per-run personal API token is revoked when the run ends.

import { createRequire } from 'node:module'
import { performance } from 'node:perf_hooks'

const require = createRequire(new URL('../../frontend/package.json', import.meta.url))
const signalR = require('@microsoft/signalr')

const BASE = (process.env.BASE_URL ?? 'http://localhost:8080').replace(/\/$/, '')
const REQUESTS = Number(process.env.REQUESTS ?? 200)
const CONCURRENCY = Number(process.env.CONCURRENCY ?? 8)
const EVENTS = Number(process.env.EVENTS ?? 30)
const PROJECTS = 50
const VERSIONS_PER_PROJECT = 20
const EMAIL = process.env.PERF_EMAIL ?? 'nfr-perf@perf.aide.local'
const PASSWORD = process.env.PERF_PASSWORD ?? 'nfr-perf-password-1'
const P95_TARGET_MS = 300
const EVENT_TARGET_MS = 2000

// ---- HTTP helpers ---------------------------------------------------------------------------------------------------

/**
 * Signs in (or registers) with cookies, then creates a personal API token: everything after that uses Bearer auth.
 * Returns the token and a `revoke()` that deletes it again (token management is browser-session only, so it keeps
 * the session cookies and XSRF header for that).
 */
async function bearerToken() {
  const send = (method, path, body, headers = {}) =>
    fetch(BASE + path, {
      method,
      headers: { ...(body === undefined ? {} : { 'content-type': 'application/json' }), ...headers },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  let response = await send('POST', '/api/auth/login', { email: EMAIL, password: PASSWORD })
  if (response.status === 401) {
    response = await send('POST', '/api/auth/register', { email: EMAIL, name: 'NFR-PERF load user', password: PASSWORD })
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
    { name: `nfr-perf ${new Date().toISOString()}`, expiresAt: new Date(Date.now() + 86_400_000).toISOString() },
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

async function call(method, path, body) {
  const response = await fetch(BASE + path, {
    method,
    headers: { authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'content-type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  const text = await response.text()
  if (!response.ok) {
    throw new Error(`${method} ${path} → ${response.status} ${text.slice(0, 300)}`)
  }
  return text ? JSON.parse(text) : null
}

async function pool(items, size, fn) {
  const queue = [...items]
  const workers = Array.from({ length: Math.min(size, queue.length) }, async () => {
    while (queue.length > 0) {
      await fn(queue.shift())
    }
  })
  await Promise.all(workers)
}

// ---- Seed -----------------------------------------------------------------------------------------------------------

async function listPerfProjects() {
  const page = await call('GET', '/api/projects?page=1&limit=100&search=NFR-PERF')
  return page.items.filter((p) => p.name.startsWith('NFR-PERF '))
}

async function seed() {
  let projects = await listPerfProjects()
  const missing = PROJECTS - projects.length
  if (missing > 0) {
    await pool(Array.from({ length: missing }, (_, i) => projects.length + i + 1), 8, async (n) => {
      await call('POST', '/api/projects', {
        name: `NFR-PERF project ${String(n).padStart(2, '0')}`,
        idea: 'Seeded by scripts/perf/nfr-perf.mjs to measure read latency. Safe to delete.',
        approvalMode: 'advisory',
        complianceThreshold: 0.9,
        maxFilesPerStep: 20,
        maxReviewCycles: 3,
        tokenBudget: null,
        git: { provider: 'local', repoUrl: null, defaultBranch: 'main', token: null },
      })
    })
    projects = await listPerfProjects()
  }
  let created = 0
  await pool(projects, 8, async (p) => {
    const history = await call('GET', `/api/projects/${p.id}/artifacts/project_request/history`).catch(() => [])
    let latest = history.reduce((max, a) => Math.max(max, a.version), 0)
    for (let v = latest + 1; v <= VERSIONS_PER_PROJECT; v++) {
      await call('POST', `/api/projects/${p.id}/artifacts/project_request`, {
        content: `# ${p.name}\n\n## Summary\nHuman draft v${v} seeded for NFR-PERF.\n\n${'- Requirement line\n'.repeat(40)}`,
        baseVersion: latest === 0 ? null : latest,
      })
      latest = v
      created++
    }
  })
  return { projects, created }
}

// ---- Read latency ---------------------------------------------------------------------------------------------------

const percentile = (sorted, p) => sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)]

async function measure(name, pathFor, concurrency) {
  for (let i = 0; i < 10; i++) {
    await call('GET', pathFor(i)) // warm-up (JIT, connection pool, EF query cache)
  }
  const samples = []
  let errors = 0
  await pool(Array.from({ length: REQUESTS }, (_, i) => i), concurrency, async (i) => {
    const start = performance.now()
    try {
      await call('GET', pathFor(i))
      samples.push(performance.now() - start)
    } catch {
      errors++
    }
  })
  samples.sort((a, b) => a - b)
  return {
    endpoint: name,
    concurrency,
    n: samples.length,
    errors,
    p50: percentile(samples, 50),
    p95: percentile(samples, 95),
    p99: percentile(samples, 99),
    max: samples[samples.length - 1],
  }
}

// ---- Live-event latency ---------------------------------------------------------------------------------------------

/** A separate project for event timing (not one of the 50), so the read data set stays fixed. */
async function eventsProject() {
  const existing = (await call('GET', '/api/projects?page=1&limit=100&search=NFR-PERF-EVENTS')).items.find((p) => p.name === 'NFR-PERF-EVENTS')
  return (
    existing ??
    (await call('POST', '/api/projects', {
      name: 'NFR-PERF-EVENTS',
      idea: 'Live-event latency target for scripts/perf/nfr-perf.mjs. Safe to delete.',
      approvalMode: 'advisory',
      complianceThreshold: 0.9,
      maxFilesPerStep: 20,
      maxReviewCycles: 3,
      tokenBudget: null,
      git: { provider: 'local', repoUrl: null, defaultBranch: 'main', token: null },
    }))
  )
}

/**
 * Time from sending a mutation (a human project-request draft) to the `ARTIFACT_CREATED` projectEvent for exactly the
 * artifact that POST created. A draft after the first also emits ARTIFACT_SUPERSEDED for the previous draft, and an
 * event can arrive before the POST response does, so every event is buffered by artifact id and matched afterwards;
 * stray or trailing events from earlier iterations can't resolve a later sample.
 */
async function measureEvents(projectId) {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${BASE}/api/hubs/projects`, { accessTokenFactory: () => token, transport: signalR.HttpTransportType.WebSockets, skipNegotiation: true })
    .configureLogging(signalR.LogLevel.Warning)
    .build()
  /** artifactId → arrival time of its ARTIFACT_CREATED event. */
  const arrived = new Map()
  /** artifactId → resolver of the sample waiting for it. */
  const waiters = new Map()
  connection.on('projectEvent', (message) => {
    if (message.projectId !== projectId || message.type !== 'ARTIFACT_CREATED') {
      return
    }
    const artifactId = message.payload?.artifactId
    const at = performance.now()
    arrived.set(artifactId, at)
    waiters.get(artifactId)?.(at)
  })
  await connection.start()
  await connection.invoke('Join', projectId)
  const latencies = []
  let missed = 0
  try {
    for (let i = 0; i < EVENTS; i++) {
      const start = performance.now()
      const artifact = await call('POST', `/api/projects/${projectId}/artifacts/project_request`, {
        content: `# NFR-PERF-EVENTS

Draft ${i} at ${new Date().toISOString()}
`,
        baseVersion: null,
      })
      let timer
      const at =
        arrived.get(artifact.id) ??
        (await new Promise((resolve) => {
          waiters.set(artifact.id, resolve)
          timer = setTimeout(() => resolve(null), 10_000)
        }))
      clearTimeout(timer)
      waiters.delete(artifact.id)
      if (at === null) {
        missed++
      } else {
        latencies.push(at - start)
      }
    }
  } finally {
    await connection.stop()
  }
  latencies.sort((a, b) => a - b)
  return { n: latencies.length, missed, types: 'ARTIFACT_CREATED (matched by artifact id)', p50: percentile(latencies, 50), p95: percentile(latencies, 95), max: latencies[latencies.length - 1] }
}

// ---- Main -----------------------------------------------------------------------------------------------------------

const fmt = (ms) => (ms === undefined ? '—' : `${ms.toFixed(1)}`)

async function main() {
  console.log(`NFR-PERF against ${BASE} as ${EMAIL}`)
  const credentials = await bearerToken()
  token = credentials.token
  try {
    await run()
  } finally {
    await credentials.revoke()
  }
}

async function run() {
  const t0 = performance.now()
  const { projects, created } = await seed()
  const ids = projects.map((p) => p.id)
  const totalVersions = (
    await Promise.all(ids.map((id) => call('GET', `/api/projects/${id}/artifacts/project_request/history`)))
  ).reduce((n, h) => n + h.length, 0)
  console.log(
    `Seed: ${projects.length} projects, ${totalVersions} project_request versions (${created} created now, ${((performance.now() - t0) / 1000).toFixed(1)} s)`,
  )

  const pick = (i) => ids[i % ids.length]
  const endpoints = [
    ['GET /api/projects (page 1, 20)', () => '/api/projects?page=1&limit=20&sortBy=updated&sortOrder=desc'],
    ['GET /api/projects?search=', () => '/api/projects?page=1&limit=20&search=project%201'],
    ['GET /api/projects/:id', (i) => `/api/projects/${pick(i)}`],
    ['GET /api/projects/:id/state', (i) => `/api/projects/${pick(i)}/state`],
    ['GET …/artifacts', (i) => `/api/projects/${pick(i)}/artifacts`],
    ['GET …/artifacts/:type/latest', (i) => `/api/projects/${pick(i)}/artifacts/project_request/latest`],
    ['GET …/artifacts/:type/history', (i) => `/api/projects/${pick(i)}/artifacts/project_request/history`],
    ['GET …/activity', (i) => `/api/projects/${pick(i)}/activity`],
    ['GET …/runs', (i) => `/api/projects/${pick(i)}/runs`],
    ['GET …/gates', (i) => `/api/projects/${pick(i)}/gates`],
  ]
  const rows = []
  for (const concurrency of [1, CONCURRENCY]) {
    for (const [name, path] of endpoints) {
      rows.push(await measure(name, path, concurrency))
    }
  }

  console.log(`\nRead latency (ms), ${REQUESTS} requests per endpoint, target p95 < ${P95_TARGET_MS} ms`)
  console.log('| Endpoint | Clients | n | errors | p50 | p95 | p99 | max | p95 OK |')
  console.log('| --- | --- | --- | --- | --- | --- | --- | --- | --- |')
  for (const r of rows) {
    console.log(`| ${r.endpoint} | ${r.concurrency} | ${r.n} | ${r.errors} | ${fmt(r.p50)} | ${fmt(r.p95)} | ${fmt(r.p99)} | ${fmt(r.max)} | ${r.p95 < P95_TARGET_MS && r.errors === 0 ? 'yes' : 'NO'} |`)
  }

  const events = await measureEvents((await eventsProject()).id)
  console.log(`\nLive events (POST …/artifacts/project_request → SignalR projectEvent: ${events.types}), target < ${EVENT_TARGET_MS} ms`)
  console.log('| Samples | missed | p50 | p95 | max | OK |')
  console.log('| --- | --- | --- | --- | --- | --- |')
  console.log(`| ${events.n} | ${events.missed} | ${fmt(events.p50)} | ${fmt(events.p95)} | ${fmt(events.max)} | ${events.missed === 0 && events.max < EVENT_TARGET_MS ? 'yes' : 'NO'} |`)

  const ok = rows.every((r) => r.p95 < P95_TARGET_MS && r.errors === 0) && events.missed === 0 && events.max < EVENT_TARGET_MS
  console.log(`\nNFR-PERF: ${ok ? 'PASS' : 'FAIL'}`)
  process.exitCode = ok ? 0 : 1
}

main().catch((error) => {
  console.error(error)
  process.exitCode = 2
})
