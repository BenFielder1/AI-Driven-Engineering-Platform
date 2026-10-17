import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { checkTagPrefix } from '../../features/projects/settings/release-settings-rules'
import { compareSemVer, normalizeVersion, parseSemVer } from '../../features/versions/semver'
import type { ProjectRole } from '../../lib/permissions'
import { antiforgeryFailure, authDb, currentUser, nextId, unauthenticated } from './auth'
import { githubDb } from './github'
import { problem } from './problem'

type Project = Schemas['ProjectDto']
type Summary = Schemas['ProjectSummaryDto']
type State = Schemas['ProjectStateDto']
type Member = Schemas['MemberDto']
type GitSettings = Schemas['GitSettingsDto']
type LlmSettings = Schemas['LlmSettingsDto']
type ProviderStatus = Schemas['LlmProviderStatusDto']
type Channel = Schemas['NotificationChannelDto']
type ReleaseVersion = Schemas['ReleaseVersionDto']

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

export interface StoredProject extends Omit<Project, 'myRole' | 'releaseVersionId' | 'releasedVersion' | 'developmentVersion'> {
  members: StoredMember[]
  /**
   * Release versions in any order (FR-62); the project's released, development and current versions are derived from
   * them (§4.10). Seeded with one version (0.1.0: in development, or released for a released project).
   */
  releaseVersions: ReleaseVersion[]
  /**
   * FR-70: the Repository Analyst's state per release version id (GET /refinement `codebaseSummary`); a version with a
   * `codebase_summary` artifact reads `completed`, one without an entry `not_needed`.
   */
  analyst?: Record<string, Schemas['CodebaseSummaryStatusDto']>
  /** Tags that already exist in the repository: starting a version whose tag is here answers TAG_EXISTS (FR-61). */
  repoTags?: string[]
  git: GitSettings
  /** `canBind` is per caller (derived from the role); `binding` is set by mocks/handlers/connections.ts. */
  llm: Omit<LlmSettings, 'providers' | 'canBind'>
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
  /** FR-57: attempt history per execution step id, oldest first; see mocks/handlers/execution.ts. */
  stepAttempts?: Record<string, Schemas['StepAttemptDto'][]>
  /** FR-57: the review cycle whose optimisation plan an artifact id is (review-fix runs). */
  planCycles?: Record<string, number>
  /** FR-57: why a plan's run is superseded, by plan artifact id (default copy when unset). */
  supersededReasons?: Record<string, string>
  /**
   * Set once execution was started or resumed (the mock's stand-in for the runner's queued work; FR-31). Left unset,
   * an in_progress or rejected step implies queued work; `false` means no job is queued (an interrupted runner).
   */
  executionStarted?: boolean
  /** The recorded pull request and its branch comparison (FR-36); unset or null until opened. See mocks/handlers/execution.ts. */
  pullRequest?: Schemas['PullRequestDto'] | null
  /** A pull request job is queued or running (FR-36): POST /git/pull-request sets it and refuses with 409 CONFLICT while set. */
  pullRequestJobQueued?: boolean
  /** Compliance reports, oldest first (FR-38, FR-41); see mocks/handlers/review.ts. */
  complianceReports?: StoredComplianceReport[]
  /** Phase recorded at each checkpoint of the current release version (rollback restores it, §4.8, §4.10). */
  phaseAtVersion: Record<number, Project['currentPhase']>
  /** Release attempts, oldest first (FR-42, FR-43); see mocks/handlers/release.ts. */
  releases?: Schemas['ReleaseDto'][]
  /** Recorded rollbacks, oldest first (FR-44); see mocks/handlers/release.ts. */
  rollbacks?: Schemas['RollbackDto'][]
  /** When the last rollback ran (epoch ms), for the one-per-minute limit (FR-45). */
  lastRollbackAt?: number
  /** A step or agent run is in progress: POST /rollback answers 409 ROLLBACK_BLOCKED (FR-45). */
  rollbackBlocked?: boolean
  /** FR-84: the last merge-readiness result (GET /git/merge-readiness); unset derives a mergeable one. */
  mergeReadiness?: Schemas['MergeReadinessDto']
  /** FR-84: what the next `refresh=true` check returns (then becomes `mergeReadiness`). */
  mergeReadinessOnRefresh?: Schemas['MergeReadinessDto']
}

