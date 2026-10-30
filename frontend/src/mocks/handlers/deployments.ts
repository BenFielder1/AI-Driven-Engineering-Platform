import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { antiforgeryFailure, authDb, nextId } from './auth'
import { platformDb, seedPlatformConnection, type StoredPlatformConnection } from './platform-connections'
import { problem } from './problem'
import {
  access,
  currentDeploymentOf,
  currentVersionOf,
  forbidden,
  isOwnerLevel,
  ownerMutation,
  record,
  type StoredDeploymentSettings,
  type StoredEnvVar,
  type StoredProject,
} from './projects'

/**
 * Settings → Deployment (FR-144–FR-146: targets, databases, env inventory, origin model) and deployments (FR-90,
 * FR-91, §4.15). Platform connections live in ./platform-connections (FR-142). These handlers follow the contract
 * (backend/openapi/api.json) and its error cases.
 */

type Deployment = Schemas['DeploymentDto']
type Settings = Schemas['DeploymentSettingsDto']
type Target = Schemas['DeploymentTargetDto']
type Component = Schemas['DeploymentComponent']
type Provider = Schemas['DeploymentProvider']
type EnvRow = Schemas['DeploymentEnvVarDto']

/** A stored Vercel connection (a platform connection with provider `vercel`). */
export type StoredVercelConnection = StoredPlatformConnection

export const VERCEL_INSTALL_URL = 'https://github.com/apps/vercel/installations/new'
export const NETLIFY_INSTALL_URL = 'https://github.com/apps/netlify/installations/new'
/** FR-143: the AIDE GitHub App's settings page (PLATFORM_PERMISSION_MISSING `installUrl`). */
export const GITHUB_APP_SETTINGS_URL = 'https://github.com/settings/apps/aide-test'

/** In-memory deployment platforms for tests (reset after each test by src/test/setup.ts). */
export const deploymentDb = {
  /** Platforms unreachable: target changes answer 502 UPSTREAM_ERROR. */
  down: false,
  /** The platform's Git app can't see the repo: the wizard answers 409, a target's setup job fails with it. */
  gitNotConnected: false,
  /** A plan restriction: the wizard answers 409 PLATFORM_PLAN_LIMIT, a target's setup job fails with it. */
  planLimit: false,
  /** FR-143: the AIDE GitHub App lacks Pages: setting up GitHub Pages answers 409 PLATFORM_PERMISSION_MISSING. */
  pagesPermissionMissing: false,
  /** Pushing env vars fails: PUT saves the value with syncError and answers 502. */
  envSyncFails: false,
  /** A platform can't delete env vars: DELETE answers 502 and keeps the variable. */
  envDeleteFails: false,
}

/** Deployments the platform never reported (tests add ids): `unknown_at_platform` (FR-151). */
export const unknownAtPlatform = new Set<string>()

/** The v2 name of the knobs (the wizard's Vercel flow). */
export const vercelDb = deploymentDb

export function resetDeploymentDb() {
  deploymentDb.down = false
  deploymentDb.gitNotConnected = false
  deploymentDb.planLimit = false
  deploymentDb.pagesPermissionMissing = false
  deploymentDb.envSyncFails = false
  deploymentDb.envDeleteFails = false
  unknownAtPlatform.clear()
}

/** §4.20 capabilities per provider. */
function capabilitiesOf(provider: Provider): Schemas['DeploymentCapabilitiesDto'] {
  switch (provider) {
    case 'github_pages':
      return { shaPinned: false, previews: false, rollback: false, perEnvironmentVariables: false, runtimeSecrets: false }
    case 'netlify':
      return { shaPinned: true, previews: true, rollback: true, perEnvironmentVariables: true, runtimeSecrets: false }
    case 'railway':
    case 'cloudflare':
      return { shaPinned: false, previews: true, rollback: true, perEnvironmentVariables: true, runtimeSecrets: true }
    case 'render':
    case 'vercel':
    case 'fake':
      return { shaPinned: true, previews: true, rollback: true, perEnvironmentVariables: true, runtimeSecrets: true }
  }
}

const emptyBuild = (): Schemas['DeploymentBuildConfigDto'] => ({
  framework: null,
  buildCommand: null,
  outputDirectory: null,
  startCommand: null,
  dockerfilePath: null,
  healthCheckPath: null,
  preDeployCommand: null,
})

/** Platform items say "not checked yet" until verify (FR-144). */
const uncheckedReadiness = (): Schemas['ReadinessItemDto'][] => [
  { id: 'connection', state: 'na', message: 'Not checked yet. Select Verify.', fixUrl: null },
]

