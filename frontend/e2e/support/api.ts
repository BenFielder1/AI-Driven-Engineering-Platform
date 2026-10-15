import { open, rm, stat } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { expect, test, type APIResponse, type BrowserContext, type Page } from '@playwright/test'

/**
 * E2E helpers that talk to the real API through the browser context's request client, so cookies (session and the
 * NFR-SEC `XSRF-TOKEN` pair) are shared with the context's pages. Every test creates its own users and projects.
 */

export const password = 'e2e-password1'

export function uniqueEmail(prefix: string): string {
  return `e2e-${prefix}-${String(Date.now())}-${String(Math.floor(Math.random() * 1e6))}@example.com`
}

/**
 * Register is rate limited per IP: 5 per fixed 1-minute window (AuthSetup.PerIpPerMinute), answering 429 with
 * Retry-After. With fullyParallel workers (and CI retries) a run makes more registrations than one window allows, and
 * uncoordinated callers can keep losing the race. So every registration in the run, API or UI, goes through one
 * cross-worker lock (an exclusive lock file per target URL): the holder registers, waiting out a 429 with Retry-After
 * while it holds the lock, then releases it. Throughput is the limiter's 5/min and every caller is served in turn.
 */
const REGISTER_ATTEMPTS = 6
/** One holder waits at most one window (~61 s) on a 429; a lock older than this belongs to a dead worker. */
const STALE_LOCK_MS = 3 * 60_000
/**
 * Time budget for one registration, added to the test timeout: queueing behind the other workers (a run makes ~11
 * registrations = 3 windows; a CI retry adds its own) plus the holder's own Retry-After wait. Generous on purpose;
 * it only matters when something is wrong.
 */
export const REGISTER_BUDGET_MS = 6 * 60_000
const LOCK_WAIT_MS = REGISTER_BUDGET_MS - 90_000

function lockPath(): string {
  const target = (process.env.BASE_URL ?? 'http://localhost:5173').replace(/[^a-z0-9]+/gi, '_')
  return join(tmpdir(), `aide-e2e-register-${target}.lock`)
}

const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

/** Runs `fn` while holding the cross-worker register lock (see above). */
async function withRegisterLock<T>(fn: () => Promise<T>): Promise<T> {
  const path = lockPath()
  const deadline = Date.now() + LOCK_WAIT_MS
  for (;;) {
    try {
      const handle = await open(path, 'wx')
      await handle.writeFile(`${String(process.pid)} ${new Date().toISOString()}`)
      await handle.close()
      break
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'EEXIST') {
        throw error
      }
      const age = await stat(path).then(
        (s) => Date.now() - s.mtimeMs,
        () => 0,
      )
      if (age > STALE_LOCK_MS) {
        await rm(path, { force: true })
        continue
      }
      if (Date.now() > deadline) {
        throw new Error(`waited ${String(LOCK_WAIT_MS / 1000)} s for the register lock ${path}`, { cause: error })
      }
      // Lock poll (another worker is registering), not a fixed wait for app state.
      await pause(200)
    }
  }
  try {
    return await fn()
  } finally {
    await rm(path, { force: true })
  }
}

/** Seconds from a 429's Retry-After (+1 s margin), in ms. */
function retryAfterMs(header: string | undefined): number {
  return (Math.max(1, Number(header ?? '10') || 10) + 1) * 1000
}

export interface TestUser {
  id: string
  email: string
  name: string
}

/**
 * POST /api/auth/register in `context`: the context is signed in as the new user afterwards. Use this wherever the
 * register page itself isn't under test. In a test, the queue and rate-limit waits extend the test's timeout by
 * REGISTER_BUDGET_MS; in a hook (`inHook`), the hook must set a timeout that covers them, because `testInfo.timeout`
 * there reports the test timeout, not the hook's.
 */