/** A refinement round as stored: the round DTO plus the questions its run asked (null until it succeeds). */
export type StoredRefinementRound = Schemas['RefinementRoundDto'] & {
  questions: Omit<Schemas['RefinementQuestionDto'], 'answer'>[] | null
}

/** An execution step and the plan version it belongs to (and its preset specialist, FR-57/FR-76). */
export type StoredExecutionStep = Schemas['ExecutionStepDto'] & {
  planArtifactId: string
  agentKey?: string | null
}

/** A compliance report plus the v2 detail fields (FR-56) that the v1 DTO lacks. */
export type StoredComplianceReport = Schemas['ComplianceReportDto'] & {
  regressions?: Schemas['RegressionDto'][]
  externalChecks?: Schemas['ExternalChecksDto'] | null
}

/** A request the mock API received (for assertions such as "the token was sent but never shown"). */
export interface RecordedRequest {
  method: string
  path: string
  body: unknown
}

/** v2 default (FR-79): system keys aren't allowed, so keyed providers are configured only by a bound connection. */
const defaultProviders = (): ProviderStatus[] => [
  { provider: 'anthropic', configured: false, defaultModel: 'claude-opus-5-5' },
  { provider: 'openai', configured: false, defaultModel: 'gpt-6-astra' },
  { provider: 'ollama', configured: true, defaultModel: 'qwen2.5-coder' },
  { provider: 'fake', configured: true, defaultModel: 'fake-1' },
]

/**
 * Git settings as stored (FR-82, FR-83): defaults to a PAT project on acme/inventory without a token. `mode` follows
 * `provider`; `canSwitchToApp` is derived per caller by `gitDto`.
 */
export function gitSettings(overrides: Partial<GitSettings> = {}): GitSettings {
  const provider = overrides.mode ?? overrides.provider ?? 'github_pat'
  const app = provider === 'github_app'
  const repoFullName = overrides.repoFullName ?? (app ? 'acme/inventory' : null)
  return {
    repoUrl: provider === 'local' ? null : `https://github.com/${repoFullName ?? 'acme/inventory'}`,
    defaultBranch: 'main',
    branchName: null,
    hasToken: false,
    installationId: app ? 9001 : null,
    repoId: app ? 4242 : null,
    appInstalledOnRepo: null,
    canSwitchToApp: false,
    mergeStrategy: 'aide_merges',
    tagPrefix: 'v',
    ...overrides,
    repoFullName,
    provider,
    mode: provider,
  }
}

/** GET/PATCH /settings/git body: "Switch to GitHub App" is offered to Owners of a PAT project the App can access. */
export function gitDto(project: StoredProject, isOwner: boolean): GitSettings {
  const g = project.git
  return { ...g, canSwitchToApp: isOwner && g.mode === 'github_pat' && g.appInstalledOnRepo === true }
}

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
  review: { cycle: 0, maxCycles: 3, latestScore: null, latestDecision: null, driftLevel: null, status: 'idle' },
})

