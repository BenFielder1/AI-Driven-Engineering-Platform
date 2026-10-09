import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import type { ProjectRole } from '../../lib/permissions'
import { antiforgeryFailure, authDb, currentUser, nextId, unauthenticated } from './auth'
import { problem } from './problem'

type Project = Schemas['ProjectDto']
type Summary = Schemas['ProjectSummaryDto']
type State = Schemas['ProjectStateDto']
type Member = Schemas['MemberDto']
type GitSettings = Schemas['GitSettingsDto']
type LlmSettings = Schemas['LlmSettingsDto']
type ProviderStatus = Schemas['LlmProviderStatusDto']
type Channel = Schemas['NotificationChannelDto']

/**
 * A stored channel. `secret` is the webhook HMAC key, or for Slack/Teams the incoming-webhook URL (the API never
 * returns it: the DTO shows `hasSecret` and, for Slack/Teams, a masked target).
 */
export interface StoredChannel {
  id: string
  type: Channel['type']
  /** Email address or webhook URL; unused (empty) for Slack/Teams. */
  target: string
  secret: string | null
  events: Channel['events']
}

interface StoredMember {
  userId: string
  role: ProjectRole
  createdAt: string
}

export interface StoredProject extends Omit<Project, 'myRole'> {
  members: StoredMember[]
  git: GitSettings
  llm: Omit<LlmSettings, 'providers'>
  channels: StoredChannel[]
  /** `pendingGates` on /state is derived from `gates`. */
  state: Pick<State, 'artifacts' | 'execution' | 'review'>
  /** Gates, any status (FR-12–FR-15); see mocks/handlers/lifecycle.ts. */
  gates: Schemas['GateDto'][]
  /** Activity feed events in insertion order (FR-50). */
  events: Schemas['ActivityItemDto'][]
  /** Every artifact version, any type (FR-16); see mocks/handlers/artifacts.ts. */
  artifacts: Schemas['ArtifactDto'][]
  /** Agent runs, any order (the API lists newest first); see mocks/handlers/runs.ts (FR-23). */
  runs: Schemas['AgentRunDetailDto'][]
  /** Refinement rounds, oldest first, with the questions each completed round asked; see mocks/handlers/refinement.ts (FR-27). */
  refinementRounds: StoredRefinementRound[]
  /** Execution steps of approved plan versions (FR-30); see mocks/handlers/execution.ts. */
  executionSteps: StoredExecutionStep[]
  /** Phase recorded at each project version (rollback restores it, §4.8). */
  phaseAtVersion: Record<number, Project['currentPhase']>
}

/** A refinement round as stored: the round DTO plus the questions its run asked (null until it succeeds). */
export type StoredRefinementRound = Schemas['RefinementRoundDto'] & {
  questions: Omit<Schemas['RefinementQuestionDto'], 'answer'>[] | null
}

/** An execution step and the plan version it belongs to. */
export type StoredExecutionStep = Schemas['ExecutionStepDto'] & {
  planArtifactId: string
}

/** A request the mock API received (for assertions such as "the token was sent but never shown"). */
export interface RecordedRequest {
  method: string
  path: string
  body: unknown
}

const defaultProviders = (): ProviderStatus[] => [
  { provider: 'anthropic', configured: true, defaultModel: 'claude-opus-5-5' },
  { provider: 'openai', configured: false, defaultModel: 'gpt-6-astra' },
  { provider: 'ollama', configured: true, defaultModel: 'qwen2.5-coder' },
  { provider: 'fake', configured: true, defaultModel: 'fake-1' },
]

/** In-memory projects backend for tests (reset after each test by src/test/setup.ts). */
export const projectsDb = {
  projects: [] as StoredProject[],
  /** System-level provider status (a project key makes a provider configured for that project). */
  providers: defaultProviders(),
  gitTestResult: { ok: true, reachable: true, writable: true, defaultBranchExists: true, reason: null } as Schemas['GitTestResultDto'],
  requests: [] as RecordedRequest[],
}

export function resetProjectsDb() {
  projectsDb.projects = []
  projectsDb.providers = defaultProviders()
  projectsDb.gitTestResult = { ok: true, reachable: true, writable: true, defaultBranchExists: true, reason: null }
  projectsDb.requests = []
}