/** A target card as the API builds it; the composition's host for `component` is `provider`. */
export function makeTarget(component: Component, provider: Provider, overrides: Partial<Target> = {}): Target {
  return {
    component,
    provider,
    hostOption: provider === 'github_pages' ? 'deploy-github-pages' : `deploy-${provider}`,
    inComposition: true,
    needsConnection: provider !== 'github_pages',
    binding: null,
    canBind: false,
    resourceId: null,
    name: null,
    rootDirectory: component === 'frontend' ? 'frontend' : 'backend',
    buildConfig: emptyBuild(),
    productionUrl: null,
    previewUrl: null,
    status: 'not_configured',
    statusReason: null,
    setup: null,
    capabilities: capabilitiesOf(provider),
    readiness: uncheckedReadiness(),
    implementedForReal: provider === 'vercel',
    fakeMode: false,
    ...overrides,
  }
}

const emptyOrigin = (): Schemas['DeploymentOriginModelDto'] => ({ model: null, packModel: null, options: [], explanation: null })

/** Deployment settings of a project; unset: no deployment hosts in the composition. */
export function deploymentOf(p: StoredProject): StoredDeploymentSettings {
  p.deployment ??= { targets: [], databases: [], publicPrefix: null, originModel: emptyOrigin(), required: [], derived: [], fakeMode: false }
  return p.deployment
}

/** FR-87, FR-142: a Vercel platform connection (`teamSlug` makes it team-scoped). */
export function seedVercelConnection(
  overrides: Partial<Omit<StoredVercelConnection, 'metadata' | 'provider'>> &
    Pick<StoredVercelConnection, 'userId'> & { teamSlug?: string | null },
): StoredVercelConnection {
  const { teamSlug = null, ...rest } = overrides
  return seedPlatformConnection({
    label: 'Vercel token',
    token: overrides.token ?? `vcp_test${nextId().slice(-4)}`,
    ...rest,
    provider: 'vercel',
    metadata: { teamSlug, teamId: teamSlug ? `team_${teamSlug}` : null },
  })
}

export function bindingOf(c: StoredPlatformConnection): Schemas['DeploymentTargetBindingDto'] {
  const user = authDb.users.find((u) => u.id === c.userId)
  return {
    connectionId: c.id,
    label: c.label,
    last4: c.last4,
    accountName: c.metadata.teamSlug ?? c.accountName,
    boundBy: { id: c.userId, name: user?.name ?? 'Unknown' },
    status: c.status,
  }
}

const hostSuffix: Record<Provider, string> = {
  vercel: 'vercel.app',
  netlify: 'netlify.app',
  cloudflare: 'workers.dev',
  render: 'onrender.com',
  railway: 'up.railway.app',
  github_pages: 'github.io',
  fake: 'vercel.test',
}

const urlsOf = (provider: Provider, name: string) =>
  provider === 'github_pages'
    ? { productionUrl: `https://acme.github.io/${name}`, previewUrl: null }
    : { productionUrl: `https://${name}.${hostSuffix[provider]}`, previewUrl: `https://${name}-preview.${hostSuffix[provider]}` }

/** A set-up target: bound (when it needs a connection), resource linked, ready. */
function readyTarget(
  component: Component,
  provider: Provider,
  name: string,
  connection?: StoredPlatformConnection,
  overrides: Partial<Target> = {},
): Target {
  return makeTarget(component, provider, {
    binding: connection ? bindingOf(connection) : null,
    resourceId: `${provider}_${name}`,
    name,
    ...urlsOf(provider, name),
    status: 'ready',
    setup: {
      jobId: nextId(),
      state: 'succeeded',
      step: null,
      errorCode: null,
      message: null,
      fixUrl: null,
      startedAt: '2026-09-02T10:00:00Z',
      completedAt: '2026-09-02T10:00:30Z',
    },
    ...overrides,
  })
}

/**
 * Sets a project up with a ready Vercel frontend target (the v2 shape most deployment tests need): bound to
 * `connection` (if any) and linked to the Vercel project `inventory`.
 */
export function seedDeploymentSettings(
  p: StoredProject,
  init: { connection?: StoredVercelConnection; settings?: Partial<StoredDeploymentSettings> } = {},
): StoredDeploymentSettings {
  const d = deploymentOf(p)
  d.targets = [
    readyTarget('frontend', 'vercel', 'inventory', init.connection, {
      buildConfig: { ...emptyBuild(), framework: 'nextjs', buildCommand: 'npm run build' },
    }),
  ]
  d.originModel = {
    model: 'same-origin',
    packModel: 'same-origin',
    options: ['same-origin'],
    explanation: 'One Next.js app serves the pages and the API.',
  }
  d.publicPrefix = 'NEXT_PUBLIC_'
  Object.assign(d, init.settings)
  return d
}

const derivedRow = (overrides: Partial<EnvRow> & Pick<EnvRow, 'key' | 'derivedFrom'>): EnvRow => ({
  appliesTo: ['frontend'],
  environments: ['preview', 'production'],
  sensitive: false,
  public: false,
  source: 'derived',
  description: null,
  hasValue: false,
  updatedAt: null,
  syncedAt: null,
  syncError: null,
  ...overrides,
})

/**
 * FR-144–FR-146 fixture: a React SPA on Netlify and an ASP.NET API on Render, in Fake mode, origin model `proxy`,
 * Render Postgres per environment (placeholders until managed databases), with derived and required variables.
 * `configured`: both targets set up (bound to the given connections).
 */
