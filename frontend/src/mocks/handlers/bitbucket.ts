import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { antiforgeryFailure, authDb, currentUser, nextId, unauthenticated } from './auth'
import { problem } from './problem'

type Integration = Schemas['AdminBitbucketIntegrationDto']
type Connection = Schemas['BitbucketConnectionDto']
type Repo = Schemas['BitbucketRepoDto']
type Workspace = Schemas['BitbucketWorkspaceDto']
type PendingLink = Schemas['BitbucketPendingLinkDto']

export const BITBUCKET_CALLBACK_URL = 'http://localhost:3000/api/auth/bitbucket/callback'
const PAGE_SIZE = 30

/** The nine API-token scopes (FR-134), as the API reports them. */
export const BITBUCKET_MOCK_TOKEN_SCOPES = [
  'read:user:bitbucket',
  'read:workspace:bitbucket',
  'read:project:bitbucket',
  'read:repository:bitbucket',
  'write:repository:bitbucket',
  'admin:repository:bitbucket',
  'read:pullrequest:bitbucket',
  'write:pullrequest:bitbucket',
  'read:pipeline:bitbucket',
]

const noConsumer = (): Integration => ({
  callbackUrl: BITBUCKET_CALLBACK_URL,
  permissions: ['account', 'email', 'repository:write', 'repository:admin', 'pullrequest:write', 'project', 'webhook'],
  source: 'database',
  clientId: null,
  hasClientSecret: false,
  oauthConfigured: false,
  connectionCount: 0,
  updatedAt: '2026-10-29T10:00:00Z',
})

/** In-memory Bitbucket backend for tests (reset after each test by src/test/setup.ts). */
export const bitbucketDb = {
  consumer: noConsumer(),
  /** Connections by AIDE user id. */
  connections: {} as Record<string, Connection[]>,
  /** Repositories per connection id (the picker lists them in this order). */
  repos: {} as Record<string, Repo[]>,
  owners: {} as Record<string, Workspace[]>,
  /** The owners list was capped at 50 workspaces (`truncated`). */
  ownersTruncated: false,
  /** The sign-in waiting in this browser to be linked (FR-134). */
  pendingLink: null as PendingLink | null,
  /** Request bodies, for assertions. */
  consumerUpdates: [] as Schemas['UpdateBitbucketConsumerRequest'][],
  tokenRequests: [] as Schemas['CreateBitbucketTokenConnectionRequest'][],
  createdRepos: [] as Schemas['CreateBitbucketRepoRequest'][],
  repoQueries: [] as { connectionId: string; search: string | null; cursor: string | null }[],
  linkConfirmed: false,
  /** Answer the picker endpoints with 429 UPSTREAM_RATE_LIMITED (Retry-After: this many seconds). */
  rateLimitedFor: null as number | null,
}

export function resetBitbucketDb() {
  bitbucketDb.consumer = noConsumer()
  bitbucketDb.connections = {}
  bitbucketDb.repos = {}
  bitbucketDb.owners = {}
  bitbucketDb.ownersTruncated = false
  bitbucketDb.pendingLink = null
  bitbucketDb.consumerUpdates = []
  bitbucketDb.tokenRequests = []
  bitbucketDb.createdRepos = []
  bitbucketDb.repoQueries = []
  bitbucketDb.linkConfirmed = false
  bitbucketDb.rateLimitedFor = null
  syncSignIn()
}

/** "Continue with Bitbucket" on /login (GET /api/auth/providers `bitbucket`). */
function syncSignIn() {
  authDb.bitbucketSignIn = bitbucketDb.consumer.oauthConfigured
}

/** Sets the OAuth consumer, as the admin page or `Bitbucket__OAuth__*` configuration would. */
export function configureBitbucketConsumer(overrides: Partial<Integration> = {}): Integration {
  bitbucketDb.consumer = {
    ...bitbucketDb.consumer,
    clientId: 'bb-key',
    hasClientSecret: true,
    oauthConfigured: true,
    ...overrides,
  }
  syncSignIn()
  return bitbucketDb.consumer
}