/** A release version row for seeds (FR-62). */
export function makeReleaseVersion(overrides: Partial<ReleaseVersion> & Pick<ReleaseVersion, 'version'>): ReleaseVersion {
  const status = overrides.status ?? 'in_development'
  const released = status === 'released' || status === 'imported'
  return {
    id: nextId(),
    status,
    tag: released ? `v${overrides.version}` : null,
    tagPrefix: 'v',
    baseTag: null,
    baseCommitSha: null,
    mergeCommitSha: released ? 'abc1234def5678' : null,
    branchName: `aide/project/v${overrides.version}`,
    pullRequestUrl: null,
    checkpoints: { current: 1, max: 1 },
    presetId: null,
    presetVersion: null,
    request: 'Track stock levels across warehouses.',
    startedBy: { id: null, name: 'Ada Owner', isSystem: false },
    createdAt: '2026-09-01T10:00:00Z',
    releasedAt: released ? '2026-09-10T10:00:00Z' : null,
    ...overrides,
  }
}

const byVersionDesc = (a: ReleaseVersion, b: ReleaseVersion) => {
  const pa = parseSemVer(a.version)
  const pb = parseSemVer(b.version)
  return pa && pb ? compareSemVer(pb, pa) : b.version.localeCompare(a.version)
}

/** §4.10: the version in development or awaiting merge, if any. */
export function developmentVersionOf(p: StoredProject): ReleaseVersion | undefined {
  return p.releaseVersions.find((v) => v.status === 'in_development' || v.status === 'awaiting_merge')
}

/** §4.10: the highest released or imported version, if any. */
export function releasedVersionOf(p: StoredProject): ReleaseVersion | undefined {
  return [...p.releaseVersions].filter((v) => v.status === 'released' || v.status === 'imported').sort(byVersionDesc)[0]
}

/** The release version the lifecycle state belongs to: the one in development, else the latest released. */
export function currentVersionOf(p: StoredProject): ReleaseVersion | undefined {
  return developmentVersionOf(p) ?? releasedVersionOf(p)
}

/** Every release version, highest first (GET /versions). */
export function releaseVersionsOf(p: StoredProject): ReleaseVersion[] {
  return [...p.releaseVersions].sort(byVersionDesc).map((v) => (v === currentVersionOf(p) ? { ...v, checkpoints: { ...v.checkpoints, current: p.checkpoint } } : v))
}

/** The current release version's id ('' when the project has none). */
export const currentVersionId = (p: StoredProject) => currentVersionOf(p)?.id ?? ''

/** The semver of one of the project's release versions ('' when unknown), e.g. ArtifactDto.releaseVersion (FR-59). */
export const releaseVersionName = (p: StoredProject, releaseVersionId: string | null) =>
  p.releaseVersions.find((v) => v.id === releaseVersionId)?.version ?? ''

/** FR-60: the next minor of the released version, else 0.1.0. */
export function suggestedNextVersion(p: StoredProject): string {
  const released = releasedVersionOf(p)
  const parsed = released ? parseSemVer(released.version) : null
  return parsed ? `${String(parsed.major)}.${String(parsed.minor + 1n)}.0` : '0.1.0'
}