export async function registerViaApi(
  context: BrowserContext,
  prefix: string,
  name = `E2E ${prefix}`,
  { inHook = false }: { inHook?: boolean } = {},
): Promise<TestUser> {
  const email = uniqueEmail(prefix)
  if (!inHook) {
    test.info().setTimeout(test.info().timeout + REGISTER_BUDGET_MS)
  }
  return withRegisterLock(async () => {
    for (let attempt = 1; attempt <= REGISTER_ATTEMPTS; attempt++) {
      const response = await context.request.post('/api/auth/register', { data: { email, name, password } })
      if (response.status() === 429) {
        // Server-directed back-off until the rate-limit window resets.
        await pause(retryAfterMs(response.headers()['retry-after']))
        continue
      }
      expect(response.status(), await response.text()).toBe(201)
      const user = (await response.json()) as { id: string }
      return { id: user.id, email, name }
    }
    throw new Error(`register stayed rate limited after ${String(REGISTER_ATTEMPTS)} attempts`)
  })
}

/**
 * Registers through the /register page — only for tests where the register form is what's under test (FR-1 in
 * auth.spec.ts). Takes the same register lock as registerViaApi; a 429 is waited out (Retry-After) and the form
 * re-submitted.
 */
export async function registerViaUi(page: Page, prefix: string, name = `E2E ${prefix}`): Promise<TestUser> {
  const email = uniqueEmail(prefix)
  test.info().setTimeout(test.info().timeout + REGISTER_BUDGET_MS)
  await page.goto('/register')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Name', { exact: true }).fill(name)
  await page.getByLabel(/^Password/).fill(password)
  const user = await withRegisterLock(async () => {
    for (let attempt = 1; attempt <= REGISTER_ATTEMPTS; attempt++) {
      const answered = page.waitForResponse((r) => r.url().endsWith('/api/auth/register') && r.request().method() === 'POST')
      await page.getByRole('button', { name: 'Create account' }).click()
      const response = await answered
      if (response.status() === 429) {
        await expect(page.getByRole('button', { name: 'Create account' })).toBeEnabled()
        await pause(retryAfterMs(response.headers()['retry-after']))
        continue
      }
      expect(response.status(), await response.text()).toBe(201)
      return (await response.json()) as { id: string }
    }
    throw new Error(`register stayed rate limited after ${String(REGISTER_ATTEMPTS)} attempts`)
  })
  await expect(page.getByRole('heading', { level: 1, name: 'Projects', exact: true })).toBeVisible()
  return { id: user.id, email, name }
}

async function xsrfHeader(context: BrowserContext): Promise<Record<string, string>> {
  const cookie = (await context.cookies()).find((c) => c.name === 'XSRF-TOKEN')
  if (!cookie) {
    await context.request.get('/api/auth/antiforgery')
    return xsrfHeader(context)
  }
  return { 'X-XSRF-TOKEN': decodeURIComponent(cookie.value) }
}

/** A JSON API call as the context's user (antiforgery header added for mutations). */
export async function api(
  context: BrowserContext,
  method: 'GET' | 'POST' | 'PATCH' | 'PUT' | 'DELETE',
  path: string,
  data?: unknown,
): Promise<APIResponse> {
  const headers = method === 'GET' ? {} : await xsrfHeader(context)
  return context.request.fetch(path, { method, headers, data: data ?? (method === 'GET' ? undefined : {}) })
}

export async function apiJson<T>(context: BrowserContext, method: Parameters<typeof api>[1], path: string, data?: unknown) {
  const response = await api(context, method, path, data)
  expect(response.ok(), `${method} ${path} → ${String(response.status())} ${await response.text()}`).toBe(true)
  return (await response.json()) as T
}

export type ApprovalMode = 'strict' | 'advisory' | 'autonomous'

export interface ProjectRef {
  id: string
  name: string
}

/** POST /api/projects with the Local Git provider (no keys configured → the Fake LLM provider). */
export async function createProjectViaApi(
  context: BrowserContext,
  name: string,
  approvalMode: ApprovalMode = 'strict',
): Promise<ProjectRef> {
  const project = await apiJson<{ id: string; name: string }>(context, 'POST', '/api/projects', {
    name,
    idea: `${name}: an API that lets small teams track their work items.`,
    approvalMode,
    complianceThreshold: 0.9,
    maxFilesPerStep: 20,
    maxReviewCycles: 3,
    tokenBudget: null,
    git: { provider: 'local', repoUrl: null, defaultBranch: 'main', token: null },
  })
  return { id: project.id, name: project.name }
}

export async function addMember(context: BrowserContext, projectId: string, email: string, role: 'owner' | 'contributor' | 'viewer') {
  await apiJson(context, 'POST', `/api/projects/${projectId}/members`, { email, role })
}