export function seedBitbucketConnection(userId: string, overrides: Partial<Connection> = {}): Connection {
  const connection: Connection = {
    id: nextId(),
    login: 'ada-bb',
    externalUserId: '{4242-uuid}',
    authKind: 'oauth',
    scopes: ['account', 'email', 'repository:write', 'repository:admin', 'pullrequest:write', 'project', 'webhook'],
    status: 'valid',
    lastValidatedAt: '2026-10-29T12:00:00Z',
    createdAt: '2026-10-29T12:00:00Z',
    updatedAt: '2026-10-29T12:00:00Z',
    ...overrides,
  }
  ;(bitbucketDb.connections[userId] ??= []).unshift(connection)
  bitbucketDb.owners[connection.id] ??= [
    {
      slug: 'acme',
      name: 'Acme',
      isAdmin: true,
      projects: [
        { key: 'TODO', name: 'Todo', isPrivate: true },
        { key: 'WEB', name: 'Website', isPrivate: false },
      ],
      projectsTruncated: false,
    },
    { slug: 'empty-ws', name: 'Empty workspace', isAdmin: false, projects: [], projectsTruncated: false },
  ]
  return connection
}

export function seedBitbucketRepo(connectionId: string, overrides: Partial<Repo> = {}): Repo {
  const list = (bitbucketDb.repos[connectionId] ??= [])
  const slug = overrides.slug ?? `repo-${String(list.length + 1)}`
  const workspace = overrides.workspace ?? 'acme'
  const repo: Repo = {
    id: `{bb-${String(1000 + list.length)}}`,
    fullName: `${workspace}/${slug}`,
    name: slug,
    slug,
    workspace,
    projectKey: 'TODO',
    mainBranch: 'main',
    isPrivate: true,
    webUrl: `https://bitbucket.org/${workspace}/${slug}`,
    updatedOn: '2026-10-29T12:00:00Z',
    ...overrides,
  }
  list.push(repo)
  return repo
}

const isAdminUser = () => currentUser()?.systemRole === 'admin'
const forbidden = () => problem(403, 'FORBIDDEN', 'Admins only.')

function myConnection(id: string | null): Connection | undefined {
  const user = currentUser()
  return user && id ? (bitbucketDb.connections[user.id] ?? []).find((c) => c.id === id) : undefined
}

function connectionCount(): number {
  return Object.values(bitbucketDb.connections).reduce((n, list) => n + list.length, 0)
}

/** Shared picker guards: signed in, the caller's connection, usable, not rate limited. */
function pickerGuard(connectionId: string | null): Response | Connection {
  if (!currentUser()) {
    return unauthenticated()
  }
  const connection = myConnection(connectionId)
  if (!connection) {
    return problem(404, 'NOT_FOUND', 'The Bitbucket connection was not found.')
  }
  if (connection.status === 'invalid') {
    return problem(409, 'GIT_NOT_CONNECTED', 'The Bitbucket connection is invalid.')
  }
  if (bitbucketDb.rateLimitedFor !== null) {
    return problem(429, 'UPSTREAM_RATE_LIMITED', 'Bitbucket is rate limiting requests.', {
      headers: { 'Retry-After': String(bitbucketDb.rateLimitedFor) },
    })
  }
  return connection
}

/** Bitbucket's slug of a repository name. */
const slugOf = (name: string) =>
  name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9_.-]+/g, '-')
    .replace(/^-+|-+$/g, '')

