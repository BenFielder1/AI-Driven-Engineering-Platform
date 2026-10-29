import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { antiforgeryFailure, authDb, currentUser, nextId, unauthenticated } from './auth'
import { problem } from './problem'

type Instance = Schemas['AdminGitLabInstanceDto']
type Connection = Schemas['GitLabConnectionDto']
type Repo = Schemas['GitLabRepoDto']
type Owner = Schemas['GitLabOwnerDto']
type PendingLink = Schemas['GitLabPendingLinkDto']

export const GITLAB_COM_ID = '00000000-0000-7000-9000-000000000001'
export const GITLAB_CALLBACK_URL = 'http://localhost:3000/api/auth/gitlab/callback'
const PAGE_SIZE = 30

/** gitlab.com is always listed (FR-130); without an OAuth application only token connections work. */
const gitlabCom = (): Instance => ({
  id: GITLAB_COM_ID,
  name: 'GitLab.com',
  baseUrl: 'https://gitlab.com',
  isGitLabCom: true,
  source: 'database',
  clientId: null,
  hasClientSecret: false,
  oauthConfigured: false,
  allowInsecureHttp: false,
  enabled: true,
  version: null,
  versionStatus: 'unknown',
  versionWarning: null,
  connectionCount: 0,
  projectCount: 0,
  createdAt: '2026-10-28T10:00:00Z',
  updatedAt: '2026-10-28T10:00:00Z',
})

/** In-memory GitLab backend for tests (reset after each test by src/test/setup.ts). */
export const gitlabDb = {
  instances: [gitlabCom()],
  /** Connections by AIDE user id. */
  connections: {} as Record<string, Connection[]>,
  /** Projects per connection id (the picker lists them newest first). */
  repos: {} as Record<string, Repo[]>,
  owners: {} as Record<string, Owner[]>,
  /** The sign-in waiting in this browser to be linked (FR-131), and the email of the account it belongs to. */
  pendingLink: null as PendingLink | null,
  /** Request bodies, for assertions. */
  createdInstances: [] as Schemas['CreateGitLabInstanceRequest'][],
  updates: [] as { id: string; body: Schemas['UpdateGitLabInstanceRequest'] }[],
  tokenRequests: [] as Schemas['CreateGitLabTokenConnectionRequest'][],
  createdRepos: [] as Schemas['CreateGitLabRepoRequest'][],
  repoQueries: [] as { connectionId: string; search: string | null; cursor: string | null }[],
  linkConfirmed: false,
  /** The version the next reachability check reports (`null`: 401 unauthenticated). */
  nextVersion: '17.11.2' as string | null,
  /** Answer the picker endpoints with 429 UPSTREAM_RATE_LIMITED. */
  rateLimited: false,
}

export function resetGitLabDb() {
  gitlabDb.instances = [gitlabCom()]
  gitlabDb.connections = {}
  gitlabDb.repos = {}
  gitlabDb.owners = {}
  gitlabDb.pendingLink = null
  gitlabDb.createdInstances = []
  gitlabDb.updates = []
  gitlabDb.tokenRequests = []
  gitlabDb.createdRepos = []
  gitlabDb.repoQueries = []
  gitlabDb.linkConfirmed = false
  gitlabDb.nextVersion = '17.11.2'
  gitlabDb.rateLimited = false
  syncSignIn()
}

/** "Continue with GitLab" on /login: the enabled instances with an OAuth application (GET /api/auth/providers). */
function syncSignIn() {
  authDb.gitlabSignIn = gitlabDb.instances
    .filter((i) => i.enabled && i.oauthConfigured)
    .map(({ id, name, baseUrl, isGitLabCom, oauthConfigured }) => ({ id, name, baseUrl, isGitLabCom, oauthConfigured }))
}

function versionVerdict(version: string | null): Pick<Instance, 'versionStatus' | 'versionWarning'> {
  if (!version) {
    return { versionStatus: 'unknown', versionWarning: null }
  }
  const [major = 0, minor = 0] = version.split('.').map(Number)
  return major > 17 || (major === 17 && minor >= 11)
    ? { versionStatus: 'supported', versionWarning: null }
    : { versionStatus: 'old', versionWarning: `GitLab ${version} is older than 17.11; some features may not work.` }
}