const emptyState = (): StoredProject['state'] => ({
  artifacts: [
    { type: 'project_request', latestVersion: null, latestStatus: null, currentLockedVersion: null },
    { type: 'technical_specification', latestVersion: null, latestStatus: null, currentLockedVersion: null },
    { type: 'implementation_plan', latestVersion: null, latestStatus: null, currentLockedVersion: null },
  ],
  execution: { total: 0, pending: 0, inProgress: 0, completed: 0, rejected: 0, blocked: 0 },
  review: { cycle: 0, maxCycles: 3, latestScore: null, latestDecision: null, driftLevel: null },
})

/**
 * Seeds a project. `members` defaults to the signed-in user as Owner; pass `[]` for a project the user can't see.
 */
export function seedProject(
  overrides: Partial<Omit<StoredProject, 'members'>> & { members?: { userId: string; role: ProjectRole }[] } = {},
): StoredProject {
  const now = '2026-09-01T10:00:00Z'
  const { members, ...rest } = overrides
  const owner = authDb.currentUserId
  const project: StoredProject = {
    id: nextId(),
    name: 'Inventory tracker',
    slug: 'inventory-tracker',
    idea: 'Track stock levels across warehouses.',
    currentPhase: 'refinement',
    status: 'active',
    projectVersion: 1,
    approvalMode: 'advisory',
    complianceThreshold: 0.9,
    maxFilesPerStep: 20,
    maxReviewCycles: 3,
    tokenBudget: null,
    tokensUsed: 0,
    executionPaused: false,
    createdAt: now,
    updatedAt: now,
    git: { provider: 'github', repoUrl: 'https://github.com/acme/inventory', defaultBranch: 'main', branchName: null, hasToken: false },
    llm: { provider: 'anthropic', defaultModel: 'claude-opus-5-5', modelsByAgent: {}, hasApiKey: false },
    channels: [],
    state: emptyState(),
    gates: [],
    events: [],
    artifacts: [],
    runs: [],
    refinementRounds: [],
    executionSteps: [],
    phaseAtVersion: {},
    ...rest,
    members: (members ?? (owner ? [{ userId: owner, role: 'owner' as const }] : [])).map((m) => ({
      ...m,
      createdAt: now,
    })),
  }
  projectsDb.projects.push(project)
  return project
}

function slugify(name: string) {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '')
}

export function roleOf(project: StoredProject, userId: string): ProjectRole | null {
  return project.members.find((m) => m.userId === userId)?.role ?? null
}

function toDto(project: StoredProject, userId: string): Project {
  const p = project
  return {
    id: p.id,
    name: p.name,
    slug: p.slug,
    idea: p.idea,
    currentPhase: p.currentPhase,
    status: p.status,
    projectVersion: p.projectVersion,
    approvalMode: p.approvalMode,
    complianceThreshold: p.complianceThreshold,
    maxFilesPerStep: p.maxFilesPerStep,
    maxReviewCycles: p.maxReviewCycles,
    tokenBudget: p.tokenBudget,
    tokensUsed: p.tokensUsed,
    executionPaused: p.executionPaused,
    myRole: roleOf(p, userId),
    createdAt: p.createdAt,
    updatedAt: p.updatedAt,
  }
}

function toSummary(project: StoredProject, userId: string): Summary {
  const { id, name, slug, currentPhase, status, projectVersion, approvalMode, createdAt, updatedAt } = project
  return { id, name, slug, currentPhase, status, projectVersion, approvalMode, createdAt, updatedAt, myRole: roleOf(project, userId) }
}

function toMember(m: StoredMember): Member {
  const user = authDb.users.find((u) => u.id === m.userId)
  return {
    userId: m.userId,
    email: user?.email ?? 'unknown@example.com',
    name: user?.name ?? 'Unknown',
    avatarUrl: null,
    role: m.role,
    createdAt: m.createdAt,
  }
}

function llmDto(project: StoredProject): LlmSettings {
  return {
    ...project.llm,
    providers: projectsDb.providers.map((p) => ({ ...p, configured: p.configured || project.llm.hasApiKey })),
  }
}

const urlIsSecret = (type: Channel['type']) => type === 'slack' || type === 'teams'

/** Mirrors the backend's MaskUrl: `host/…`. */
export function maskUrl(url: string): string {
  try {
    return `${new URL(url).host}/…`
  } catch {
    return url
  }
}