export const bitbucketHandlers = [
  // ---- Admin (FR-133) ------------------------------------------------------------------------------------------
  http.get('*/api/admin/integrations/bitbucket', () => {
    if (!currentUser()) {
      return unauthenticated()
    }
    return isAdminUser() ? HttpResponse.json({ ...bitbucketDb.consumer, connectionCount: connectionCount() }) : forbidden()
  }),

  http.put('*/api/admin/integrations/bitbucket', async ({ request }) => {
    if (!isAdminUser()) {
      return currentUser() ? forbidden() : unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const body = (await request.json()) as Schemas['UpdateBitbucketConsumerRequest']
    bitbucketDb.consumerUpdates.push(body)
    if (bitbucketDb.consumer.source === 'configuration') {
      return problem(409, 'CONFLICT', 'The consumer is configured in the environment.')
    }
    const hasClientSecret = body.clientSecret ? true : bitbucketDb.consumer.hasClientSecret
    if (!hasClientSecret) {
      return problem(400, 'VALIDATION', 'One or more validation errors occurred.', {
        errors: { clientSecret: ['Enter the consumer secret.'] },
      })
    }
    configureBitbucketConsumer({ clientId: body.clientId, hasClientSecret, updatedAt: new Date().toISOString() })
    return HttpResponse.json({ ...bitbucketDb.consumer, connectionCount: connectionCount() })
  }),

  http.delete('*/api/admin/integrations/bitbucket', ({ request }) => {
    if (!isAdminUser()) {
      return currentUser() ? forbidden() : unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    if (bitbucketDb.consumer.source === 'configuration') {
      return problem(409, 'CONFLICT', 'The consumer is configured in the environment.')
    }
    bitbucketDb.consumer = noConsumer()
    syncSignIn()
    return new HttpResponse(null, { status: 204 })
  }),

  // ---- Sign-in link (FR-134) -----------------------------------------------------------------------------------
  http.get('*/api/auth/bitbucket/link', () =>
    bitbucketDb.pendingLink
      ? HttpResponse.json(bitbucketDb.pendingLink)
      : problem(404, 'NOT_FOUND', "There's no Bitbucket sign-in waiting to be linked."),
  ),

  http.post('*/api/auth/bitbucket/link', ({ request }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const link = bitbucketDb.pendingLink
    if (!link) {
      return problem(404, 'NOT_FOUND', "There's no Bitbucket sign-in waiting to be linked.")
    }
    if (user.email.toLowerCase() !== link.email.toLowerCase()) {
      return problem(403, 'FORBIDDEN', 'Signed in as a different account.')
    }
    bitbucketDb.pendingLink = null
    bitbucketDb.linkConfirmed = true
    seedBitbucketConnection(user.id, { login: link.login })
    return new HttpResponse(null, { status: 204 })
  }),

  http.delete('*/api/auth/bitbucket/link', () => {
    bitbucketDb.pendingLink = null
    return new HttpResponse(null, { status: 204 })
  }),

  // ---- Connections (FR-134) ------------------------------------------------------------------------------------
  http.get('*/api/me/connections/bitbucket', () => {
    const user = currentUser()
    return user ? HttpResponse.json<Connection[]>(bitbucketDb.connections[user.id] ?? []) : unauthenticated()
  }),

  http.post('*/api/me/connections/bitbucket', async ({ request }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const body = (await request.json()) as Schemas['CreateBitbucketTokenConnectionRequest']
    bitbucketDb.tokenRequests.push(body)
    if (body.token.startsWith('bad')) {
      return problem(400, 'CREDENTIAL_INVALID', 'Bitbucket rejected the email and token.')
    }
    // FR-134 Given: a token without write:pullrequest is refused with that scope listed, and nothing is saved.
    if (body.token.startsWith('nopr')) {
      return problem(400, 'CONNECTION_SCOPE_INSUFFICIENT', 'This token is missing permissions: write:pullrequest:bitbucket.', {
        details: ['write:pullrequest:bitbucket'],
      })
    }
    // Scopes AIDE couldn't confirm (no repository to probe) are listed apart, and nothing is saved either.
    if (body.token.startsWith('unverif')) {
      return problem(400, 'CONNECTION_SCOPE_INSUFFICIENT', 'Some permissions could not be confirmed.', {
        details: [],
        unverifiableScopes: ['write:repository:bitbucket', 'admin:repository:bitbucket'],
      })
    }
    if (body.token.startsWith('busy')) {
      return problem(429, 'UPSTREAM_RATE_LIMITED', 'Bitbucket is rate limiting requests.', { headers: { 'Retry-After': '300' } })
    }
    const connection = seedBitbucketConnection(user.id, {
      login: body.email.split('@')[0] ?? 'ada',
      authKind: 'token',
      scopes: BITBUCKET_MOCK_TOKEN_SCOPES,
    })
    return HttpResponse.json(connection, { status: 201 })
  }),

  http.delete('*/api/me/connections/bitbucket/:id', ({ request, params }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const list = bitbucketDb.connections[user.id] ?? []
    if (!list.some((c) => c.id === params.id)) {
      return problem(404, 'NOT_FOUND', 'The Bitbucket connection was not found.')
    }
    bitbucketDb.connections[user.id] = list.filter((c) => c.id !== params.id)
    return new HttpResponse(null, { status: 204 })
  }),

  http.post('*/api/me/connections/bitbucket/:id/test', ({ request, params }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const connection = myConnection(typeof params.id === 'string' ? params.id : null)
    if (!connection) {
      return problem(404, 'NOT_FOUND', 'The Bitbucket connection was not found.')
    }
    connection.lastValidatedAt = new Date().toISOString()
    return HttpResponse.json(connection)
  }),

  // ---- Repo picker and Create repository (FR-135) --------------------------------------------------------------
  http.get('*/api/me/git/bitbucket/repos', ({ request }) => {
    const url = new URL(request.url)
    const connectionId = url.searchParams.get('connectionId')
    const search = url.searchParams.get('search')
    const cursor = url.searchParams.get('cursor')
    bitbucketDb.repoQueries.push({ connectionId: connectionId ?? '', search, cursor })
    const guard = pickerGuard(connectionId)
    if (guard instanceof Response) {
      return guard
    }
    const all = (bitbucketDb.repos[guard.id] ?? []).filter(
      (r) => !search || r.name.toLowerCase().includes(search.toLowerCase()),
    )
    // The real cursor is opaque (workspace and page); the mock's is an offset.
    const start = cursor ? Number(cursor.replace('opaque-', '')) : 0
    const items = all.slice(start, start + PAGE_SIZE)
    const nextCursor = start + PAGE_SIZE < all.length ? `opaque-${String(start + PAGE_SIZE)}` : null
    return HttpResponse.json<Schemas['BitbucketRepoPageDto']>({ items, nextCursor })
  }),

  http.get('*/api/me/git/bitbucket/owners', ({ request }) => {
    const connectionId = new URL(request.url).searchParams.get('connectionId')
    const guard = pickerGuard(connectionId)
    if (guard instanceof Response) {
      return guard
    }
    return HttpResponse.json<Schemas['BitbucketOwnersDto']>({ workspaces: bitbucketDb.owners[guard.id] ?? [], truncated: bitbucketDb.ownersTruncated })
  }),

  http.post('*/api/me/git/bitbucket/repos', async ({ request }) => {
    if (currentUser()) {
      const rejected = antiforgeryFailure(request)
      if (rejected) {
        return rejected
      }
    }
    const body = (await request.json()) as Schemas['CreateBitbucketRepoRequest']
    bitbucketDb.createdRepos.push(body)
    const guard = pickerGuard(body.connectionId)
    if (guard instanceof Response) {
      return guard
    }
    const workspace = (bitbucketDb.owners[guard.id] ?? []).find((w) => w.slug === body.owner)
    if (!workspace?.projects.some((p) => p.key === body.project)) {
      return problem(404, 'NOT_FOUND', 'No such workspace or project.')
    }
    if (workspace.isAdmin === false) {
      return problem(403, 'FORBIDDEN', "Bitbucket doesn't let the caller create repositories there.")
    }
    const slug = slugOf(body.name)
    if ((bitbucketDb.repos[guard.id] ?? []).some((r) => r.fullName === `${workspace.slug}/${slug}`)) {
      return problem(409, 'REPO_NAME_TAKEN', 'A repository with that slug already exists.')
    }
    const repo = seedBitbucketRepo(guard.id, {
      name: body.name,
      slug,
      workspace: workspace.slug,
      projectKey: body.project,
      isPrivate: body.private !== false,
    })
    return HttpResponse.json(repo, { status: 201 })
  }),
]