/** Adds (or changes) an instance, as the admin page or `GitLab__Instances__*` configuration would. */
export function seedGitLabInstance(overrides: Partial<Instance> = {}): Instance {
  const existing = overrides.id ? gitlabDb.instances.find((i) => i.id === overrides.id) : undefined
  if (existing) {
    Object.assign(existing, overrides)
    syncSignIn()
    return existing
  }
  const instance: Instance = {
    ...gitlabCom(),
    id: nextId(),
    name: 'Acme GitLab',
    baseUrl: 'https://gitlab.acme.example',
    isGitLabCom: false,
    clientId: 'client-acme',
    hasClientSecret: true,
    oauthConfigured: true,
    version: '17.11.2',
    versionStatus: 'supported',
    ...overrides,
  }
  gitlabDb.instances.push(instance)
  syncSignIn()
  return instance
}

/** Gives gitlab.com an OAuth application (sign-in and OAuth connect offered). */
export function configureGitLabComOAuth(): Instance {
  return seedGitLabInstance({ id: GITLAB_COM_ID, clientId: 'client-gitlab-com', hasClientSecret: true, oauthConfigured: true })
}

export function seedGitLabConnection(userId: string, overrides: Partial<Connection> = {}): Connection {
  const instance = gitlabDb.instances.find((i) => i.id === (overrides.instanceId ?? GITLAB_COM_ID)) ?? gitlabCom()
  const connection: Connection = {
    id: nextId(),
    instanceId: instance.id,
    instanceName: instance.name,
    instanceBaseUrl: instance.baseUrl,
    instanceEnabled: instance.enabled,
    login: 'ada',
    externalUserId: '4242',
    authKind: 'oauth',
    scopes: ['api', 'write_repository', 'openid', 'profile', 'email'],
    expiresAt: null,
    expiryWarning: false,
    status: 'valid',
    lastValidatedAt: '2026-10-28T12:00:00Z',
    createdAt: '2026-10-28T12:00:00Z',
    updatedAt: '2026-10-28T12:00:00Z',
    ...overrides,
  }
  ;(gitlabDb.connections[userId] ??= []).unshift(connection)
  gitlabDb.owners[connection.id] ??= [
    { id: 1, kind: 'user', fullPath: connection.login, name: 'Ada Lovelace', canCreateProjects: true, reason: null },
    { id: 2, kind: 'group', fullPath: 'acme', name: 'Acme', canCreateProjects: true, reason: null },
    {
      id: 3,
      kind: 'group',
      fullPath: 'locked',
      name: 'Locked group',
      canCreateProjects: false,
      reason: 'Only Maintainers can create projects in this group.',
    },
  ]
  return connection
}

export function seedGitLabRepo(connectionId: string, overrides: Partial<Repo> = {}): Repo {
  const list = (gitlabDb.repos[connectionId] ??= [])
  const path = overrides.path ?? `repo-${String(list.length + 1)}`
  const namespace = overrides.namespaceFullPath ?? 'acme'
  const repo: Repo = {
    id: String(5000 + list.length),
    fullPath: `${namespace}/${path}`,
    name: path,
    path,
    defaultBranch: 'main',
    webUrl: `https://gitlab.com/${namespace}/${path}`,
    namespaceFullPath: namespace,
    visibility: 'private',
    lastActivityAt: '2026-10-28T12:00:00Z',
    ...overrides,
  }
  list.push(repo)
  return repo
}

const isAdminUser = () => currentUser()?.systemRole === 'admin'
const forbidden = () => problem(403, 'FORBIDDEN', 'Admins only.')

function overview(): Schemas['AdminGitLabIntegrationDto'] {
  return {
    callbackUrl: GITLAB_CALLBACK_URL,
    scopes: ['api', 'write_repository', 'openid', 'profile', 'email'],
    minimumVersion: '17.11',
    allowPrivateNetworks: true,
    instances: gitlabDb.instances,
  }
}

/** The SSRF guard's refusals the tests use (link-local and metadata addresses). */
const refusedHost = (url: string) => /169\.254\.|localhost|127\.0\.0\.1/.test(url)

function myConnection(id: string | null): Connection | undefined {
  const user = currentUser()
  return user && id ? (gitlabDb.connections[user.id] ?? []).find((c) => c.id === id) : undefined
}

