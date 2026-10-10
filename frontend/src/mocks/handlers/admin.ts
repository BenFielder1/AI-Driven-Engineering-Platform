import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { antiforgeryFailure, authDb, currentUser, nextId, unauthenticated } from './auth'
import { problem } from './problem'

type AdminEvent = Schemas['AdminEventDto']
type AdminRun = Schemas['AdminAgentRunDto']
type DeadLetter = Schemas['DeadLetterDto']

/** Mirrors the admin endpoints' MaxLimit. */
const maxPageLimit = 100
const eventTypePattern = /^[A-Z][A-Z0-9_]*$/

/** In-memory admin backend for tests (reset after each test by src/test/setup.ts). */
export const adminDb = {
  events: [] as AdminEvent[],
  runs: [] as AdminRun[],
  deadLetters: [] as DeadLetter[],
  /** Projects with an execution job queued or running (a retried execution dead letter → 409 CONFLICT). */
  activeExecutionProjects: [] as string[],
  /** Account creation times by user id (UserDto has none); unknown users get a fixed time. */
  userCreatedAt: {} as Record<string, string>,
  /** Every admin list request URL, in order (for asserting the filters sent). */
  requests: [] as URL[],
}

export function resetAdminDb() {
  adminDb.events = []
  adminDb.runs = []
  adminDb.deadLetters = []
  adminDb.activeExecutionProjects = []
  adminDb.userCreatedAt = {}
  adminDb.requests = []
}

let clock = Date.parse('2026-09-10T10:00:00Z')
/** Each seeded record is a minute newer than the previous one, so the last seeded is listed first. */
function tick(): string {
  clock += 60_000
  return new Date(clock).toISOString()
}

export function seedAdminEvent(overrides: Partial<AdminEvent> = {}): AdminEvent {
  const event: AdminEvent = {
    id: nextId(),
    projectId: null,
    projectName: null,
    type: 'ARTIFACT_APPROVED',
    category: 'artifact',
    outcome: 'ok',
    actor: { id: null, name: 'aide-system', isSystem: true },
    summary: 'Technical specification v2 approved',
    payload: { type: 'technical_specification', version: 2 },
    createdAt: tick(),
    ...overrides,
  }
  adminDb.events.push(event)
  return event
}

export function seedAdminRun(overrides: Partial<AdminRun> & Pick<AdminRun, 'projectId' | 'projectName'>): AdminRun {
  const createdAt = tick()
  const run: AdminRun = {
    id: nextId(),
    agent: 'spec_generator',
    phase: 'specification',
    stepId: null,
    provider: 'anthropic',
    model: 'claude-opus-5-5',
    promptVersion: '1',
    status: 'succeeded',
    attempts: 1,
    inputTokens: 3_412,
    outputTokens: 9_876,
    costEstimate: 0.211,
    durationMs: 48_250,
    error: null,
    createdAt,
    completedAt: createdAt,
    ...overrides,
  }
  adminDb.runs.push(run)
  return run
}

export function seedDeadLetter(overrides: Partial<DeadLetter> = {}): DeadLetter {
  const at = tick()
  const item: DeadLetter = {
    id: nextId(),
    type: 'spec_generation',
    projectId: null,
    projectName: null,
    status: 'dead',
    attempts: 5,
    lastError: 'PROVIDER_ERROR: The model provider returned 529 (overloaded).',
    payload: { projectId: null },
    createdAt: at,
    deadLetteredAt: at,
    ...overrides,
  }
  adminDb.deadLetters.push(item)
  return item
}

/** 401 when signed out, 403 for non-admins, otherwise null. */
function adminOnly(): Response | null {
  const user = currentUser()
  if (!user) {
    return unauthenticated()
  }
  return user.systemRole === 'admin' ? null : problem(403, 'FORBIDDEN', 'Only administrators can do that.')
}