function channelDto(c: StoredChannel): Channel {
  return {
    id: c.id,
    type: c.type,
    target: urlIsSecret(c.type) ? maskUrl(c.secret ?? '') : c.target,
    hasSecret: c.secret !== null,
    events: [...c.events],
  }
}

const isHttpsUrl = (v: string | null | undefined): v is string => typeof v === 'string' && /^https:\/\/\S+$/.test(v.trim())

/** The notification rules of the API (backend ProjectSettingsRules.ValidateChannels / PlanChannel). */
function planChannels(
  input: Schemas['NotificationChannelInput'][],
  saved: StoredChannel[],
): { channels: StoredChannel[] } | { errors: Record<string, string[]> } {
  const errors: Record<string, string[]> = {}
  const channels = input.map((c, i): StoredChannel => {
    const field = `channels[${String(i)}]`
    const fail = (key: string, message: string) => {
      errors[`${field}.${key}`] = [message]
    }
    const existing = saved.find((e) => e.id === c.id)
    const keepable = !c.clearSecret && existing?.secret != null && existing.type === c.type
    const target = c.target?.trim() ?? ''
    const sent = c.secret ?? null
    if (sent !== null && c.clearSecret) {
      fail('secret', 'Send either secret or clearSecret, not both.')
    } else if (sent !== null && !sent.trim()) {
      fail('secret', "The secret can't be blank. Send clearSecret to remove it.")
    }
    const stored: StoredChannel = { id: existing?.id ?? nextId(), type: c.type, target, secret: null, events: c.events }
    if (c.type === 'email') {
      if (!/^\S+@\S+\.\S+$/.test(target)) {
        fail('target', 'Use an email address.')
      }
    } else if (c.type === 'webhook') {
      if (!isHttpsUrl(target)) {
        fail('target', 'Use an https:// URL.')
      }
      stored.secret = sent ?? (keepable ? (existing.secret ?? null) : null)
    } else {
      const label = c.type === 'slack' ? 'Slack' : 'Teams'
      stored.target = ''
      if (sent !== null) {
        if (!isHttpsUrl(sent)) {
          fail('secret', `Use the https:// incoming-webhook URL from ${label}.`)
        }
        stored.secret = sent.trim()
      } else if (/^[a-z]+:\/\//i.test(target)) {
        if (!isHttpsUrl(target)) {
          fail('target', 'Use an https:// URL.')
        }
        stored.secret = target
      } else if (keepable) {
        stored.secret = existing.secret
      } else {
        fail('secret', `Enter the ${label} incoming-webhook URL.`)
      }
    }
    return stored
  })
  return Object.keys(errors).length > 0 ? { errors } : { channels }
}

/** GET /state for a stored project; `pendingGates` comes from its gates. */
export function stateDto(p: StoredProject): State {
  return {
    projectId: p.id,
    phase: p.currentPhase,
    status: p.status,
    projectVersion: p.projectVersion,
    executionPaused: p.executionPaused,
    ...p.state,
    pendingGates: p.gates
      .filter((g) => g.status === 'pending')
      .map(({ id, type, createdAt, overdue }) => ({ id, type, createdAt, overdue })),
    tokensUsed: p.tokensUsed,
    tokenBudget: p.tokenBudget,
  }
}

export type Access = { project: StoredProject; userId: string; level: 'owner' | 'contributor' | 'viewer' | 'admin' } | Response

/** Member (or Admin) access to a project; 401 signed out, 404 unknown or not a member (SPEC §2). */
export function access(id: unknown): Access {
  const user = currentUser()
  if (!user) {
    return unauthenticated()
  }
  const project = projectsDb.projects.find((p) => p.id === id)
  const role = project ? roleOf(project, user.id) : null
  if (!project || (!role && user.systemRole !== 'admin')) {
    return problem(404, 'NOT_FOUND', 'The project was not found.')
  }
  return { project, userId: user.id, level: role ?? 'admin' }
}

export const forbidden = () => problem(403, 'FORBIDDEN', 'Only project Owners can do that.')
export const isOwnerLevel = (a: Exclude<Access, Response>) => a.level === 'owner' || a.level === 'admin'

/** Access check for owner-only mutations, including antiforgery. */
export function ownerMutation(request: Request, id: unknown): Access {
  const a = access(id)
  if (a instanceof Response) {
    return a
  }
  const rejected = antiforgeryFailure(request)
  if (rejected) {
    return rejected
  }
  return isOwnerLevel(a) ? a : forbidden()
}