export function seedNetlifyRenderTargets(
  p: StoredProject,
  init: { configured?: boolean; netlify?: StoredPlatformConnection; render?: StoredPlatformConnection } = {},
): StoredDeploymentSettings {
  const d = deploymentOf(p)
  const feBuild = { ...emptyBuild(), framework: 'vite', buildCommand: 'npm run build', outputDirectory: 'dist' }
  const beBuild = {
    ...emptyBuild(),
    dockerfilePath: 'backend/Dockerfile',
    healthCheckPath: '/api/health/ready',
    preDeployCommand: '/app/efbundle --connection "$DATABASE_URL"',
  }
  const common = { fakeMode: true, implementedForReal: true }
  d.fakeMode = true
  d.targets = init.configured
    ? [
        readyTarget('frontend', 'netlify', 'inventory-web', init.netlify, { buildConfig: feBuild, ...common }),
        readyTarget('backend', 'render', 'inventory-api', init.render, { buildConfig: beBuild, ...common }),
      ]
    : [
        makeTarget('frontend', 'netlify', { buildConfig: feBuild, ...common }),
        makeTarget('backend', 'render', { buildConfig: beBuild, ...common }),
      ]
  d.databases = [
    { environment: 'production', provider: 'render-postgres', status: 'not_configured' },
    { environment: 'preview', provider: 'render-postgres', status: 'not_configured' },
  ]
  d.publicPrefix = 'VITE_'
  d.originModel = {
    model: 'proxy',
    packModel: 'same-origin',
    options: ['proxy', 'cross-origin'],
    explanation: 'Netlify rewrites /api/* to the backend (API_ORIGIN), so the browser only talks to the frontend origin.',
  }
  d.required = [
    {
      key: 'JWT_SIGNING_KEY',
      appliesTo: ['backend'],
      environments: ['preview', 'production'],
      sensitive: true,
      public: false,
      description: 'Signs the API tokens (be-aspnet).',
    },
  ]
  d.derived = [
    derivedRow({
      key: 'API_ORIGIN',
      derivedFrom: 'the backend target on Render',
      hasValue: init.configured === true,
      updatedAt: init.configured ? '2026-09-02T10:00:30Z' : null,
      syncedAt: init.configured ? '2026-09-02T10:00:31Z' : null,
    }),
    derivedRow({ key: 'DATABASE_URL', appliesTo: ['backend'], sensitive: true, derivedFrom: 'Render Postgres (production)' }),
  ]
  return d
}

/**
 * FR-143, FR-146 fixture: a React SPA on GitHub Pages (no connection: the project's GitHub App) with a backend on
 * Railway; origin model `cross-origin`.
 */
export function seedGitHubPagesTargets(p: StoredProject): StoredDeploymentSettings {
  const d = deploymentOf(p)
  d.targets = [
    makeTarget('frontend', 'github_pages', {
      buildConfig: { ...emptyBuild(), framework: 'vite', buildCommand: 'npm run build', outputDirectory: 'dist' },
      implementedForReal: true,
    }),
    makeTarget('backend', 'railway', { implementedForReal: false }),
  ]
  d.publicPrefix = 'VITE_'
  d.originModel = {
    model: 'cross-origin',
    packModel: 'cross-origin',
    options: ['cross-origin'],
    explanation:
      'The SPA calls the API at VITE_API_BASE_URL (set at build time); the API allows the Pages origin in CORS_ALLOWED_ORIGINS.',
  }
  d.derived = [
    derivedRow({
      key: 'VITE_API_BASE_URL',
      environments: ['production'],
      public: true,
      derivedFrom: 'the backend target on Railway (production)',
    }),
    derivedRow({
      key: 'CORS_ALLOWED_ORIGINS',
      appliesTo: ['backend'],
      environments: ['production'],
      derivedFrom: 'the frontend target on GitHub Pages',
    }),
  ]
  return d
}

export function seedEnvVar(p: StoredProject, overrides: Partial<StoredEnvVar> & Pick<StoredEnvVar, 'key'>): StoredEnvVar {
  const env = (p.deploymentEnv ??= [])
  const v: StoredEnvVar = {
    appliesTo: ['frontend'],
    environments: ['preview', 'production'],
    sensitive: false,
    public: false,
    source: 'user',
    derivedFrom: null,
    description: null,
    hasValue: true,
    updatedAt: '2026-09-02T10:00:00Z',
    syncedAt: '2026-09-02T10:00:01Z',
    syncError: null,
    value: 'value',
    ...overrides,
  }
  env.push(v)
  return v
}

let deploymentClock = Date.parse('2026-09-05T10:00:00Z')

/** FR-150: the row a rollback records is `promote` on Vercel (v2 wording), `rollback` elsewhere. */
const rollbackTrigger = (platform: Provider): 'promote' | 'rollback' =>
  platform === 'vercel' || platform === 'fake' ? 'promote' : 'rollback'