function positiveInt(value: string | null, fallback: number): number | null {
  if (value === null) {
    return fallback
  }
  const n = Number(value)
  return Number.isInteger(n) && n >= 1 ? n : null
}

/** Parses page/limit and records the request; a VALIDATION problem when they are out of range. */
function paging(request: Request, errors: Record<string, string[]>): { page: number; limit: number; q: URLSearchParams } | Response {
  const url = new URL(request.url)
  adminDb.requests.push(url)
  const q = url.searchParams
  const page = positiveInt(q.get('page'), 1)
  const limit = positiveInt(q.get('limit'), 20)
  if (page === null) {
    errors.page = ['page must be at least 1.']
  }
  if (limit === null || limit > maxPageLimit) {
    errors.limit = [`limit must be between 1 and ${String(maxPageLimit)}.`]
  }
  if (page === null || limit === null || Object.keys(errors).length > 0) {
    return problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors })
  }
  return { page, limit, q }
}

function pageOf<T>(items: T[], page: number, limit: number) {
  return { items: items.slice((page - 1) * limit, page * limit), page, limit, total: items.length }
}

const newestFirst = (x: { createdAt: string; id: string }, y: { createdAt: string; id: string }) =>
  y.createdAt.localeCompare(x.createdAt) || y.id.localeCompare(x.id)

function userCreatedAt(id: string): string {
  return adminDb.userCreatedAt[id] ?? '2026-09-01T09:00:00.000Z'
}