export async function record(request: Request): Promise<unknown> {
  const text = await request.clone().text()
  const body: unknown = text ? JSON.parse(text) : null
  projectsDb.requests.push({ method: request.method, path: new URL(request.url).pathname, body })
  return body
}

const touch = (p: StoredProject) => {
  p.updatedAt = new Date().toISOString()
}

function validation(errors: Record<string, string[]>) {
  return problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors })
}

export const projectHandlers = [
  // ---- Projects ----
  http.get('*/api/projects', ({ request }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const q = new URL(request.url).searchParams
    const page = Number(q.get('page') ?? 1)
    const limit = Number(q.get('limit') ?? 20)
    const search = q.get('search')?.toLowerCase()
    const phase = q.get('phase')
    const status = q.get('status')
    const sortBy = q.get('sortBy') ?? 'updated'
    const dir = q.get('sortOrder') === 'asc' ? 1 : -1
    const items = projectsDb.projects
      .filter((p) => user.systemRole === 'admin' || roleOf(p, user.id))
      .filter((p) => !search || p.name.toLowerCase().includes(search))
      .filter((p) => !phase || p.currentPhase === phase)
      .filter((p) => !status || p.status === status)
      .sort((a, b) => {
        const key = sortBy === 'name' ? 'name' : sortBy === 'created' ? 'createdAt' : 'updatedAt'
        return a[key].localeCompare(b[key]) * dir
      })
    return HttpResponse.json<Schemas['PagedResultOfProjectSummaryDto']>({
      items: items.slice((page - 1) * limit, page * limit).map((p) => toSummary(p, user.id)),
      page,
      limit,
      total: items.length,
    })
  }),

  http.post('*/api/projects', async ({ request }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const body = (await record(request)) as Schemas['CreateProjectRequest']
    const errors: Record<string, string[]> = {}
    if (!body.name.trim()) {
      errors.name = ['The name is required.']
    }
    if (body.git.provider === 'github' && !body.git.repoUrl) {
      errors['git.repoUrl'] = ['A repository URL is required for GitHub.']
    }
    if (Object.keys(errors).length > 0) {
      return validation(errors)
    }
    const now = new Date().toISOString()
    const project = seedProject({
      name: body.name,
      slug: slugify(body.name),
      idea: body.idea,
      approvalMode: body.approvalMode ?? 'advisory',
      complianceThreshold: body.complianceThreshold ?? 0.9,
      maxFilesPerStep: body.maxFilesPerStep ?? 20,
      maxReviewCycles: body.maxReviewCycles ?? 3,
      tokenBudget: body.tokenBudget ?? null,
      createdAt: now,
      updatedAt: now,
      git: {
        provider: body.git.provider,
        repoUrl: body.git.repoUrl ?? null,
        defaultBranch: body.git.defaultBranch ?? 'main',
        branchName: null,
        hasToken: Boolean(body.git.token),
      },
      members: [{ userId: user.id, role: 'owner' }],
    })
    return HttpResponse.json<Project>(toDto(project, user.id), { status: 201 })
  }),

  http.get('*/api/projects/:id', ({ params }) => {
    const a = access(params.id)
    return a instanceof Response ? a : HttpResponse.json<Project>(toDto(a.project, a.userId))
  }),

  http.patch('*/api/projects/:id', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['UpdateProjectRequest']
    if (body.name?.trim() === '') {
      return validation({ name: ['The name is required.'] })
    }
    const p = a.project
    p.name = body.name ?? p.name
    p.approvalMode = body.approvalMode ?? p.approvalMode
    p.complianceThreshold = body.complianceThreshold ?? p.complianceThreshold
    p.maxFilesPerStep = body.maxFilesPerStep ?? p.maxFilesPerStep
    p.maxReviewCycles = body.maxReviewCycles ?? p.maxReviewCycles
    p.tokenBudget = body.clearTokenBudget ? null : (body.tokenBudget ?? p.tokenBudget)
    touch(p)
    return HttpResponse.json<Project>(toDto(p, a.userId))
  }),

  http.delete('*/api/projects/:id', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    projectsDb.projects = projectsDb.projects.filter((p) => p !== a.project)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get('*/api/projects/:id/state', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    return HttpResponse.json<State>(stateDto(a.project))
  }),

  // ---- Members (FR-8) ----
  http.get('*/api/projects/:id/members', ({ params }) => {
    const a = access(params.id)
    return a instanceof Response ? a : HttpResponse.json<Member[]>(a.project.members.map(toMember))
  }),

  http.post('*/api/projects/:id/members', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['AddMemberRequest']
    const user = authDb.users.find((u) => u.email.toLowerCase() === body.email.toLowerCase())
    if (!user) {
      return problem(404, 'USER_NOT_FOUND', 'No user with that email exists.')
    }
    if (roleOf(a.project, user.id)) {
      return problem(409, 'CONFLICT', 'The user is already a member.')
    }
    const member: StoredMember = { userId: user.id, role: body.role ?? 'viewer', createdAt: new Date().toISOString() }
    a.project.members.push(member)
    return HttpResponse.json<Member>(toMember(member), { status: 201 })
  }),

  http.patch('*/api/projects/:id/members/:userId', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['UpdateMemberRoleRequest']
    const member = a.project.members.find((m) => m.userId === params.userId)
    if (!member) {
      return problem(404, 'NOT_FOUND', 'The member was not found.')
    }
    const owners = a.project.members.filter((m) => m.role === 'owner').length
    if (member.role === 'owner' && body.role !== 'owner' && owners === 1) {
      return problem(409, 'LAST_OWNER', 'A project needs at least one Owner.')
    }
    member.role = body.role ?? member.role
    return HttpResponse.json<Member>(toMember(member))
  }),

  http.delete('*/api/projects/:id/members/:userId', async ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    // Owners remove anyone; any member may remove themselves (leave).
    if (!isOwnerLevel(a) && params.userId !== a.userId) {
      return forbidden()
    }
    await record(request)
    const member = a.project.members.find((m) => m.userId === params.userId)
    if (!member) {
      return problem(404, 'NOT_FOUND', 'The member was not found.')
    }
    if (member.role === 'owner' && a.project.members.filter((m) => m.role === 'owner').length === 1) {
      return problem(409, 'LAST_OWNER', 'A project needs at least one Owner.')
    }
    a.project.members = a.project.members.filter((m) => m !== member)
    return new HttpResponse(null, { status: 204 })
  }),

  // ---- Settings (FR-9, FR-10) ----
  http.get('*/api/projects/:id/settings', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    if (!isOwnerLevel(a)) {
      return forbidden()
    }
    return HttpResponse.json<Schemas['ProjectSettingsDto']>({
      git: a.project.git,
      llm: llmDto(a.project),
      notifications: { channels: a.project.channels.map(channelDto) },
    })
  }),

  http.patch('*/api/projects/:id/settings/git', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['UpdateGitSettingsRequest']
    const git = a.project.git
    git.provider = body.provider ?? git.provider
    git.repoUrl = body.repoUrl !== undefined ? body.repoUrl : git.repoUrl
    git.defaultBranch = body.defaultBranch ?? git.defaultBranch
    if (body.clearToken) {
      git.hasToken = false
    } else if (body.token) {
      git.hasToken = true
    }
    return HttpResponse.json<GitSettings>(git)
  }),

  http.patch('*/api/projects/:id/settings/llm', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['UpdateLlmSettingsRequest']
    const llm = a.project.llm
    llm.provider = body.provider ?? llm.provider
    llm.defaultModel = body.defaultModel ?? llm.defaultModel
    llm.modelsByAgent = body.modelsByAgent ?? llm.modelsByAgent
    if (body.clearApiKey) {
      llm.hasApiKey = false
    } else if (body.apiKey) {
      llm.hasApiKey = true
    }
    return HttpResponse.json<LlmSettings>(llmDto(a.project))
  }),

  http.patch('*/api/projects/:id/settings/notifications', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['UpdateNotificationSettingsRequest']
    const plan = planChannels(body.channels, a.project.channels)
    if ('errors' in plan) {
      return problem(400, 'VALIDATION', 'The notification settings are invalid.', { errors: plan.errors })
    }
    a.project.channels = plan.channels
    return HttpResponse.json<Schemas['NotificationSettingsDto']>({ channels: a.project.channels.map(channelDto) })
  }),

  http.post('*/api/projects/:id/settings/git/test', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    return HttpResponse.json<Schemas['GitTestResultDto']>(projectsDb.gitTestResult)
  }),
]