interface ArtifactDto {
  id: string
  version: number
  status: string
}

/** Polls `GET …/artifacts/:type/latest` until the latest version is a draft (agent output arrives via a background job). */
export async function waitForDraft(context: BrowserContext, projectId: string, type: string, timeout = 60_000) {
  let draft: ArtifactDto | null = null
  await expect
    .poll(
      async () => {
        const response = await api(context, 'GET', `/api/projects/${projectId}/artifacts/${type}/latest`)
        if (!response.ok()) {
          return 'none'
        }
        const a = (await response.json()) as ArtifactDto
        draft = a.status === 'draft' ? a : null
        return a.status
      },
      { timeout, intervals: [250, 500, 1000] },
    )
    .toBe('draft')
  return draft as unknown as ArtifactDto
}

interface Gate {
  id: string
  type: string
  status: string
}

export async function waitForPendingGate(context: BrowserContext, projectId: string, type: string, timeout = 60_000) {
  let gate: Gate | undefined
  await expect
    .poll(
      async () => {
        const gates = await apiJson<Gate[]>(context, 'GET', `/api/projects/${projectId}/gates`)
        gate = gates.find((g) => g.status === 'pending' && g.type === type)
        return gate !== undefined
      },
      { timeout, intervals: [250, 500, 1000] },
    )
    .toBe(true)
  return gate as unknown as Gate
}

interface ProjectStateDto {
  phase: string
  status: string
  checkpoint: number
  executionStatus: string
  review: { cycle: number; latestDecision: string | null; status: string }
}

export async function projectState(context: BrowserContext, projectId: string) {
  return apiJson<ProjectStateDto>(context, 'GET', `/api/projects/${projectId}/state`)
}

/**
 * Drives a Fake + Local project through the API (no UI) up to `target`. Used to give a11y and permission specs real
 * content; the full UI journey lives in lifecycle.spec.ts. Assumes strict mode (spec, plan and pre-release gates).
 */
export async function driveProject(
  context: BrowserContext,
  projectId: string,
  target: 'specification' | 'planning' | 'execution' | 'review' | 'released',
) {
  const approveDraft = async (type: string) => {
    const draft = await waitForDraft(context, projectId, type)
    await apiJson(context, 'POST', `/api/projects/${projectId}/artifacts/${draft.id}/approve`, {})
  }
  const approveGate = async (type: string) => {
    const gate = await waitForPendingGate(context, projectId, type)
    await apiJson(context, 'POST', `/api/projects/${projectId}/gates/${gate.id}/approve`, {})
  }

  await apiJson(context, 'POST', `/api/projects/${projectId}/refinement/run`, { answers: [] })
  await approveDraft('project_request')
  await apiJson(context, 'POST', `/api/projects/${projectId}/phase/advance`, {})
  if (target === 'specification') {
    await waitForDraft(context, projectId, 'technical_specification')
    return
  }
  await approveDraft('technical_specification')
  await apiJson(context, 'POST', `/api/projects/${projectId}/phase/advance`, {})
  await approveGate('spec_approval')
  if (target === 'planning') {
    await waitForDraft(context, projectId, 'implementation_plan')
    return
  }
  await approveDraft('implementation_plan')
  await apiJson(context, 'POST', `/api/projects/${projectId}/phase/advance`, {})
  await approveGate('plan_approval')
  if (target === 'execution') {
    return
  }
  await apiJson(context, 'POST', `/api/projects/${projectId}/execution/start`)
  await expect
    .poll(async () => (await projectState(context, projectId)).phase, { timeout: 120_000, intervals: [500, 1000] })
    .toBe('review')
  // Fake review: cycle 1 → review_required with a one-step optimisation plan, cycle 2 → release_eligible.
  await expect
    .poll(async () => (await projectState(context, projectId)).review.latestDecision, { timeout: 120_000 })
    .not.toBeNull()
  if ((await projectState(context, projectId)).review.latestDecision === 'review_required') {
    await approveDraft('optimization_plan')
  }
  if (target === 'review') {
    await waitForPendingGate(context, projectId, 'pre_release', 180_000)
    return
  }
  await approveGate('pre_release')
  await expect
    .poll(async () => (await projectState(context, projectId)).status, { timeout: 120_000, intervals: [500, 1000] })
    .toBe('released')
}