export const adminHandlers = [
  // FR-51: the cross-project event log.
  http.get('*/api/admin/events', ({ request }) => {
    const denied = adminOnly()
    if (denied) {
      return denied
    }
    const errors: Record<string, string[]> = {}
    const q0 = new URL(request.url).searchParams
    const type = q0.get('type')
    const from = q0.get('from')
    const to = q0.get('to')
    if (type !== null && !eventTypePattern.test(type)) {
      errors.type = ['type must be an event type such as RUN_FAILED.']
    }
    if (from !== null && to !== null && Date.parse(from) > Date.parse(to)) {
      errors.from = ['from must not be after to.']
    }
    const p = paging(request, errors)
    if (p instanceof Response) {
      return p
    }
    const { q } = p
    const items = adminDb.events
      .filter((e) => !q.get('projectId') || e.projectId === q.get('projectId'))
      .filter((e) => type === null || e.type === type)
      .filter((e) => !q.get('category') || e.category === q.get('category'))
      .filter((e) => !q.get('outcome') || e.outcome === q.get('outcome'))
      .filter((e) => from === null || e.createdAt >= new Date(from).toISOString())
      .filter((e) => to === null || e.createdAt <= new Date(to).toISOString())
      .sort(newestFirst)
    return HttpResponse.json<Schemas['PagedResultOfAdminEventDto']>(pageOf(items, p.page, p.limit))
  }),

  // FR-51, FR-23: agent runs of every project.
  http.get('*/api/admin/runs', ({ request }) => {
    const denied = adminOnly()
    if (denied) {
      return denied
    }
    const p = paging(request, {})
    if (p instanceof Response) {
      return p
    }
    const { q } = p
    const match = (key: 'projectId' | 'agent' | 'status' | 'provider' | 'model') => (r: AdminRun) =>
      !q.get(key) || r[key] === q.get(key)
    const items = adminDb.runs
      .filter(match('projectId'))
      .filter(match('agent'))
      .filter(match('status'))
      .filter(match('provider'))
      .filter(match('model'))
      .sort(newestFirst)
    return HttpResponse.json<Schemas['PagedResultOfAdminAgentRunDto']>(pageOf(items, p.page, p.limit))
  }),

  // FR-51: dead letters (retried ones only with includeRetried=true).
  http.get('*/api/admin/dead-letters', ({ request }) => {
    const denied = adminOnly()
    if (denied) {
      return denied
    }
    const p = paging(request, {})
    if (p instanceof Response) {
      return p
    }
    const { q } = p
    const includeRetried = q.get('includeRetried') === 'true'
    const items = adminDb.deadLetters
      .filter((d) => d.status === 'dead' || (includeRetried && d.status === 'retried'))
      .filter((d) => !q.get('projectId') || d.projectId === q.get('projectId'))
      .filter((d) => !q.get('type') || d.type === q.get('type'))
      .sort((x, y) => (y.deadLetteredAt ?? y.createdAt).localeCompare(x.deadLetteredAt ?? x.createdAt) || y.id.localeCompare(x.id))
    return HttpResponse.json<Schemas['PagedResultOfDeadLetterDto']>(pageOf(items, p.page, p.limit))
  }),

  // FR-51: re-enqueue a dead letter's job.
  http.post('*/api/admin/dead-letters/:jobId/retry', ({ request, params }) => {
    const denied = adminOnly() ?? antiforgeryFailure(request)
    if (denied) {
      return denied
    }
    const dead = adminDb.deadLetters.find((d) => d.id === params.jobId)
    if (dead?.status !== 'dead') {
      return problem(404, 'NOT_FOUND', 'The dead letter was not found.')
    }
    if (dead.type === 'execution' && dead.projectId && adminDb.activeExecutionProjects.includes(dead.projectId)) {
      return problem(409, 'CONFLICT', 'An execution job is already queued or running for this project.')
    }
    dead.status = 'retried'
    const now = new Date().toISOString()
    const job: Schemas['JobDto'] = {
      id: nextId(),
      type: dead.type,
      projectId: dead.projectId,
      status: 'queued',
      attempts: 0,
      runAfter: now,
      createdAt: now,
    }
    seedAdminEvent({
      projectId: dead.projectId,
      projectName: dead.projectName,
      type: 'DEAD_LETTER_RETRIED',
      category: null,
      summary: `Dead letter ${dead.type} retried`,
      payload: { deadJobId: dead.id, jobId: job.id, jobType: dead.type },
      createdAt: now,
    })
    return HttpResponse.json<Schemas['JobDto']>(job)
  }),

  // FR-3: users, newest first, searched by email or name.
  http.get('*/api/admin/users', ({ request }) => {
    const denied = adminOnly()
    if (denied) {
      return denied
    }
    const p = paging(request, {})
    if (p instanceof Response) {
      return p
    }
    const search = (p.q.get('search') ?? '').trim().toLowerCase()
    const items: Schemas['AdminUserDto'][] = authDb.users
      .filter((u) => !search || u.email.toLowerCase().includes(search) || u.name.toLowerCase().includes(search))
      .map(({ id, email, name, systemRole, avatarUrl, githubLinked }) => ({
        id,
        email,
        name,
        systemRole,
        avatarUrl,
        githubLinked,
        createdAt: userCreatedAt(id),
      }))
      .sort(newestFirst)
    return HttpResponse.json<Schemas['PagedResultOfAdminUserDto']>(pageOf(items, p.page, p.limit))
  }),

  // FR-3: change a system role; the last Admin can't be demoted.
  http.patch('*/api/admin/users/:id/role', async ({ request, params }) => {
    const denied = adminOnly() ?? antiforgeryFailure(request)
    if (denied) {
      return denied
    }
    const body = (await request.json()) as Schemas['ChangeSystemRoleRequest']
    const user = authDb.users.find((u) => u.id === params.id)
    if (!user) {
      return problem(404, 'NOT_FOUND', 'The user was not found.')
    }
    const admins = authDb.users.filter((u) => u.systemRole === 'admin')
    if (user.systemRole === 'admin' && body.systemRole === 'user' && admins.length === 1) {
      return problem(409, 'CONFLICT', 'The last Admin cannot be demoted.')
    }
    user.systemRole = body.systemRole
    const { id, email, name, systemRole, avatarUrl, githubLinked } = user
    return HttpResponse.json<Schemas['UserDto']>({ id, email, name, systemRole, avatarUrl, githubLinked })
  }),
]