/**
 * Appends a deployment. Defaults: a ready preview of the frontend on Vercel at the current version by AIDE, one minute
 * after the previous one. `current`, `canRollback` and `rollbackBlockedReason` are worked out when it is listed.
 */
export function seedDeployment(p: StoredProject, overrides: Partial<Deployment> = {}): Deployment {
  const list = (p.deployments ??= [])
  deploymentClock += 60_000
  const version = currentVersionOf(p)
  const sha = overrides.gitSha ?? `${String(list.length + 1).padStart(2, '0')}a9c1d8e7b6a5f4e3d2c1b0a9f8e7d6c5b4a3f`.slice(0, 40)
  const target = overrides.target ?? 'preview'
  const platform = overrides.platform ?? overrides.provider ?? 'vercel'
  const trigger = overrides.trigger ?? 'auto'
  const d: Deployment = {
    id: nextId(),
    target,
    provider: platform,
    component: 'frontend',
    platform,
    kind: trigger === 'promote' ? 'promote' : trigger === 'rollback' ? 'rollback' : 'deploy',
    current: false,
    canRollback: false,
    rollbackBlockedReason: null,
    olderThanLatestMigration: null,
    status: 'ready',
    url: `https://inventory-${sha.slice(0, 8)}.${platform === 'vercel' ? 'vercel.app' : 'example.app'}`,
    inspectorUrl: null,
    gitRef: target === 'production' ? 'main' : (version?.branchName ?? 'aide/project/v0.1.0'),
    gitSha: sha,
    releaseVersionId: version?.id ?? '',
    releaseVersion: version?.version ?? '0.1.0',
    releaseId: null,
    errorCode: null,
    errorMessage: null,
    buildLogTail: null,
    trigger,
    triggeredBy: null,
    promotedFromDeploymentId: null,
    createdAt: new Date(deploymentClock).toISOString(),
    readyAt: overrides.status && overrides.status !== 'ready' ? null : new Date(deploymentClock + 45_000).toISOString(),
    ...overrides,
  }
  list.push(d)
  return d
}

const envDto = (v: StoredEnvVar): EnvRow => ({
  key: v.key,
  appliesTo: [...v.appliesTo],
  environments: [...v.environments],
  sensitive: v.sensitive,
  public: v.public,
  source: v.source,
  derivedFrom: v.derivedFrom,
  description: v.description,
  hasValue: v.hasValue,
  updatedAt: v.updatedAt,
  syncedAt: v.syncedAt,
  syncError: v.syncError,
})

/** The env inventory (FR-145): user rows, derived rows, and the pack-required keys that have no user value. */
function inventoryOf(p: StoredProject): EnvRow[] {
  const d = deploymentOf(p)
  const user = (p.deploymentEnv ?? []).map(envDto)
  const required = d.required
    .filter((r) => !user.some((u) => u.key === r.key))
    .map<EnvRow>((r) => ({
      ...r,
      appliesTo: [...r.appliesTo],
      environments: [...r.environments],
      source: 'required',
      derivedFrom: null,
      hasValue: false,
      updatedAt: null,
      syncedAt: null,
      syncError: null,
    }))
  return [...user, ...d.derived.map((r) => ({ ...r })), ...required].sort((x, y) => x.key.localeCompare(y.key))
}

function settingsDto(p: StoredProject, owner: boolean): Settings {
  const d = deploymentOf(p)
  return {
    targets: d.targets.map((t) => ({ ...t, canBind: owner && t.needsConnection, readiness: t.readiness.map((r) => ({ ...r })) })),
    databases: d.databases.map((db) => ({ ...db })),
    env: { publicPrefix: d.publicPrefix, variables: inventoryOf(p) },
    originModel: { ...d.originModel, options: [...d.originModel.options] },
    canManage: owner,
    fakeMode: d.fakeMode,
  }
}

const notFound = (what: string) => problem(404, 'NOT_FOUND', what)

const upstream = () => problem(502, 'UPSTREAM_ERROR', 'The deployment platform could not be reached.')
const VERCEL_PROJECT_NAME = /^(?!.*---)[a-z0-9._-]{1,100}$/
const ENV_KEY = /^[A-Za-z_][A-Za-z0-9_]{0,255}$/
/** §4.18 secret patterns (EnvInventoryRules.LooksSecret). */
const SECRET_KEY = /SECRET|SERVICE_?ROLE|PRIVATE|PASSWORD/i
const SECRET_VALUE = /sb_secret_|sk_live|PRIVATE KEY-----|service_role/

