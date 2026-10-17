import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { antiforgeryFailure, authDb, currentUser, nextId, unauthenticated } from './auth'
import { problem } from './problem'
import {
  access,
  currentVersionOf,
  forbidden,
  isOwnerLevel,
  ownerMutation,
  projectsDb,
  record,
  type StoredDeploymentSettings,
  type StoredEnvVar,
  type StoredProject,
} from './projects'

/**
 * Vercel connections, deployment settings, env vars and deployments (FR-87–FR-91, §4.15). The endpoints answer 501
 * until the backend lands; these handlers follow the contract (backend/openapi/api.json) and its error cases.
 */

type VercelConnection = Schemas['VercelConnectionDto']
type Deployment = Schemas['DeploymentDto']
type Settings = Schemas['DeploymentSettingsDto']

/** A stored Vercel connection: the DTO minus the derived `boundProjects`, plus its owner and token. */
export interface StoredVercelConnection extends Omit<VercelConnection, 'boundProjects'> {
  userId: string
  token: string
}

export const VERCEL_INSTALL_URL = 'https://github.com/apps/vercel/installations/new'

/** In-memory Vercel backend for tests (reset after each test by src/test/setup.ts). */
export const vercelDb = {
  connections: [] as StoredVercelConnection[],
  /** Tokens Vercel rejects (CREDENTIAL_INVALID on add; status invalid on test). */
  rejectedTokens: new Set<string>(),
  /** Vercel unreachable: add, test and settings changes answer 502 UPSTREAM_ERROR. */
  down: false,
  /** Vercel's GitHub App can't access the repo: linking or creating a project answers 409 VERCEL_GIT_NOT_CONNECTED. */
  gitNotConnected: false,
  /** Hobby plan + org repo: 409 VERCEL_PLAN_LIMIT. */
  planLimit: false,
  /** Pushing env vars fails: PUT saves the value with syncError and answers 502. */
  envSyncFails: false,
  /** Vercel can't delete env vars: DELETE answers 502 and keeps the variable. */
  envDeleteFails: false,
}

export function resetVercelDb() {
  vercelDb.connections = []
  vercelDb.rejectedTokens = new Set()
  vercelDb.down = false
  vercelDb.gitNotConnected = false
  vercelDb.planLimit = false
  vercelDb.envSyncFails = false
  vercelDb.envDeleteFails = false
}

/** FR-88, §4.11: the env vars each preset requires. */
const presetRequiredEnv: Record<string, StoredDeploymentSettings['requiredEnv']> = {
  'nextjs-supabase': [
    { key: 'NEXT_PUBLIC_SUPABASE_URL', targets: ['preview', 'production'], sensitive: false, description: 'The Supabase project URL.' },
    {
      key: 'NEXT_PUBLIC_SUPABASE_ANON_KEY',
      targets: ['preview', 'production'],
      sensitive: false,
      description: 'The Supabase anon (public) key.',
    },
    {
      key: 'SUPABASE_SERVICE_ROLE_KEY',
      targets: ['preview', 'production'],
      sensitive: true,
      description: 'The Supabase service role key (server only).',
    },
  ],
  'nextjs-frontend': [],
}

const vercelPresets = new Set(['nextjs-frontend', 'nextjs-supabase'])

const presetOf = (p: StoredProject) => currentVersionOf(p)?.presetId ?? null

/** Deployment settings of a project, created with the preset's defaults the first time they are read. */
export function deploymentOf(p: StoredProject): StoredDeploymentSettings {
  p.deployment ??= {
    provider: null,
    supported: vercelPresets.has(presetOf(p) ?? ''),
    binding: null,
    vercelProject: null,
    rootDirectory: '.',
    framework: 'nextjs',
    requiredEnv: presetRequiredEnv[presetOf(p) ?? ''] ?? [],
    gitConnected: null,
    installUrl: null,
  }
  return p.deployment
}