/** Shared picker guards: signed in, the caller's connection, usable, not rate limited. */
function pickerGuard(connectionId: string | null): Response | Connection {
  if (!currentUser()) {
    return unauthenticated()
  }
  const connection = myConnection(connectionId)
  if (!connection) {
    return problem(404, 'NOT_FOUND', 'The GitLab connection was not found.')
  }
  if (connection.status === 'invalid' || !connection.instanceEnabled) {
    return problem(409, 'GIT_NOT_CONNECTED', 'The GitLab connection is invalid, or its instance is disabled.')
  }
  if (gitlabDb.rateLimited) {
    return problem(429, 'UPSTREAM_RATE_LIMITED', 'GitLab is rate limiting requests.', { headers: { 'Retry-After': '60' } })
  }
  return connection
}

export const gitlabHandlers = [
  // ---- Admin (FR-130) ------------------------------------------------------------------------------------------
  http.get('*/api/admin/integrations/gitlab', () => {
    if (!currentUser()) {
      return unauthenticated()
    }
    return isAdminUser() ? HttpResponse.json(overview()) : forbidden()
  }),

  http.post('*/api/admin/integrations/gitlab', async ({ request }) => {
    if (!isAdminUser()) {
      return currentUser() ? forbidden() : unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const body = (await request.json()) as Schemas['CreateGitLabInstanceRequest']
    gitlabDb.createdInstances.push(body)
    if (refusedHost(body.baseUrl)) {
      return problem(400, 'GIT_HOST_NOT_ALLOWED', "That address isn't allowed.")
    }
    if (body.baseUrl.startsWith('http://') && !body.allowInsecureHttp) {
      return problem(400, 'VALIDATION', 'One or more validation errors occurred.', {
        errors: { baseUrl: ['Use https://, or tick "Allow insecure HTTP" for a lab host.'] },
      })
    }
    const baseUrl = body.baseUrl.replace(/\/+$/, '')
    if (gitlabDb.instances.some((i) => i.baseUrl === baseUrl)) {
      return problem(409, 'CONFLICT', 'That GitLab instance is already registered.')
    }
    if (baseUrl.includes('unreachable')) {
      return problem(502, 'UPSTREAM_ERROR', "The host didn't answer like GitLab.")
    }
    const clientId = body.clientId ?? null
    const hasClientSecret = Boolean(body.clientSecret)
    const instance = seedGitLabInstance({
      name: body.name,
      baseUrl,
      clientId,
      hasClientSecret,
      oauthConfigured: Boolean(clientId) && hasClientSecret,
      allowInsecureHttp: body.allowInsecureHttp ?? false,
      enabled: body.enabled ?? true,
      version: gitlabDb.nextVersion,
      ...versionVerdict(gitlabDb.nextVersion),
    })
    return HttpResponse.json(instance, { status: 201 })
  }),

  http.patch('*/api/admin/integrations/gitlab/:instanceId', async ({ request, params }) => {
    if (!isAdminUser()) {
      return currentUser() ? forbidden() : unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const instance = gitlabDb.instances.find((i) => i.id === params.instanceId)
    if (!instance) {
      return problem(404, 'NOT_FOUND', 'The GitLab instance was not found.')
    }
    const body = (await request.json()) as Schemas['UpdateGitLabInstanceRequest']
    gitlabDb.updates.push({ id: instance.id, body })
    const onlyEnabled = Object.keys(body).every((k) => k === 'enabled')
    if (instance.source === 'configuration' && !onlyEnabled) {
      return problem(409, 'CONFLICT', 'Configured instances only accept enabled.')
    }
    if (body.baseUrl && refusedHost(body.baseUrl)) {
      return problem(400, 'GIT_HOST_NOT_ALLOWED', "That address isn't allowed.")
    }
    if (body.name != null) {
      instance.name = body.name
    }
    if (body.baseUrl != null && !instance.isGitLabCom) {
      instance.baseUrl = body.baseUrl.replace(/\/+$/, '')
    }
    if (body.clientId != null) {
      instance.clientId = body.clientId || null
    }
    if (body.clientSecret != null) {
      instance.hasClientSecret = body.clientSecret !== ''
    }
    if (body.allowInsecureHttp != null) {
      instance.allowInsecureHttp = body.allowInsecureHttp
    }
    if (body.enabled != null) {
      instance.enabled = body.enabled
    }
    instance.oauthConfigured = Boolean(instance.clientId) && instance.hasClientSecret
    instance.updatedAt = new Date().toISOString()
    syncSignIn()
    return HttpResponse.json(instance)
  }),

  http.post('*/api/admin/integrations/gitlab/:instanceId/check', ({ request, params }) => {
    if (!isAdminUser()) {
      return currentUser() ? forbidden() : unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const instance = gitlabDb.instances.find((i) => i.id === params.instanceId)
    if (!instance) {
      return problem(404, 'NOT_FOUND', 'The GitLab instance was not found.')
    }
    if (instance.baseUrl.includes('unreachable')) {
      return problem(502, 'UPSTREAM_ERROR', "The host didn't answer like GitLab.")
    }
    Object.assign(instance, { version: gitlabDb.nextVersion, ...versionVerdict(gitlabDb.nextVersion) })
    return HttpResponse.json(instance)
  }),

  http.delete('*/api/admin/integrations/gitlab/:instanceId', ({ request, params }) => {
    if (!isAdminUser()) {
      return currentUser() ? forbidden() : unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const instance = gitlabDb.instances.find((i) => i.id === params.instanceId)
    if (!instance) {
      return problem(404, 'NOT_FOUND', 'The GitLab instance was not found.')
    }
    if (instance.projectCount > 0 || instance.isGitLabCom || instance.source === 'configuration') {
      return problem(409, 'CONFLICT', 'Projects use this instance.')
    }
    gitlabDb.instances = gitlabDb.instances.filter((i) => i.id !== instance.id)
    syncSignIn()
    return new HttpResponse(null, { status: 204 })
  }),

  // ---- Sign-in link (FR-131) -----------------------------------------------------------------------------------
  http.get('*/api/auth/gitlab/link', () =>
    gitlabDb.pendingLink
      ? HttpResponse.json(gitlabDb.pendingLink)
      : problem(404, 'NOT_FOUND', "There's no GitLab sign-in waiting to be linked."),
  ),

  http.post('*/api/auth/gitlab/link', ({ request }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const link = gitlabDb.pendingLink
    if (!link) {
      return problem(404, 'NOT_FOUND', "There's no GitLab sign-in waiting to be linked.")
    }
    if (user.email.toLowerCase() !== link.email.toLowerCase()) {
      return problem(403, 'FORBIDDEN', 'Signed in as a different account.')
    }
    gitlabDb.pendingLink = null
    gitlabDb.linkConfirmed = true
    seedGitLabConnection(user.id, { instanceId: link.instanceId, login: link.login })
    return new HttpResponse(null, { status: 204 })
  }),

  http.delete('*/api/auth/gitlab/link', () => {
    gitlabDb.pendingLink = null
    return new HttpResponse(null, { status: 204 })
  }),

  // ---- Connections (FR-131) ------------------------------------------------------------------------------------
  http.get('*/api/me/connections/gitlab/instances', () => {
    if (!currentUser()) {
      return unauthenticated()
    }
    return HttpResponse.json<Schemas['GitLabInstanceSummaryDto'][]>(
      gitlabDb.instances
        .filter((i) => i.enabled)
        .map(({ id, name, baseUrl, isGitLabCom, oauthConfigured }) => ({ id, name, baseUrl, isGitLabCom, oauthConfigured })),
    )
  }),

  http.get('*/api/me/connections/gitlab', () => {
    const user = currentUser()
    return user ? HttpResponse.json<Connection[]>(gitlabDb.connections[user.id] ?? []) : unauthenticated()
  }),

  http.post('*/api/me/connections/gitlab', async ({ request }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const body = (await request.json()) as Schemas['CreateGitLabTokenConnectionRequest']
    gitlabDb.tokenRequests.push(body)
    const instance = gitlabDb.instances.find((i) => i.id === (body.instanceId ?? GITLAB_COM_ID) && i.enabled)
    if (!instance) {
      return problem(404, 'NOT_FOUND', 'No such enabled instance.')
    }
    if (body.token.startsWith('bad')) {
      return problem(400, 'CREDENTIAL_INVALID', 'GitLab rejected the token.')
    }
    if (body.token.startsWith('readonly')) {
      return problem(400, 'CONNECTION_SCOPE_INSUFFICIENT', 'This token is missing permissions: api, write_repository.', {
        details: ['api', 'write_repository'],
      })
    }
    const connection = seedGitLabConnection(user.id, {
      instanceId: instance.id,
      authKind: 'token',
      scopes: ['api', 'write_repository'],
      expiresAt: '2027-01-28T00:00:00Z',
    })
    return HttpResponse.json(connection, { status: 201 })
  }),

  http.delete('*/api/me/connections/gitlab/:id', ({ request, params }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const list = gitlabDb.connections[user.id] ?? []
    if (!list.some((c) => c.id === params.id)) {
      return problem(404, 'NOT_FOUND', 'The GitLab connection was not found.')
    }
    gitlabDb.connections[user.id] = list.filter((c) => c.id !== params.id)
    return new HttpResponse(null, { status: 204 })
  }),

  http.post('*/api/me/connections/gitlab/:id/test', ({ request, params }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const connection = myConnection(typeof params.id === 'string' ? params.id : null)
    if (!connection) {
      return problem(404, 'NOT_FOUND', 'The GitLab connection was not found.')
    }
    connection.lastValidatedAt = new Date().toISOString()
    return HttpResponse.json(connection)
  }),

  // ---- Repo picker and Create project (FR-132) -----------------------------------------------------------------
  http.get('*/api/me/git/gitlab/repos', ({ request }) => {
    const url = new URL(request.url)
    const connectionId = url.searchParams.get('connectionId')
    const search = url.searchParams.get('search')
    const cursor = url.searchParams.get('cursor')
    gitlabDb.repoQueries.push({ connectionId: connectionId ?? '', search, cursor })
    const guard = pickerGuard(connectionId)
    if (guard instanceof Response) {
      return guard
    }
    const all = (gitlabDb.repos[guard.id] ?? []).filter((r) => !search || r.name.toLowerCase().includes(search.toLowerCase()))
    const start = cursor ? Number(cursor) : 0
    const items = all.slice(start, start + PAGE_SIZE)
    const nextCursor = start + PAGE_SIZE < all.length ? String(start + PAGE_SIZE) : null
    return HttpResponse.json<Schemas['GitLabRepoPageDto']>({ items, nextCursor })
  }),

  http.get('*/api/me/git/gitlab/owners', ({ request }) => {
    const connectionId = new URL(request.url).searchParams.get('connectionId')
    const guard = pickerGuard(connectionId)
    if (guard instanceof Response) {
      return guard
    }
    return HttpResponse.json<Owner[]>(gitlabDb.owners[guard.id] ?? [])
  }),

  http.post('*/api/me/git/gitlab/repos', async ({ request }) => {
    if (currentUser()) {
      const rejected = antiforgeryFailure(request)
      if (rejected) {
        return rejected
      }
    }
    const body = (await request.json()) as Schemas['CreateGitLabRepoRequest']
    gitlabDb.createdRepos.push(body)
    const guard = pickerGuard(body.connectionId)
    if (guard instanceof Response) {
      return guard
    }
    const owner = (gitlabDb.owners[guard.id] ?? []).find((o) => o.fullPath === body.owner)
    if (!owner) {
      return problem(404, 'NOT_FOUND', 'No such group.')
    }
    if (!owner.canCreateProjects) {
      return problem(403, 'FORBIDDEN', "GitLab doesn't let the caller create projects there.")
    }
    if (body.name === 'limit') {
      return problem(429, 'UPSTREAM_RATE_LIMITED', 'The GitLab.com project-creation limit.', { headers: { 'Retry-After': '600' } })
    }
    const path = body.path ?? body.name.toLowerCase().replace(/[^a-z0-9_.-]+/g, '-')
    if ((gitlabDb.repos[guard.id] ?? []).some((r) => r.fullPath === `${owner.fullPath}/${path}`)) {
      return problem(409, 'REPO_NAME_TAKEN', 'A project with that name or path already exists.')
    }
    const repo = seedGitLabRepo(guard.id, {
      name: body.name,
      path,
      namespaceFullPath: owner.fullPath,
      fullPath: `${owner.fullPath}/${path}`,
      visibility: body.private === false ? 'public' : 'private',
    })
    return HttpResponse.json(repo, { status: 201 })
  }),
]