/** FR-88 wizard (`POST /projects` `deployment`): v2's Vercel flow, now the project's frontend target (FR-144). */
export function applyVercelSetup(
  p: StoredProject,
  userId: string,
  body: { connectionId?: string | null; vercelProjectId?: string | null; create?: Schemas['CreateVercelProjectRequest'] | null },
  /** POST /projects names the field (`deployment.connectionId`). */
  fieldPrefix: string | null = null,
): Response | null {
  const key = fieldPrefix === null ? undefined : { errors: { [`${fieldPrefix}connectionId`]: ['Pick one of your own Vercel connections.'] } }
  if (p.git.mode === 'local') {
    return problem(400, 'VALIDATION', 'Deployment needs a GitHub-backed project.', {
      errors: { provider: ['A Local project cannot deploy to Vercel.'] },
    })
  }
  if (body.vercelProjectId && body.create) {
    return problem(400, 'VALIDATION', 'Send vercelProjectId or create, not both.', {
      errors: { create: ['Link an existing project or create one, not both.'] },
    })
  }
  if (body.create && !VERCEL_PROJECT_NAME.test(body.create.name)) {
    return problem(400, 'VALIDATION', 'The Vercel project name is invalid.', {
      errors: { 'create.name': ['Use up to 100 lowercase letters, digits, ".", "_" or "-", without "---".'] },
    })
  }
  let connection: StoredVercelConnection | undefined
  if (body.connectionId) {
    connection = platformDb.connections.find((c) => c.id === body.connectionId && c.provider === 'vercel')
    if (!connection) {
      return problem(404, 'NOT_FOUND', 'The connection was not found.', key)
    }
    if (connection.userId !== userId) {
      return problem(403, 'CREDENTIAL_NOT_OWNER', 'You can only bind your own connection.', key)
    }
  }
  if (body.vercelProjectId || body.create) {
    if (deploymentDb.down) {
      return upstream()
    }
    if (deploymentDb.gitNotConnected) {
      return problem(409, 'PLATFORM_GIT_NOT_CONNECTED', "Vercel's GitHub App can't access the repository.", {
        installUrl: VERCEL_INSTALL_URL,
        platform: 'vercel',
      })
    }
    if (deploymentDb.planLimit) {
      return problem(409, 'PLATFORM_PLAN_LIMIT', 'A Hobby account cannot deploy an organisation repository.', { platform: 'vercel' })
    }
  }
  const d = deploymentOf(p)
  const name = body.create?.name ?? body.vercelProjectId?.replace(/^prj_/, '') ?? 'app'
  const root = body.create?.rootDirectory?.trim() ?? ''
  d.targets = [
    readyTarget('frontend', 'vercel', name, connection, {
      resourceId: body.vercelProjectId ?? `prj_${nextId().slice(-6)}`,
      rootDirectory: root === '' ? '.' : root,
      productionUrl: null,
    }),
    ...d.targets.filter((t) => t.component !== 'frontend'),
  ]
  return null
}

/** The setup job's progress per target (FR-144): each GET of the settings advances it one step. */
interface PendingSetup {
  steps: string[]
  failure: { errorCode: string; message: string; fixUrl: string | null } | null
  name: string
}

const pendingSetups = new WeakMap<Target, PendingSetup>()

function advanceSetups(d: StoredDeploymentSettings) {
  for (const t of d.targets) {
    const pending = pendingSetups.get(t)
    if (!pending || !t.setup) {
      continue
    }
    const next = pending.steps.shift()
    if (next !== undefined) {
      t.setup = { ...t.setup, state: 'running', step: next, startedAt: t.setup.startedAt ?? new Date().toISOString() }
      continue
    }
    pendingSetups.delete(t)
    const completedAt = new Date().toISOString()
    if (pending.failure) {
      t.status = 'error'
      t.statusReason = pending.failure.message
      t.setup = { ...t.setup, state: 'failed', step: null, ...pending.failure, completedAt }
      continue
    }
    t.status = 'ready'
    t.statusReason = null
    t.name = pending.name
    t.resourceId ??= `${t.provider}_${pending.name}`
    Object.assign(t, urlsOf(t.provider, pending.name))
    t.setup = { ...t.setup, state: 'succeeded', step: null, completedAt }
  }
}

const platformLabel: Record<Provider, string> = {
  vercel: 'Vercel',
  netlify: 'Netlify',
  cloudflare: 'Cloudflare',
  render: 'Render',
  railway: 'Railway',
  github_pages: 'GitHub Pages',
  fake: 'Fake',
}

/** FR-144 verify: the readiness checklist of a target, from the mock's knobs. */
function readinessOf(p: StoredProject, t: Target): Schemas['ReadinessItemDto'][] {
  const name = platformLabel[t.provider]
  const missing = inventoryOf(p).filter((r) => r.source === 'required' && r.appliesTo.includes(t.component))
  const item = (id: string, state: Schemas['ReadinessState'], message: string, fixUrl: string | null = null) => ({ id, state, message, fixUrl })
  return [
    !t.needsConnection
      ? item('connection', 'ok', "Uses the project's GitHub App installation.")
      : t.binding?.status === 'valid'
        ? item('connection', 'ok', `The ${name} connection is valid.`)
        : item('connection', 'error', `No valid ${name} connection is bound.`),
    deploymentDb.gitNotConnected
      ? item('git_access', 'error', `${name} can't see the repository.`, NETLIFY_INSTALL_URL)
      : item('git_access', 'ok', `${name} can access acme/inventory.`),
    missing.length > 0
      ? item('required_env', 'warning', `Missing: ${missing.map((m) => m.key).join(', ')}.`)
      : item('required_env', 'ok', 'Every required variable has a value.'),
    item('branch_control', 'ok', 'Auto-deploys for aide/** are off.'),
    t.capabilities.previews
      ? item('preview_environment', 'ok', 'The preview environment is ready.')
      : item('preview_environment', 'na', `${name} has no previews.`),
    item('plan_notes', 'na', 'No plan limits apply.'),
  ]
}