/**
 * Seeds a project. `members` defaults to the signed-in user as Owner; pass `[]` for a project the user can't see.
 * Without `releaseVersions`, the project gets version 0.1.0: released when `status` is `released`, else in development.
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
    checkpoint: 1,
    approvalMode: 'advisory',
    complianceThreshold: 0.9,
    maxFilesPerStep: 20,
    maxReviewCycles: 3,
    tokenBudget: null,
    tokensUsed: 0,
    executionPaused: false,
    createdAt: now,
    updatedAt: now,
    git: gitSettings(),
    llm: { provider: 'anthropic', defaultModel: 'claude-opus-5-5', modelsByAgent: {}, hasApiKey: false, binding: null },
    channels: [],
    state: emptyState(),
    gates: [],
    events: [],
    artifacts: [],
    runs: [],
    refinementRounds: [],
    executionSteps: [],
    phaseAtVersion: {},
    releaseVersions: [],
    ...rest,
    members: (members ?? (owner ? [{ userId: owner, role: 'owner' as const }] : [])).map((m) => ({
      ...m,
      createdAt: now,
    })),
  }
  if (!rest.releaseVersions) {
    project.releaseVersions = [
      makeReleaseVersion({
        version: '0.1.0',
        status: project.status === 'released' ? 'released' : 'in_development',
        checkpoints: { current: project.checkpoint, max: project.checkpoint },
        request: project.idea,
      }),
    ]
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
    checkpoint: p.checkpoint,
    releaseVersionId: currentVersionOf(p)?.id ?? null,
    releasedVersion: releasedVersionOf(p)?.version ?? null,
    developmentVersion: developmentVersionOf(p)?.version ?? null,
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
  const { id, name, slug, currentPhase, status, checkpoint, approvalMode, createdAt, updatedAt } = project
  return {
    id,
    name,
    slug,
    currentPhase,
    status,
    checkpoint,
    releasedVersion: releasedVersionOf(project)?.version ?? null,
    developmentVersion: developmentVersionOf(project)?.version ?? null,
    approvalMode,
    createdAt,
    updatedAt,
    myRole: roleOf(project, userId),
  }
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

/** FR-79: a provider is configured for a project by an allowed system key or a bound connection of that provider. */
export function llmDto(project: StoredProject, canBind: boolean): LlmSettings {
  const binding = project.llm.binding
  return {
    ...project.llm,
    hasApiKey: binding !== null,
    canBind,
    providers: projectsDb.providers.map((p) => ({ ...p, configured: p.configured || binding?.provider === p.provider })),
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
      // FR-47, NFR-SEC: webhook bodies are always signed, so a webhook needs a sent or kept signing secret.
      stored.secret = sent ?? (keepable ? (existing.secret ?? null) : null)
      if (sent === null && !keepable) {
        fail('secret', 'Enter a signing secret: webhook bodies are always HMAC-signed.')
      }
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

/**
 * ExecutionRunStatus as the backend derives it from the current plan's counts (blocked > completed > paused >
 * running > idle); `executionStarted` stands in for the runner's queued work between two steps (FR-31, FR-37).
 */
export function executionRunStatus(p: StoredProject): Schemas['ExecutionRunStatus'] {
  const c = p.state.execution
  if (c.blocked > 0) {
    return 'blocked'
  }
  if (c.total > 0 && c.completed === c.total) {
    return 'completed'
  }
  if (p.executionPaused) {
    return 'paused'
  }
  return (p.executionStarted ?? (c.inProgress > 0 || c.rejected > 0)) ? 'running' : 'idle'
}

/** FR-36: /state's pull request summary, or null until one is recorded. */
function pullRequestSummary(pr: Schemas['PullRequestDto'] | null | undefined): Schemas['PullRequestSummaryDto'] | null {
  if (!pr) {
    return null
  }
  const { provider, baseBranch, headBranch, number, url, openedAt } = pr
  return { provider, baseBranch, headBranch, number, url, openedAt }
}

/** GET /state for a stored project; `pendingGates` comes from its gates. */
export function stateDto(p: StoredProject): State {
  return {
    projectId: p.id,
    phase: p.currentPhase,
    status: p.status,
    checkpoint: p.checkpoint,
    releaseVersionId: currentVersionOf(p)?.id ?? null,
    releasedVersion: releasedVersionOf(p)?.version ?? null,
    developmentVersion: developmentVersionOf(p)?.version ?? null,
    suggestedNextVersion: suggestedNextVersion(p),
    executionPaused: p.executionPaused,
    executionStatus: executionRunStatus(p),
    ...p.state,
    pendingGates: p.gates
      .filter((g) => g.status === 'pending')
      .map(({ id, type, createdAt, overdue }) => ({ id, type, createdAt, overdue })),
    tokensUsed: p.tokensUsed,
    tokenBudget: p.tokenBudget,
    pullRequest: pullRequestSummary(p.pullRequest),
    latestRelease: latestReleaseSummary(p.releases),
  }
}

/** FR-42, FR-43: /state's latest release attempt, or null before the first. */
function latestReleaseSummary(releases: Schemas['ReleaseDto'][] | undefined): Schemas['ReleaseSummaryDto'] | null {
  const latest = releases?.at(-1)
  if (!latest) {
    return null
  }
  const { id, releaseVersionId, checkpoint, status, tag, startedAt } = latest
  const { pullRequestUrl, githubReleaseUrl, awaitingMergeSince, mergeReasons, mergeStrategy } = latest
  return {
    id,
    releaseVersionId,
    checkpoint,
    status,
    tag,
    startedAt,
    pullRequestUrl,
    githubReleaseUrl,
    awaitingMergeSince,
    mergeReasons,
    mergeStrategy,
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

/** FR-66: the initial version (default 0.1.0) is the first release version, in development. */
function scratchVersions(body: Schemas['CreateProjectRequest']): ReleaseVersion[] | Response {
  const initialVersion = body.initialVersion ? normalizeVersion(body.initialVersion) : '0.1.0'
  if (!initialVersion) {
    return problem(400, 'VERSION_INVALID', `'${body.initialVersion ?? ''}' is not a valid version.`)
  }
  return [makeReleaseVersion({ version: initialVersion, request: body.idea })]
}

/**
 * FR-73 import (the API answers 501 until it is built; this follows the contract): the released tag must be in the
 * GitHub App repo's refs (TAG_NOT_FOUND) and a version (VERSION_INVALID); the version to develop must be valid and
 * greater (VERSION_NOT_GREATER). Creates an `imported` row for the released tag plus the version `in_development`.
 */
function importVersions(body: Schemas['CreateProjectRequest']): ReleaseVersion[] | Response {
  const releasedTag = body.import?.releasedTag ?? null
  let released: string | null = null
  if (releasedTag !== null) {
    const refs = body.git.repoFullName ? githubDb.refs[body.git.repoFullName] : undefined
    const tag = refs?.tags.find((t) => t.name === releasedTag)
    if (refs && !tag && !refs.releases.some((r) => r.tagName === releasedTag)) {
      return problem(400, 'TAG_NOT_FOUND', `The tag '${releasedTag}' was not found in the repository.`)
    }
    released = normalizeVersion(tag ? tag.semver : releasedTag)
    if (!released) {
      return problem(400, 'VERSION_INVALID', `The tag '${releasedTag}' is not a semantic version.`)
    }
  }
  const text = body.import?.developmentVersion ?? ''
  const development = normalizeVersion(text)
  if (!development) {
    return problem(400, 'VERSION_INVALID', `'${text}' is not a valid version.`)
  }
  const [dev, base] = [parseSemVer(development), released ? parseSemVer(released) : null]
  if (dev && base && compareSemVer(dev, base) <= 0) {
    return problem(409, 'VERSION_NOT_GREATER', `${development} is not greater than the released version ${released ?? ''}.`)
  }
  const baseCommitSha = 'c0ffee1234567890c0ffee1234567890c0ffee12'
  return [
    ...(released
      ? [makeReleaseVersion({ version: released, status: 'imported', tag: releasedTag, mergeCommitSha: baseCommitSha, request: '' })]
      : []),
    makeReleaseVersion({ version: development, request: body.idea, baseTag: releasedTag, baseCommitSha }),
  ]
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
    const hasDevelopment = q.get('hasDevelopmentVersion')
    const sortBy = q.get('sortBy') ?? 'updated'
    const dir = q.get('sortOrder') === 'asc' ? 1 : -1
    const items = projectsDb.projects
      .filter((p) => user.systemRole === 'admin' || roleOf(p, user.id))
      .filter((p) => !search || p.name.toLowerCase().includes(search))
      .filter((p) => !phase || p.currentPhase === phase)
      .filter((p) => !status || p.status === status)
      .filter((p) => hasDevelopment === null || (developmentVersionOf(p) !== undefined) === (hasDevelopment === 'true'))
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
    const mode = body.git.mode ?? body.git.provider
    if (!mode) {
      errors['git.mode'] = ['Choose github_app, github_pat or local.']
    }
    if (mode === 'github_pat' && !body.git.repoUrl) {
      errors['git.repoUrl'] = ['A repository URL is required for GitHub.']
    }
    if (mode === 'github_app' && (!body.git.installationId || !body.git.repoFullName)) {
      errors['git.repoFullName'] = ['Pick a repository the AIDE GitHub App is installed on.']
    }
    const importing = body.source === 'import'
    if (importing && !body.import) {
      errors.import = ['Say which version is released and which to develop.']
    }
    if (importing && mode === 'local' && !body.git.localSourcePath?.trim()) {
      errors['git.localSourcePath'] = ['Enter the path of the repository to import.']
    }
    if (Object.keys(errors).length > 0) {
      return validation(errors)
    }
    const versions = importing ? importVersions(body) : scratchVersions(body)
    if (versions instanceof Response) {
      return versions
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
      git: gitSettings({
        provider: mode ?? 'local',
        ...(mode === 'github_pat' ? { repoUrl: body.git.repoUrl ?? null } : {}),
        ...(mode === 'github_app'
          ? { repoFullName: body.git.repoFullName ?? null, installationId: body.git.installationId ?? null }
          : {}),
        defaultBranch: body.git.defaultBranch ?? 'main',
        hasToken: mode === 'github_pat' && Boolean(body.git.token),
      }),
      members: [{ userId: user.id, role: 'owner' }],
      releaseVersions: versions,
    })
    // FR-70: an import queues the Repository Analyst for the version to develop.
    const development = developmentVersionOf(project)
    if (importing && development) {
      project.analyst = { [development.id]: { status: 'queued', runId: null, artifactId: null, artifactVersion: null } }
    }
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
      git: gitDto(a.project, isOwnerLevel(a)),
      llm: llmDto(a.project, a.level === 'owner'),
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
    const provider = body.provider ?? git.provider
    // FR-82/FR-83: only "Switch to GitHub App" moves a project onto the App.
    if (provider === 'github_app' && git.provider !== 'github_app') {
      return validation({ provider: ['Use Switch to GitHub App to move this project onto the AIDE GitHub App.'] })
    }
    git.provider = provider
    git.mode = provider
    if (provider === 'local') {
      git.repoUrl = null
    } else if (body.repoUrl) {
      git.repoUrl = body.repoUrl
    }
    if (provider !== 'github_app') {
      git.installationId = null
      git.repoId = null
      git.repoFullName = null
    }
    // §4.10, FR-64, FR-65: release settings. The prefix can't change while a release is awaiting merge or in progress.
    if (body.tagPrefix !== undefined && body.tagPrefix !== null && body.tagPrefix !== git.tagPrefix) {
      if (checkTagPrefix(body.tagPrefix) !== null) {
        return validation({
          tagPrefix: ['Use up to 20 letters, digits, ".", "_", "/" or "-", not starting with ".", "/" or "-".'],
        })
      }
      const releasing =
        developmentVersionOf(a.project)?.status === 'awaiting_merge' ||
        a.project.releases?.at(-1)?.status === 'in_progress' ||
        a.project.releases?.at(-1)?.status === 'awaiting_merge'
      if (releasing) {
        return problem(409, 'CONFLICT', 'The tag prefix can’t change while a release is in progress or awaiting its merge.')
      }
    }
    git.defaultBranch = body.defaultBranch ?? git.defaultBranch
    git.mergeStrategy = body.mergeStrategy ?? git.mergeStrategy
    git.tagPrefix = body.tagPrefix ?? git.tagPrefix
    if (body.clearToken) {
      git.hasToken = false
    } else if (body.token) {
      git.hasToken = true
    }
    return HttpResponse.json<GitSettings>(gitDto(a.project, isOwnerLevel(a)))
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
