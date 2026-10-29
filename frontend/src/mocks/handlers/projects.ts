import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { checkTagPrefix } from '../../features/projects/settings/release-settings-rules'
import { compareSemVer, normalizeVersion, parseSemVer } from '../../features/versions/semver'
import type { ProjectRole } from '../../lib/permissions'
import { antiforgeryFailure, authDb, currentUser, nextId, unauthenticated } from './auth'
import { bindCredential, connectionsDb } from './connections'
import { applyVercelSetup } from './deployments'
import { githubDb } from './github'
import { bitbucketDb, bitbucketRefsOf, findBitbucketConnection, findBitbucketRepo } from './bitbucket'
import { findGitLabConnection, findGitLabRepo, gitlabDb, gitlabRefsOf } from './gitlab'
import { checkCreateLlm, isLlmProviderEnabled } from './llm'
import { problem } from './problem'
import { packsOf, validateComposition } from './stack-catalog'

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

export interface StoredProject extends Omit<
    Project,
    | 'myRole'
    | 'releaseVersionId'
    | 'releasedVersion'
    | 'developmentVersion'
    | 'presetId'
    | 'presetName'
    | 'presetVersion'
    | 'deploymentsEnabled'
  > {
  members: StoredMember[]
  /**
   * FR-117 Settings → Stack: the composition pinned on the current release version (unset or null: none), and the seed
   * knobs for its lock and stack change; see mocks/handlers/stack.ts.
   */
  stack?: StoredStack
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
  /** FR-140: AIDE's release notes per release attempt id (GET …/releases/:releaseId/notes; missing = `null`). */
  releaseNotes?: Record<string, string | null>
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
  /** FR-88: Settings → Deployment as stored (unset: never configured); see mocks/handlers/deployments.ts. */
  deployment?: StoredDeploymentSettings
  /** FR-88: environment variables with their values (never returned); see mocks/handlers/deployments.ts. */
  deploymentEnv?: StoredEnvVar[]
  /** FR-90, FR-91: deployments, any order (the API lists newest first); see mocks/handlers/deployments.ts. */
  deployments?: Schemas['DeploymentDto'][]
}

export interface StoredStack {
  composition?: Schemas['StackCompositionDto'] | null
  catalogVersion?: string
  /** `null`: a composition written by the FR-129 preset migration, not re-pinned yet. */
  catalogHash?: string | null
  planLocked?: boolean
  baseHasCode?: boolean
  layersChanged?: string[]
  legacyPreset?: Schemas['LegacyPresetDto'] | null
}

/** §4.17 R5: the VCS option of the project's repository host (Local: none, any VCS). */
export function repoVcsOfProject(p: StoredProject): string | null {
  return repoVcsOfMode(p.git.mode)
}

/** §4.17 R5: the VCS option fixed by a repository host (Local: any). */
function repoVcsOfMode(mode: Schemas['GitProvider'] | undefined): string | null {
  return !mode || mode === 'local' ? null : mode === 'gitlab' ? 'vcs-gitlab' : mode === 'bitbucket' ? 'vcs-bitbucket' : 'vcs-github'
}

/** FR-88: the stored deployment settings; `present` on required env and `canBind` are derived per request. */
export interface StoredDeploymentSettings {
  provider: Schemas['DeploymentProvider'] | null
  /** The preset supports Vercel (an Owner can opt in otherwise by setting a provider). */
  supported: boolean
  binding: Schemas['DeploymentBindingDto'] | null
  vercelProject: Schemas['VercelProjectDto'] | null
  rootDirectory: string
  framework: string
  requiredEnv: Omit<Schemas['RequiredEnvVarDto'], 'present'>[]
  gitConnected: boolean | null
  installUrl: string | null
}

/** An env var with its plaintext value (the mock's stand-in for the encrypted column). */
export type StoredEnvVar = Schemas['DeploymentEnvVarDto'] & { value: string }