/** A target with a linked resource (FR-144): deployments can run. */
const configured = (p: StoredProject) => p.deployment?.targets.some((t) => t.resourceId !== null) ?? false

/** §4.20: whether a platform can make an earlier deployment current again (GitHub Pages can't). */
const canRollBackOn = (platform: Provider) => capabilitiesOf(platform).rollback

/** FR-150, FR-151: why a deployment can't be rolled back to (`null`: it can), in the API's order. */
function blockedReason(p: StoredProject, d: Deployment): string | null {
  if (!canRollBackOn(d.platform)) {
    return 'unsupported'
  }
  if (d.target !== 'production') {
    return 'not_production'
  }
  if (d.status !== 'ready') {
    return 'not_ready'
  }
  if (unknownAtPlatform.has(d.id)) {
    return 'unknown_at_platform'
  }
  return currentDeploymentOf(p, d.component)?.id === d.id ? 'current' : null
}

/** The derived fields of a listed deployment (FR-150, FR-151). */
function withRollbackState(p: StoredProject, d: Deployment): Deployment {
  const reason = blockedReason(p, d)
  return {
    ...d,
    current: currentDeploymentOf(p, d.component)?.id === d.id,
    canRollback: reason === null,
    rollbackBlockedReason: reason,
  }
}

/** Who may redeploy (FR-90, FR-91): previews need a writer, production an Owner. */
function mayDeploy(a: Exclude<ReturnType<typeof access>, Response>, target: Schemas['DeploymentEnvironment']) {
  return target === 'preview' ? a.level !== 'viewer' : isOwnerLevel(a)
}

function userRef(userId: string) {
  const user = authDb.users.find((u) => u.id === userId)
  return { id: userId, name: user?.name ?? 'Unknown' }
}