export function seedVercelConnection(
  overrides: Partial<StoredVercelConnection> & Pick<StoredVercelConnection, 'userId'>,
): StoredVercelConnection {
  const token = overrides.token ?? `vcp_test${nextId().slice(-4)}`
  const c: StoredVercelConnection = {
    id: nextId(),
    label: 'Vercel token',
    last4: token.slice(-4),
    accountName: 'ada',
    teamId: null,
    teamSlug: null,
    scope: 'account',
    expiresAt: null,
    status: 'valid',
    lastValidatedAt: '2026-09-01T09:00:00Z',
    createdAt: '2026-09-01T09:00:00Z',
    ...overrides,
    token,
  }
  vercelDb.connections.unshift(c)
  return c
}

function bindingOf(c: StoredVercelConnection): Schemas['DeploymentBindingDto'] {
  const user = authDb.users.find((u) => u.id === c.userId)
  return {
    connectionId: c.id,
    label: c.label,
    last4: c.last4,
    teamSlug: c.teamSlug,
    boundBy: { id: c.userId, name: user?.name ?? 'Unknown' },
    status: c.status,
  }
}

/** Sets a project up for Vercel deployments (FR-88): provider, a bound connection and a linked Vercel project. */
export function seedDeploymentSettings(
  p: StoredProject,
  init: { connection?: StoredVercelConnection; settings?: Partial<StoredDeploymentSettings> } = {},
): StoredDeploymentSettings {
  const d = deploymentOf(p)
  Object.assign(d, {
    provider: 'vercel',
    binding: init.connection ? bindingOf(init.connection) : d.binding,
    vercelProject: { id: 'prj_inventory', name: 'inventory', url: 'https://inventory.vercel.app', productionUrl: 'https://inventory.vercel.app' },
    gitConnected: true,
    ...init.settings,
  })
  return d
}