/** A refinement round as stored: the round DTO plus the questions its run asked (null until it succeeds). */
export type StoredRefinementRound = Schemas['RefinementRoundDto'] & {
  questions: Omit<Schemas['RefinementQuestionDto'], 'answer'>[] | null
}

/** An execution step and the plan version it belongs to (and its preset specialist, FR-57/FR-76). */
export type StoredExecutionStep = Schemas['ExecutionStepDto'] & {
  planArtifactId: string
  agentKey?: string | null
  /** FR-121, FR-129: the retired v2 preset agent the step was planned for (it ran as `agentKey`). */
  legacyAgentKey?: string | null
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
    repoUrl:
      provider === 'local' || provider === 'gitlab' || provider === 'bitbucket'
        ? null
        : `https://github.com/${repoFullName ?? 'acme/inventory'}`,
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

/**
 * GET/PATCH /settings/git body: "Switch to GitHub App" is offered to Owners of a PAT project the App can access;
 * Rebind (FR-136) to Owners of a GitLab or Bitbucket project.
 */
export function gitDto(project: StoredProject, isOwner: boolean): GitSettings {
  const g = project.git
  return {
    ...g,
    canSwitchToApp: isOwner && g.mode === 'github_pat' && g.appInstalledOnRepo === true,
    canRebind: isOwner && (g.mode === 'gitlab' || g.mode === 'bitbucket'),
  }
}

/** FR-136: the GitLab settings of a project bound to `connection` (seeds and the create handler). */
export function gitlabGitSettings(options: {
  instance: Schemas['GitHostInstanceRefDto']
  repoId: string
  repoFullName: string
  binding: Schemas['GitBindingDto'] | null
  defaultBranch?: string
}): GitSettings {
  return gitSettings({
    provider: 'gitlab',
    repoFullName: options.repoFullName,
    defaultBranch: options.defaultBranch ?? 'main',
    instance: options.instance,
    externalRepoId: options.repoId,
    repoWebUrl: `${options.instance.baseUrl}/${options.repoFullName}`,
    binding: options.binding,
  })
}

/** Bitbucket Cloud as project settings show it (FR-136: a `git_host_instances` row, base URL NULL). */
export const BITBUCKET_CLOUD: Schemas['GitHostInstanceRefDto'] = {
  id: '0192d000-0000-7000-8000-00000000bb01',
  name: 'Bitbucket Cloud',
  baseUrl: 'https://bitbucket.org',
  enabled: true,
}

/** FR-136: the Bitbucket settings of a project bound to `connection` (seeds and the create handler). */
export function bitbucketGitSettings(options: {
  repoId: string
  repoFullName: string
  binding: Schemas['GitBindingDto'] | null
  defaultBranch?: string
  mergeStrategy?: Schemas['MergeStrategy']
}): GitSettings {
  return gitSettings({
    provider: 'bitbucket',
    repoFullName: options.repoFullName,
    defaultBranch: options.defaultBranch ?? 'main',
    instance: BITBUCKET_CLOUD,
    externalRepoId: options.repoId,
    repoWebUrl: `https://bitbucket.org/${options.repoFullName}`,
    binding: options.binding,
    mergeStrategy: options.mergeStrategy ?? 'aide_merges',
  })
}

/**
 * FR-136: the checks of binding a Bitbucket connection (create and Rebind): the caller's own (CREDENTIAL_NOT_OWNER),
 * valid (GIT_NOT_CONNECTED), with write access to the repository (FORBIDDEN on `fieldPrefix + repoId`).
 */
function checkBitbucketBinding(
  callerId: string,
  connectionId: string | null | undefined,
  repoId: string,
  fieldPrefix: string,
): Response | Schemas['BitbucketConnectionDto'] {
  const onConnection = { errors: { [`${fieldPrefix}connectionId`]: ['Bind one of your own valid Bitbucket connections.'] } }
  const onRepo = { errors: { [`${fieldPrefix}repoId`]: ['Write access to the repository is required.'] } }
  const found = findBitbucketConnection(connectionId)
  if (!found) {
    return problem(404, 'NOT_FOUND', 'The connection was not found.', onConnection)
  }
  if (found.userId !== callerId) {
    return problem(403, 'CREDENTIAL_NOT_OWNER', 'You can only bind your own connections.', onConnection)
  }
  if (found.connection.status !== 'valid') {
    return problem(409, 'GIT_NOT_CONNECTED', 'The Bitbucket connection is invalid. Reconnect it under Account → Connections, then bind it.', onConnection)
  }
  if (bitbucketDb.noWriteAccess.includes(repoId)) {
    return problem(403, 'FORBIDDEN', 'Your Bitbucket account needs write access to the repository to bind it.', onRepo)
  }
  return found.connection
}

/**
 * FR-136: the checks of binding a GitLab connection (create and Rebind): the caller's own (CREDENTIAL_NOT_OWNER), on
 * the repo's instance (VALIDATION on `fieldPrefix + connectionId`), valid on an enabled instance (GIT_NOT_CONNECTED),
 * and Developer access to the repo (FORBIDDEN).
 */
function checkGitLabBinding(
  callerId: string,
  connectionId: string | null | undefined,
  instanceId: string | null | undefined,
  repoId: string,
  fieldPrefix: string,
): Response | Schemas['GitLabConnectionDto'] {
  const onConnection = { errors: { [`${fieldPrefix}connectionId`]: ['Bind one of your own valid GitLab connections.'] } }
  const onRepo = { errors: { [`${fieldPrefix}repoId`]: ['Developer access or higher is required.'] } }
  const found = findGitLabConnection(connectionId)
  if (!found) {
    return problem(404, 'NOT_FOUND', 'The connection was not found.', onConnection)
  }
  if (found.userId !== callerId) {
    return problem(403, 'CREDENTIAL_NOT_OWNER', 'You can only bind your own connections.', onConnection)
  }
  const c = found.connection
  if (c.instanceId !== instanceId) {
    return validation({
      [`${fieldPrefix}connectionId`]: ["The connection is for a different GitLab instance than the project's repository."],
    })
  }
  if (c.status !== 'valid' || !c.instanceEnabled) {
    return problem(409, 'GIT_NOT_CONNECTED', 'The GitLab connection is invalid. Reconnect it under Account → Connections, then bind it.', onConnection)
  }
  if (gitlabDb.noDeveloperAccess.includes(repoId)) {
    return problem(403, 'FORBIDDEN', 'Your GitLab account needs developer (write) access to the repository to bind it.', onRepo)
  }
  return c
}

/** The instance as project settings show it. */
export function instanceRef(instanceId: string): Schemas['GitHostInstanceRefDto'] {
  const i = gitlabDb.instances.find((x) => x.id === instanceId)
  return {
    id: instanceId,
    name: i?.name ?? 'GitLab.com',
    baseUrl: i?.baseUrl ?? 'https://gitlab.com',
    enabled: i?.enabled ?? true,
  }
}

const bindingOf = (
  c: Schemas['GitLabConnectionDto'] | Schemas['BitbucketConnectionDto'],
  user: { id: string; name: string },
): Schemas['GitBindingDto'] => ({
  connectionId: c.id,
  login: c.login,
  status: c.status,
  boundBy: { id: user.id, name: user.name },
})

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
    // FR-129: the retired preset's name is its id (no preset ships any more).
    presetName: overrides.presetId ?? null,
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
    stackPacks: [],
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

/** FR-129: the retired preset the current release version was migrated from (history; all null: none). */
function presetFields(p: StoredProject): Pick<Project, 'presetId' | 'presetName' | 'presetVersion'> {
  const v = currentVersionOf(p)
  return { presetId: v?.presetId ?? null, presetName: v?.presetId ?? null, presetVersion: v?.presetVersion ?? null }
}

export function projectDto(project: StoredProject, userId: string): Project {
  const p = project
  return {
    ...presetFields(p),
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
    deploymentsEnabled: p.deployment?.provider != null,
    stackPacks: p.stackPacks,
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
    ...presetFields(project),
    stackPacks: project.stackPacks,
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
    // FR-157: only providers enabled on this server are listed.
    providers: projectsDb.providers
      .filter((p) => isLlmProviderEnabled(p.provider))
      .map((p) => ({ ...p, configured: p.configured || binding?.provider === p.provider })),
  }
}

/**
 * FR-159: the new project's LLM settings, bound to the chosen connection, or (a keyed provider without one) to the
 * caller's newest connection for it, as the API does.
 */
function applyCreateLlm(project: StoredProject, llm: Schemas['CreateProjectLlmRequest'], userId: string) {
  project.llm.provider = llm.provider
  project.llm.defaultModel = llm.defaultModel.trim()
  project.llm.modelsByAgent = llm.modelsByAgent ?? {}
  project.llm.binding = null
  const credential = llm.credentialId
    ? connectionsDb.credentials.find((c) => c.id === llm.credentialId)
    : llm.provider === 'anthropic' || llm.provider === 'openai'
      ? connectionsDb.credentials.find((c) => c.userId === userId && c.provider === llm.provider)
      : undefined
  if (credential) {
    bindCredential(project, credential)
  }
  project.llm.hasApiKey = credential !== undefined
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
    latestDeployment: [...(p.deployments ?? [])].sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0] ?? null,
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
function importVersions(
  body: Schemas['CreateProjectRequest'],
  hostRefs?: Schemas['GitLabRepoRefsDto'] | Schemas['BitbucketRepoRefsDto'],
): ReleaseVersion[] | Response {
  const releasedTag = body.import?.releasedTag ?? null
  let released: string | null = null
  if (releasedTag !== null) {
    const refs = hostRefs ?? (body.git.repoFullName ? githubDb.refs[body.git.repoFullName] : undefined)
    const tag = refs?.tags.find((t) => t.name === releasedTag)
    const asRelease = refs && 'releases' in refs ? refs.releases.some((r) => r.tagName === releasedTag) : false
    if (refs && !tag && !asRelease) {
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

/** GET /projects and /projects/kpis: the caller's projects (all for Admins) matching search, phase, status, pack and dev version. */
function listFiltered(user: { id: string; systemRole: string }, q: URLSearchParams) {
  const search = q.get('search')?.toLowerCase()
  const phase = q.get('phase')
  const status = q.get('status')
  const hasDevelopment = q.get('hasDevelopmentVersion')
  const pack = q.get('pack')
  return projectsDb.projects
    .filter((p) => user.systemRole === 'admin' || roleOf(p, user.id))
    .filter((p) => !search || p.name.toLowerCase().includes(search))
    .filter((p) => !phase || p.currentPhase === phase)
    .filter((p) => !status || p.status === status)
    .filter((p) => !pack || p.stackPacks.includes(pack))
    .filter((p) => hasDevelopment === null || (developmentVersionOf(p) !== undefined) === (hasDevelopment === 'true'))
}

export const projectHandlers = [
  // ---- Projects ----
  // §7.2: the KPI row over the projects matching the list's filters (mirrors GetProjectKpis).
  http.get('*/api/projects/kpis', ({ request }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const items = listFiltered(user, new URL(request.url).searchParams)
    return HttpResponse.json<Schemas['ProjectKpisDto']>({
      total: items.length,
      active: items.filter((p) => p.status !== 'released').length,
      inReview: items.filter((p) => p.status === 'reviewing').length,
      awaitingGates: items.reduce((n, p) => n + p.gates.filter((g) => g.status === 'pending').length, 0),
      released: items.filter((p) => p.status === 'released').length,
    })
  }),

  http.get('*/api/projects', ({ request }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const q = new URL(request.url).searchParams
    const page = Number(q.get('page') ?? 1)
    const limit = Number(q.get('limit') ?? 20)
    const sortBy = q.get('sortBy') ?? 'updated'
    const dir = q.get('sortOrder') === 'asc' ? 1 : -1
    const items = listFiltered(user, q)
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
    if (mode === 'gitlab') {
      if (!body.git.instanceId) {
        errors['git.instanceId'] = ['The GitLab instance is required.']
      }
      if (!body.git.connectionId) {
        errors['git.connectionId'] = ['Pick one of your GitLab connections.']
      }
      if (!body.git.repoId) {
        errors['git.repoId'] = ['Pick a GitLab project.']
      }
      if ((body.git.repoFullName ?? '').length > 255) {
        errors['git.repoFullName'] = ['The project path is at most 255 characters.']
      }
    }
    if (mode === 'bitbucket') {
      if (!body.git.connectionId) {
        errors['git.connectionId'] = ['Pick one of your Bitbucket connections.']
      }
      if (!body.git.repoId) {
        errors['git.repoId'] = ['Pick a Bitbucket repository.']
      }
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
    // FR-159: the AI model section (enabled and usable provider, the caller's own connection, a model id).
    const llmFailure = body.llm ? checkCreateLlm(body.llm, user.id) : null
    if (llmFailure) {
      return llmFailure
    }
    // FR-132, FR-136: the caller's own valid connection on the repo's instance, with Developer access to a project it sees.
    let gitlab: { connection: Schemas['GitLabConnectionDto']; repo: Schemas['GitLabRepoDto'] } | null = null
    if (mode === 'gitlab') {
      const checked = checkGitLabBinding(user.id, body.git.connectionId, body.git.instanceId, body.git.repoId ?? '', 'git.')
      if (checked instanceof Response) {
        return checked
      }
      const repo = findGitLabRepo(checked.id, body.git.repoId ?? '')
      if (!repo) {
        return problem(404, 'NOT_FOUND', "The GitLab repository wasn't found, or your GitLab account can't see it.", {
          errors: { 'git.repoId': ['Pick a GitLab project your account can see.'] },
        })
      }
      gitlab = { connection: checked, repo }
    }
    // FR-135, FR-136: the caller's own valid Bitbucket connection, with write access to a repository it sees.
    let bitbucket: { connection: Schemas['BitbucketConnectionDto']; repo: Schemas['BitbucketRepoDto'] } | null = null
    if (mode === 'bitbucket') {
      const checked = checkBitbucketBinding(user.id, body.git.connectionId, body.git.repoId ?? '', 'git.')
      if (checked instanceof Response) {
        return checked
      }
      const repo = findBitbucketRepo(checked.id, body.git.repoId ?? '')
      if (!repo) {
        return problem(404, 'NOT_FOUND', "The Bitbucket repository wasn't found, or your Bitbucket account can't see it.", {
          errors: { 'git.repoId': ['Pick a Bitbucket repository your account can see.'] },
        })
      }
      bitbucket = { connection: checked, repo }
    }
    const hostRefs = gitlab ? gitlabRefsOf(gitlab.repo) : bitbucket ? bitbucketRefsOf(bitbucket.repo) : undefined
    const versions = importing ? importVersions(body, hostRefs) : scratchVersions(body)
    if (versions instanceof Response) {
      return versions
    }
    // FR-114–FR-117: the stack composition, validated (STACK_INCOMPATIBLE, VCS_MISMATCH, paths).
    let composition: Schemas['StackCompositionDto'] | null = null
    if (body.stack) {
      const repoVcs = repoVcsOfMode(mode)
      if (repoVcs && body.stack.vcs && body.stack.vcs.pack !== repoVcs) {
        return problem(400, 'VCS_MISMATCH', "The VCS layer isn't the repository's host.")
      }
      const result = validateComposition({ ...body.stack, vcs: body.stack.vcs ?? (repoVcs ? { pack: repoVcs } : null) })
      if (!result.ok) {
        return 'errors' in result
          ? validation(Object.fromEntries(Object.entries(result.errors).map(([k, v]) => [`stack.${k}`, v])))
          : problem(400, 'STACK_INCOMPATIBLE', "These stack choices don't work together.", { violations: result.violations })
      }
      composition = result.composition
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
      git: bitbucket
        ? bitbucketGitSettings({
            repoId: bitbucket.repo.id,
            repoFullName: bitbucket.repo.fullName,
            binding: bindingOf(bitbucket.connection, user),
            defaultBranch: body.git.defaultBranch ?? 'main',
          })
        : gitlab
        ? gitlabGitSettings({
            instance: instanceRef(gitlab.connection.instanceId),
            repoId: gitlab.repo.id,
            repoFullName: gitlab.repo.fullPath,
            binding: bindingOf(gitlab.connection, user),
            defaultBranch: body.git.defaultBranch ?? 'main',
          })
        : gitSettings({
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
      ...(composition ? { stack: { composition }, stackPacks: packsOf(composition) } : {}),
    })
    // FR-88: the optional Deployment step (one of the caller's own Vercel connections; Local can't deploy).
    if (body.deployment) {
      const failed = applyVercelSetup(project, user.id, body.deployment, 'deployment.')
      if (failed) {
        projectsDb.projects = projectsDb.projects.filter((p) => p !== project)
        return failed
      }
    }
    if (body.llm) {
      applyCreateLlm(project, body.llm, user.id)
    }
    // FR-70: an import queues the Repository Analyst for the version to develop.
    const development = developmentVersionOf(project)
    if (importing && development) {
      project.analyst = { [development.id]: { status: 'queued', runId: null, artifactId: null, artifactVersion: null } }
    }
    return HttpResponse.json<Project>(projectDto(project, user.id), { status: 201 })
  }),

  http.get('*/api/projects/:id', ({ params }) => {
    const a = access(params.id)
    return a instanceof Response ? a : HttpResponse.json<Project>(projectDto(a.project, a.userId))
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
    return HttpResponse.json<Project>(projectDto(p, a.userId))
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
    // FR-136: a project can't move onto GitLab or Bitbucket here (only the wizard creates those).
    if ((provider === 'gitlab' || provider === 'bitbucket') && git.provider !== provider) {
      return validation({ provider: ['A project can only be put on GitLab or Bitbucket when it is created.'] })
    }
    // FR-140: fast_forward and squash are Bitbucket's merge strategies only.
    if ((body.mergeStrategy === 'fast_forward' || body.mergeStrategy === 'squash') && git.mode !== 'bitbucket') {
      return validation({ mergeStrategy: ['fast_forward and squash are only available on Bitbucket projects.'] })
    }
    // FR-136 Rebind: the caller's own connection (GitLab: on the project's instance, Developer access; Bitbucket: write).
    if (body.connectionId) {
      if (git.mode !== 'gitlab' && git.mode !== 'bitbucket') {
        return validation({ connectionId: ["Only used to re-bind a GitLab or Bitbucket project's connection."] })
      }
      const user = authDb.users.find((u) => u.id === a.userId)
      const checked =
        git.mode === 'bitbucket'
          ? checkBitbucketBinding(a.userId, body.connectionId, git.externalRepoId ?? '', '')
          : checkGitLabBinding(a.userId, body.connectionId, git.instance?.id, git.externalRepoId ?? '', '')
      if (checked instanceof Response) {
        return checked
      }
      git.binding = bindingOf(checked, { id: a.userId, name: user?.name ?? 'Owner' })
    }
    git.provider = provider
    git.mode = provider
    if (provider === 'local') {
      git.repoUrl = null
    } else if (body.repoUrl) {
      git.repoUrl = body.repoUrl
    }
    if (provider !== 'github_app' && provider !== 'gitlab' && provider !== 'bitbucket') {
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