export const deploymentHandlers = [
  // ---- Settings → Deployment (FR-144–FR-146) ----
  http.get('*/api/projects/:id/settings/deployment', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    advanceSetups(deploymentOf(a.project))
    return HttpResponse.json<Settings>(settingsDto(a.project, a.level === 'owner'))
  }),

  http.put('*/api/projects/:id/settings/deployment/targets/:component', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['PutDeploymentTargetRequest']
    const d = deploymentOf(a.project)
    const t = d.targets.find((x) => x.component === params.component)
    if (!t) {
      return problem(400, 'VALIDATION', 'The component has no deployment host.', {
        errors: { component: ['The composition has no host for this component.'] },
      })
    }
    if (body.provider !== t.provider && !(d.fakeMode && body.provider === 'fake')) {
      return problem(400, 'STACK_INCOMPATIBLE', "These stack choices don't work together.", {
        violations: [
          { layer: `${t.component}Host`, option: body.provider, rule: 'R7', reason: `The composition hosts the ${t.component} on ${platformLabel[t.provider]}` },
        ],
      })
    }
    if (t.provider === 'github_pages' && deploymentDb.pagesPermissionMissing) {
      return problem(409, 'PLATFORM_PERMISSION_MISSING', 'The AIDE GitHub App lacks the Pages permission.', {
        platform: 'github_pages',
        installUrl: GITHUB_APP_SETTINGS_URL,
      })
    }
    let connection: StoredPlatformConnection | undefined
    if (t.needsConnection) {
      // Every PUT but GitHub Pages names one of the caller's own connections (a rebind included).
      if (!body.connectionId) {
        return problem(400, 'VALIDATION', 'The deployment settings are invalid.', {
          errors: { connectionId: [`Pick one of your own ${platformLabel[t.provider]} connections.`] },
        })
      }
      connection = platformDb.connections.find((c) => c.id === body.connectionId && c.provider === t.provider)
      if (!connection) {
        return notFound('The connection was not found.')
      }
      if (connection.userId !== a.userId) {
        return problem(403, 'CREDENTIAL_NOT_OWNER', 'You can only bind your own connection.')
      }
      if (body.create && body.link) {
        return problem(400, 'VALIDATION', 'The deployment settings are invalid.', { errors: { create: ['Create a resource or link one, not both.'] } })
      }
      // Neither create nor link: a rebind of the existing resource; without one, VALIDATION on `create`.
      if (!body.create && !body.link && !t.resourceId) {
        return problem(400, 'VALIDATION', 'The deployment settings are invalid.', { errors: { create: ['Create a resource or link an existing one.'] } })
      }
      // The backend follows Vercel's naming rules; the mock only checks non-empty and up to 100 characters.
      if (body.create && (body.create.name.trim() === '' || body.create.name.length > 100)) {
        return problem(400, 'VALIDATION', 'The deployment settings are invalid.', {
          errors: { 'create.name': ['Enter a name of up to 100 characters.'] },
        })
      }
      if (deploymentDb.down) {
        return upstream()
      }
    }
    if (connection) {
      t.binding = bindingOf(connection)
    }
    const name = body.create?.name ?? body.link?.resourceId.replace(/^[a-z]+_/, '') ?? t.name ?? `${a.project.slug}-${t.component}`
    if (body.link) {
      t.resourceId = body.link.resourceId
    }
    t.status = 'configuring'
    t.statusReason = null
    t.setup = { jobId: nextId(), state: 'queued', step: 'queued', errorCode: null, message: null, fixUrl: null, startedAt: null, completedAt: null }
    const failure = deploymentDb.gitNotConnected
      ? {
          errorCode: 'PLATFORM_GIT_NOT_CONNECTED',
          message: `${platformLabel[t.provider]}'s GitHub App can't access acme/inventory.`,
          fixUrl: NETLIFY_INSTALL_URL,
        }
      : deploymentDb.planLimit
        ? { errorCode: 'PLATFORM_PLAN_LIMIT', message: 'A private organisation repository needs the Pro plan.', fixUrl: null }
        : null
    const rebind = !body.create && !body.link
    pendingSetups.set(t, { steps: rebind ? ['linking'] : [body.link ? 'linking' : 'creating', 'syncing_env'], failure, name })
    return HttpResponse.json<Target>({ ...t, canBind: t.needsConnection }, { status: 202 })
  }),

  http.delete('*/api/projects/:id/settings/deployment/targets/:component', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    const d = deploymentOf(a.project)
    const i = d.targets.findIndex((x) => x.component === params.component)
    const t = d.targets[i]
    if (!t) {
      return notFound('The target was not found.')
    }
    d.targets[i] = makeTarget(t.component, t.provider, {
      hostOption: t.hostOption,
      rootDirectory: t.rootDirectory,
      buildConfig: t.buildConfig,
      fakeMode: t.fakeMode,
      implementedForReal: t.implementedForReal,
    })
    return new HttpResponse(null, { status: 204 })
  }),

  http.post('*/api/projects/:id/settings/deployment/targets/:component/verify', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    const t = deploymentOf(a.project).targets.find((x) => x.component === params.component)
    if (!t) {
      return notFound('The target was not found.')
    }
    t.readiness = readinessOf(a.project, t)
    if (t.status === 'ready' && t.readiness.some((r) => r.state === 'error')) {
      t.status = 'attention'
    }
    return HttpResponse.json<Schemas['DeploymentVerifyDto']>({ target: { ...t, canBind: t.needsConnection }, items: t.readiness })
  }),

  http.get('*/api/projects/:id/settings/deployment/env', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    if (!isOwnerLevel(a)) {
      return forbidden()
    }
    return HttpResponse.json<EnvRow[]>(inventoryOf(a.project))
  }),

  http.put('*/api/projects/:id/settings/deployment/env/:key', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['PutDeploymentEnvRequest']
    const key = String(params.key)
    const d = deploymentOf(a.project)
    const isPublic = (d.publicPrefix !== null && key.startsWith(d.publicPrefix)) || (body.public ?? false)
    const errors: Record<string, string[]> = {}
    if (!ENV_KEY.test(key)) {
      errors.key = ['Use letters, digits and "_", not starting with a digit.']
    }
    if (body.targets.length === 0) {
      errors.targets = ['Choose preview, production or both.']
    }
    if (!body.value) {
      errors.value = ['The value is required.']
    }
    if (body.appliesTo?.length === 0) {
      errors.appliesTo = ['Choose frontend, backend or both, each once.']
    }
    if (d.derived.some((r) => r.key === key)) {
      errors.key = [`AIDE derives ${key} from the deployment targets; it can't be set by hand.`]
    }
    const secret = isPublic && (SECRET_KEY.test(key) || SECRET_VALUE.test(body.value))
    if (secret) {
      errors.public = ["Secret values can't be public."]
    }
    if (Object.keys(errors).length > 0) {
      return problem(
        400,
        'VALIDATION',
        secret ? "The environment variable is invalid: secret values can't be public." : 'The environment variable is invalid.',
        { errors },
      )
    }
    const env = (a.project.deploymentEnv ??= [])
    const now = new Date().toISOString()
    let v = env.find((e) => e.key === key)
    if (!v) {
      v = {
        key,
        appliesTo: [],
        environments: [],
        sensitive: false,
        public: false,
        source: 'user',
        derivedFrom: null,
        description: null,
        hasValue: true,
        updatedAt: now,
        syncedAt: null,
        syncError: null,
        value: '',
      }
      env.push(v)
    }
    v.value = body.value
    v.environments = [...body.targets]
    v.appliesTo = [...(body.appliesTo ?? ['frontend'])]
    v.sensitive = body.sensitive ?? false
    v.public = isPublic
    v.updatedAt = now
    if (deploymentDb.envSyncFails) {
      v.syncedAt = null
      v.syncError = 'Render answered 500 while saving the variable.'
      return upstream()
    }
    v.syncedAt = now
    v.syncError = null
    return HttpResponse.json<EnvRow>(envDto(v))
  }),

  http.delete('*/api/projects/:id/settings/deployment/env/:key', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    const env = a.project.deploymentEnv ?? []
    const v = env.find((e) => e.key === params.key)
    if (!v) {
      return notFound('The environment variable was not found.')
    }
    if (deploymentDb.envDeleteFails) {
      return upstream()
    }
    a.project.deploymentEnv = env.filter((e) => e !== v)
    return new HttpResponse(null, { status: 204 })
  }),

  // ---- Deployments (FR-90, FR-91) ----
  http.get('*/api/projects/:id/deployments', ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const q = new URL(request.url).searchParams
    const environment = q.get('environment') ?? q.get('target')
    const component = q.get('component')
    const versionId = q.get('releaseVersionId')
    const p = a.project
    return HttpResponse.json<Deployment[]>(
      [...(p.deployments ?? [])]
        .filter((d) => !environment || d.target === environment)
        .filter((d) => !component || d.component === component)
        .filter((d) => !versionId || d.releaseVersionId === versionId)
        .sort((x, y) => y.createdAt.localeCompare(x.createdAt))
        .map((d) => withRollbackState(p, d)),
    )
  }),

  http.post('*/api/projects/:id/deployments/preview', async ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    await record(request)
    if (a.level === 'viewer') {
      return forbidden()
    }
    if (!configured(a.project)) {
      return problem(409, 'DEPLOYMENT_NOT_CONFIGURED', 'The project has no deployment settings.')
    }
    const created = seedDeployment(a.project, {
      target: 'preview',
      status: 'queued',
      url: null,
      trigger: 'manual',
      triggeredBy: userRef(a.userId),
      createdAt: new Date().toISOString(),
    })
    return HttpResponse.json<Deployment>(created, { status: 202 })
  }),

  http.post('*/api/projects/:id/deployments/:deploymentId/redeploy', async ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    await record(request)
    const source = a.project.deployments?.find((d) => d.id === params.deploymentId)
    if (!source) {
      return notFound('The deployment was not found.')
    }
    if (!mayDeploy(a, source.target)) {
      return forbidden()
    }
    if (!configured(a.project)) {
      return problem(409, 'DEPLOYMENT_NOT_CONFIGURED', 'The project has no deployment settings.')
    }
    const created = seedDeployment(a.project, {
      target: source.target,
      gitRef: source.gitRef,
      gitSha: source.gitSha,
      releaseVersionId: source.releaseVersionId,
      releaseVersion: source.releaseVersion,
      releaseId: source.releaseId,
      status: 'queued',
      url: null,
      trigger: 'redeploy',
      triggeredBy: userRef(a.userId),
      createdAt: new Date().toISOString(),
    })
    return HttpResponse.json<Deployment>(created, { status: 202 })
  }),

  // FR-150: rollback per provider capability; `/promote` is its v2 alias.
  ...(['rollback', 'promote'] as const).map((action) =>
    http.post(`*/api/projects/:id/deployments/:deploymentId/${action}`, async ({ request, params }) => {
      const a = ownerMutation(request, params.id)
      if (a instanceof Response) {
        return a
      }
      await record(request)
      const source = a.project.deployments?.find((d) => d.id === params.deploymentId)
      if (!source) {
        return notFound('The deployment was not found.')
      }
      if (!configured(a.project)) {
        return problem(409, 'DEPLOYMENT_NOT_CONFIGURED', 'The project has no deployment settings.')
      }
      if (!canRollBackOn(source.platform)) {
        return problem(409, 'DEPLOYMENT_ACTION_UNSUPPORTED', 'The platform has no rollback.', { platform: source.platform })
      }
      if (source.target !== 'production' || source.status !== 'ready' || unknownAtPlatform.has(source.id)) {
        return problem(409, 'INVALID_DEPLOYMENT', 'Only a ready production deployment can be rolled back to.')
      }
      if (deploymentDb.down) {
        return problem(502, 'UPSTREAM_ERROR', 'The platform could not be reached.')
      }
      const trigger = rollbackTrigger(source.platform)
      const created = seedDeployment(a.project, {
        target: 'production',
        component: source.component,
        provider: source.provider,
        platform: source.platform,
        gitRef: source.gitRef,
        gitSha: source.gitSha,
        releaseVersionId: source.releaseVersionId,
        releaseVersion: source.releaseVersion,
        releaseId: source.releaseId,
        url: source.url,
        status: 'ready',
        trigger,
        kind: trigger,
        triggeredBy: userRef(a.userId),
        promotedFromDeploymentId: source.id,
        olderThanLatestMigration: source.olderThanLatestMigration,
        createdAt: new Date().toISOString(),
      })
      return HttpResponse.json<Deployment>(withRollbackState(a.project, created), { status: 202 })
    }),
  ),
]