export function seedEnvVar(p: StoredProject, overrides: Partial<StoredEnvVar> & Pick<StoredEnvVar, 'key'>): StoredEnvVar {
  const env = (p.deploymentEnv ??= [])
  const v: StoredEnvVar = {
    targets: ['preview', 'production'],
    sensitive: false,
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

/** Appends a deployment. Defaults: a ready preview of the current version by AIDE, one minute after the previous one. */
export function seedDeployment(p: StoredProject, overrides: Partial<Deployment> = {}): Deployment {
  const list = (p.deployments ??= [])
  deploymentClock += 60_000
  const version = currentVersionOf(p)
  const sha = overrides.gitSha ?? `${String(list.length + 1).padStart(2, '0')}a9c1d8e7b6a5f4e3d2c1b0a9f8e7d6c5b4a3f`.slice(0, 40)
  const target = overrides.target ?? 'preview'
  const d: Deployment = {
    id: nextId(),
    target,
    provider: 'vercel',
    status: 'ready',
    url: `https://inventory-${sha.slice(0, 8)}.vercel.app`,
    inspectorUrl: null,
    gitRef: target === 'production' ? 'main' : (version?.branchName ?? 'aide/project/v0.1.0'),
    gitSha: sha,
    releaseVersionId: version?.id ?? '',
    releaseVersion: version?.version ?? '0.1.0',
    releaseId: null,
    errorCode: null,
    errorMessage: null,
    buildLogTail: null,
    trigger: 'auto',
    triggeredBy: null,
    promotedFromDeploymentId: null,
    createdAt: new Date(deploymentClock).toISOString(),
    readyAt: overrides.status && overrides.status !== 'ready' ? null : new Date(deploymentClock + 45_000).toISOString(),
    ...overrides,
  }
  list.push(d)
  return d
}

function vercelDto(c: StoredVercelConnection): VercelConnection {
  return {
    id: c.id,
    label: c.label,
    last4: c.last4,
    accountName: c.accountName,
    teamId: c.teamId,
    teamSlug: c.teamSlug,
    scope: c.scope,
    expiresAt: c.expiresAt,
    status: c.status,
    lastValidatedAt: c.lastValidatedAt,
    createdAt: c.createdAt,
    boundProjects: projectsDb.projects
      .filter((p) => p.deployment?.binding?.connectionId === c.id)
      .map((p) => ({ id: p.id, name: p.name })),
  }
}

function settingsDto(p: StoredProject, canBind: boolean): Settings {
  const d = deploymentOf(p)
  const env = p.deploymentEnv ?? []
  return {
    provider: d.provider,
    supported: d.supported || d.provider !== null,
    binding: d.binding,
    canBind,
    vercelProject: d.vercelProject,
    rootDirectory: d.rootDirectory,
    framework: d.framework,
    requiredEnv: d.requiredEnv.map((r) => ({ ...r, present: env.some((e) => e.key === r.key) })),
    gitConnected: d.gitConnected,
    installUrl: d.installUrl,
  }
}

const envDto = (v: StoredEnvVar): Schemas['DeploymentEnvVarDto'] => ({
  key: v.key,
  targets: [...v.targets],
  sensitive: v.sensitive,
  hasValue: v.hasValue,
  updatedAt: v.updatedAt,
  syncedAt: v.syncedAt,
  syncError: v.syncError,
})

function userMutation(request: Request) {
  const user = currentUser()
  if (!user) {
    return unauthenticated()
  }
  return antiforgeryFailure(request) ?? user
}

const ownConnection = (id: unknown, userId: string) => vercelDb.connections.find((c) => c.id === id && c.userId === userId)
const notFound = (what = 'The connection was not found.') => problem(404, 'NOT_FOUND', what)
const upstream = () => problem(502, 'UPSTREAM_ERROR', 'Vercel could not be reached.')
const VERCEL_PROJECT_NAME = /^(?!.*---)[a-z0-9._-]{1,100}$/
const ENV_KEY = /^[A-Za-z_][A-Za-z0-9_]{0,255}$/

/** The Vercel part shared by POST /projects (`deployment`) and PATCH /settings/deployment (FR-88). */
export function applyVercelSetup(
  p: StoredProject,
  userId: string,
  body: { connectionId?: string | null; vercelProjectId?: string | null; create?: Schemas['CreateVercelProjectRequest'] | null },
): Response | null {
  const d = deploymentOf(p)
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
    connection = vercelDb.connections.find((c) => c.id === body.connectionId)
    if (!connection) {
      return notFound()
    }
    if (connection.userId !== userId) {
      return problem(403, 'CREDENTIAL_NOT_OWNER', 'You can only bind your own connection.')
    }
  }
  if (body.vercelProjectId || body.create) {
    if (vercelDb.down) {
      return upstream()
    }
    if (vercelDb.gitNotConnected) {
      d.gitConnected = false
      d.installUrl = VERCEL_INSTALL_URL
      return problem(409, 'VERCEL_GIT_NOT_CONNECTED', "Vercel's GitHub App can't access the repository.", {
        installUrl: VERCEL_INSTALL_URL,
      })
    }
    if (vercelDb.planLimit) {
      return problem(409, 'VERCEL_PLAN_LIMIT', 'The Hobby plan cannot deploy an organisation repository.')
    }
  }
  d.provider = 'vercel'
  d.supported = true
  if (connection) {
    d.binding = bindingOf(connection)
  }
  if (body.create) {
    d.vercelProject = { id: `prj_${nextId().slice(-6)}`, name: body.create.name, url: `https://${body.create.name}.vercel.app`, productionUrl: null }
    const root = body.create.rootDirectory?.trim() ?? ''
    d.rootDirectory = root === '' ? '.' : root
  } else if (body.vercelProjectId) {
    d.vercelProject = { id: body.vercelProjectId, name: body.vercelProjectId.replace(/^prj_/, ''), url: null, productionUrl: null }
  }
  if (body.vercelProjectId || body.create) {
    d.gitConnected = true
    d.installUrl = null
  }
  return null
}

/** Who may redeploy (FR-90, FR-91): previews need a writer, production an Owner. */
function mayDeploy(a: Exclude<ReturnType<typeof access>, Response>, target: Schemas['DeploymentTarget']) {
  return target === 'preview' ? a.level !== 'viewer' : isOwnerLevel(a)
}

function userRef(userId: string) {
  const user = authDb.users.find((u) => u.id === userId)
  return { id: userId, name: user?.name ?? 'Unknown' }
}

export const deploymentHandlers = [
  // ---- Me → Vercel connections (FR-87) ----
  http.get('*/api/me/connections/vercel', () => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    return HttpResponse.json<VercelConnection[]>(vercelDb.connections.filter((c) => c.userId === user.id).map(vercelDto))
  }),

  http.post('*/api/me/connections/vercel', async ({ request }) => {
    const user = userMutation(request)
    if (user instanceof Response) {
      return user
    }
    const body = (await record(request)) as Schemas['CreateVercelConnectionRequest']
    const errors: Record<string, string[]> = {}
    if (!body.label.trim()) {
      errors.label = ['The label is required.']
    }
    if (!body.token) {
      errors.token = ['The token is required.']
    }
    if (Object.keys(errors).length > 0) {
      return problem(400, 'VALIDATION', 'The connection is invalid.', { errors })
    }
    if (vercelDb.down) {
      return upstream()
    }
    if (vercelDb.rejectedTokens.has(body.token)) {
      return problem(400, 'CREDENTIAL_INVALID', 'Vercel rejected the token.')
    }
    const now = new Date().toISOString()
    // A token naming a team (`team_…`) is team-scoped in the mock.
    const team = body.token.startsWith('team_')
    const created = seedVercelConnection({
      userId: user.id,
      label: body.label.trim(),
      token: body.token,
      accountName: 'ada',
      scope: team ? 'team' : 'account',
      teamId: team ? 'team_acme' : null,
      teamSlug: team ? 'acme' : null,
      expiresAt: '2027-01-01T00:00:00Z',
      lastValidatedAt: now,
      createdAt: now,
    })
    return HttpResponse.json<VercelConnection>(vercelDto(created), { status: 201 })
  }),

  http.patch('*/api/me/connections/vercel/:id', async ({ request, params }) => {
    const user = userMutation(request)
    if (user instanceof Response) {
      return user
    }
    const body = (await record(request)) as Schemas['UpdateLlmCredentialRequest']
    const c = ownConnection(params.id, user.id)
    if (!c) {
      return notFound()
    }
    if (!body.label.trim()) {
      return problem(400, 'VALIDATION', 'The connection is invalid.', { errors: { label: ['The label is required.'] } })
    }
    c.label = body.label.trim()
    return HttpResponse.json<VercelConnection>(vercelDto(c))
  }),

  http.delete('*/api/me/connections/vercel/:id', async ({ request, params }) => {
    const user = userMutation(request)
    if (user instanceof Response) {
      return user
    }
    await record(request)
    const c = ownConnection(params.id, user.id)
    if (!c) {
      return notFound()
    }
    for (const p of projectsDb.projects) {
      if (p.deployment?.binding?.connectionId === c.id) {
        p.deployment.binding = null
      }
    }
    vercelDb.connections = vercelDb.connections.filter((x) => x !== c)
    return new HttpResponse(null, { status: 204 })
  }),

  http.post('*/api/me/connections/vercel/:id/test', async ({ request, params }) => {
    const user = userMutation(request)
    if (user instanceof Response) {
      return user
    }
    await record(request)
    const c = ownConnection(params.id, user.id)
    if (!c) {
      return notFound()
    }
    if (vercelDb.down) {
      return upstream()
    }
    c.status = vercelDb.rejectedTokens.has(c.token) ? 'invalid' : 'valid'
    c.lastValidatedAt = new Date().toISOString()
    return HttpResponse.json<VercelConnection>(vercelDto(c))
  }),

  // ---- Settings → Deployment (FR-88) ----
  http.get('*/api/projects/:id/settings/deployment', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    return HttpResponse.json<Settings>(settingsDto(a.project, a.level === 'owner'))
  }),

  http.patch('*/api/projects/:id/settings/deployment', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['UpdateDeploymentSettingsRequest']
    const d = deploymentOf(a.project)
    if (body.provider === null) {
      d.provider = null
      d.binding = null
      return HttpResponse.json<Settings>(settingsDto(a.project, a.level === 'owner'))
    }
    if (body.connectionId && a.level !== 'owner') {
      return forbidden()
    }
    // Changing the provider or the Vercel project needs the caller's own connection unless they are the binder.
    const changesProject = Boolean(body.vercelProjectId ?? body.create) || d.provider !== body.provider
    if (changesProject && !body.connectionId && d.binding && d.binding.boundBy.id !== a.userId) {
      return problem(403, 'CREDENTIAL_NOT_OWNER', 'Changing the Vercel project needs your own connection.')
    }
    const failed = applyVercelSetup(a.project, a.userId, body)
    if (failed) {
      return failed
    }
    return HttpResponse.json<Settings>(settingsDto(a.project, a.level === 'owner'))
  }),

  http.get('*/api/projects/:id/settings/deployment/env', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    if (!isOwnerLevel(a)) {
      return forbidden()
    }
    return HttpResponse.json<Schemas['DeploymentEnvVarDto'][]>(
      [...(a.project.deploymentEnv ?? [])].sort((x, y) => x.key.localeCompare(y.key)).map(envDto),
    )
  }),

  http.put('*/api/projects/:id/settings/deployment/env/:key', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['PutDeploymentEnvRequest']
    const key = String(params.key)
    const errors: Record<string, string[]> = {}
    if (!ENV_KEY.test(key)) {
      errors.key = ['Use letters, digits and "_", not starting with a digit.']
    }
    if (body.targets.length === 0) {
      errors.targets = ['Pick at least one target.']
    }
    if (!body.value) {
      errors.value = ['Enter a value.']
    }
    if (Object.keys(errors).length > 0) {
      return problem(400, 'VALIDATION', 'The environment variable is invalid.', { errors })
    }
    const env = (a.project.deploymentEnv ??= [])
    const now = new Date().toISOString()
    let v = env.find((e) => e.key === key)
    if (!v) {
      v = { key, targets: [], sensitive: false, hasValue: true, updatedAt: now, syncedAt: null, syncError: null, value: '' }
      env.push(v)
    }
    Object.assign(v, { value: body.value, targets: body.targets, sensitive: body.sensitive ?? false, updatedAt: now })
    if (vercelDb.envSyncFails) {
      v.syncedAt = null
      v.syncError = 'Vercel answered 500 while saving the variable.'
      return upstream()
    }
    v.syncedAt = now
    v.syncError = null
    return HttpResponse.json<Schemas['DeploymentEnvVarDto']>(envDto(v))
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
    if (vercelDb.envDeleteFails) {
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
    const target = q.get('target')
    const versionId = q.get('releaseVersionId')
    return HttpResponse.json<Deployment[]>(
      [...(a.project.deployments ?? [])]
        .filter((d) => !target || d.target === target)
        .filter((d) => !versionId || d.releaseVersionId === versionId)
        .sort((x, y) => y.createdAt.localeCompare(x.createdAt)),
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
    const d = a.project.deployment
    if (!d?.provider || !d.binding) {
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
    if (!a.project.deployment?.provider || !a.project.deployment.binding) {
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

  http.post('*/api/projects/:id/deployments/:deploymentId/promote', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    const source = a.project.deployments?.find((d) => d.id === params.deploymentId)
    if (!source) {
      return notFound('The deployment was not found.')
    }
    if (source.target !== 'production' || source.status !== 'ready') {
      return problem(409, 'INVALID_DEPLOYMENT', 'Only a ready production deployment can be promoted.')
    }
    const created = seedDeployment(a.project, {
      target: 'production',
      gitRef: source.gitRef,
      gitSha: source.gitSha,
      releaseVersionId: source.releaseVersionId,
      releaseVersion: source.releaseVersion,
      releaseId: source.releaseId,
      url: source.url,
      status: 'ready',
      trigger: 'promote',
      triggeredBy: userRef(a.userId),
      promotedFromDeploymentId: source.id,
      createdAt: new Date().toISOString(),
    })
    return HttpResponse.json<Deployment>(created, { status: 202 })
  }),
]
