import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { antiforgeryFailure, authDb, currentUser, unauthenticated } from './auth'
import { problem } from './problem'
import { gitDto, ownerMutation, record } from './projects'

type App = Schemas['AdminGitHubAppDto']
type Repo = Schemas['GitHubRepoDto']
type Owner = Schemas['GitHubOwnerDto']
type Installation = Schemas['GitHubInstallationDto']

/** A user's stored GitHub connection (FR-81): what GET /me/connections/github shows, never the tokens. */
export interface StoredGitHubConnection {
  login: string
  githubUserId: number
  status: 'valid' | 'invalid'
  accessExpiresAt: string
}

export const INSTALL_URL = 'https://github.com/apps/aide-localhost/installations/new'

/** The §4.14 permissions the AIDE App manifest asks for (GitHub's own names). */
export const manifestPermissions: Record<string, string> = {
  contents: 'write',
  pull_requests: 'write',
  workflows: 'write',
  administration: 'write',
  metadata: 'read',
  pages: 'write',
  checks: 'read',
  statuses: 'read',
}

const notConfigured = (): App => ({
  status: 'not_configured',
  source: null,
  appId: null,
  slug: null,
  ownerLogin: null,
  ownerType: null,
  installUrl: null,
  htmlUrl: null,
  permissions: {},
  canCreateRepositories: false,
  pagesPermission: 'unknown',
})

/** In-memory GitHub App backend for tests (reset after each test by src/test/setup.ts). */
export const githubDb = {
  app: notConfigured(),
  /** GitHub connections by AIDE user id. */
  connections: {} as Record<string, StoredGitHubConnection>,
  installations: [] as Installation[],
  repos: [] as Repo[],
  owners: [] as Owner[],
  /** Refs per `owner/name`; repos without an entry are empty with no releases or tags. */
  refs: {} as Record<string, Schemas['GitHubRepoRefsDto']>,
  /** Answer the picker endpoints with 429 GITHUB_RATE_LIMITED (Retry-After seconds). */
  rateLimitedFor: null as number | null,
  /** Answer the picker endpoints with 502 UPSTREAM_ERROR. */
  upstreamDown: false,
  /** `owner/name` of repos the caller can't push to (switch-to-app answers 403). */
  noPushRepos: new Set<string>(),
  /** Last manifest request body, for assertions. */
  manifestRequests: [] as Schemas['CreateGitHubAppManifestRequest'][],
}

export function resetGitHubDb() {
  githubDb.app = notConfigured()
  githubDb.connections = {}
  githubDb.installations = []
  githubDb.repos = []
  githubDb.owners = []
  githubDb.refs = {}
  githubDb.rateLimitedFor = null
  githubDb.upstreamDown = false
  githubDb.manifestRequests = []
  githubDb.noPushRepos = new Set()
}

/** Registers the App (FR-80), as the manifest callback or `GitHub__App__*` configuration would. */
export function configureGitHubApp(overrides: Partial<App> = {}): App {
  githubDb.app = {
    status: 'configured',
    source: 'database',
    appId: 123456,
    slug: 'aide-localhost',
    ownerLogin: 'acme',
    ownerType: 'organization',
    installUrl: INSTALL_URL,
    htmlUrl: 'https://github.com/apps/aide-localhost',
    permissions: { ...manifestPermissions },
    canCreateRepositories: true,
    pagesPermission: 'granted',
    ...overrides,
  }
  authDb.githubMode = 'app'
  return githubDb.app
}

export function seedRepo(overrides: Partial<Repo> = {}): Repo {
  const owner = overrides.owner ?? 'ada'
  const name = overrides.name ?? `repo-${String(githubDb.repos.length + 1)}`
  const repo: Repo = {
    id: 1000 + githubDb.repos.length,
    fullName: `${owner}/${name}`,
    owner,
    name,
    private: true,
    defaultBranch: 'main',
    installationId: 9001,
    empty: true,
    htmlUrl: `https://github.com/${owner}/${name}`,
    canPush: true,
    ...overrides,
  }
  githubDb.repos.push(repo)
  return repo
}

/**
 * Connects the signed-in user's GitHub account (FR-81) with one installation on their own account, and the user and
 * an `acme` organisation as owners (AIDE installed on both).
 */
export function connectGitHub(userId: string, overrides: Partial<StoredGitHubConnection> = {}) {
  if (githubDb.app.status !== 'configured') {
    configureGitHubApp()
  }
  const login = overrides.login ?? 'ada'
  githubDb.connections[userId] = {
    login,
    githubUserId: 42,
    status: 'valid',
    accessExpiresAt: '2026-10-16T18:00:00Z',
    ...overrides,
  }
  if (githubDb.installations.length === 0) {
    githubDb.installations.push({
      id: 9001,
      account: { login, type: 'user' },
      repositorySelection: 'selected',
      manageUrl: 'https://github.com/settings/installations/9001',
      pagesPermission: 'granted',
    })
  }
  if (githubDb.owners.length === 0) {
    githubDb.owners.push(
      { login, type: 'user', installationId: 9001, appInstalled: true, canCreate: true },
      { login: 'acme', type: 'organization', installationId: 9002, appInstalled: true, canCreate: true },
    )
  }
}

function signedIn() {
  return currentUser() ?? unauthenticated()
}

function connected() {
  const user = currentUser()
  if (!user) {
    return unauthenticated()
  }
  if (githubDb.connections[user.id]?.status !== 'valid') {
    return problem(409, 'GITHUB_NOT_CONNECTED', 'Connect GitHub first.')
  }
  if (githubDb.rateLimitedFor !== null) {
    return problem(429, 'GITHUB_RATE_LIMITED', 'GitHub rate limit exhausted.', {
      headers: { 'Retry-After': String(githubDb.rateLimitedFor) },
    })
  }
  if (githubDb.upstreamDown) {
    return problem(502, 'UPSTREAM_ERROR', 'GitHub could not be reached.')
  }
  return user
}

function admin(request?: Request) {
  const user = currentUser()
  if (!user) {
    return unauthenticated()
  }
  if (user.systemRole !== 'admin') {
    return problem(403, 'FORBIDDEN', 'Admins only.')
  }
  return (request && antiforgeryFailure(request)) ?? user
}

const PAGE_SIZE = 30

export const githubHandlers = [
  // ---- Admin → Integrations → GitHub (FR-80) ----
  http.get('*/api/admin/integrations/github', () => {
    const user = admin()
    return user instanceof Response ? user : HttpResponse.json<App>(githubDb.app)
  }),

  http.post('*/api/admin/integrations/github/manifest', async ({ request }) => {
    const user = admin(request)
    if (user instanceof Response) {
      return user
    }
    const body = (await record(request)) as Schemas['CreateGitHubAppManifestRequest']
    githubDb.manifestRequests.push(body)
    if (githubDb.app.status === 'configured') {
      return problem(409, 'CONFLICT', 'An App is already configured.')
    }
    if (body.ownerType === 'organization' && !body.org?.trim()) {
      return problem(400, 'VALIDATION', 'The manifest request is invalid.', {
        errors: { org: ['Enter the organisation login.'] },
      })
    }
    const state = 'state-abc123'
    const base =
      body.ownerType === 'organization'
        ? `https://github.com/organizations/${body.org?.trim() ?? ''}/settings/apps/new`
        : 'https://github.com/settings/apps/new'
    return HttpResponse.json<Schemas['GitHubAppManifestResponseDto']>({
      postUrl: `${base}?state=${state}`,
      state,
      manifest: {
        name: 'AIDE (localhost)',
        url: 'http://localhost:8080',
        redirect_url: 'http://localhost:8080/api/admin/integrations/github/callback',
        callback_urls: ['http://localhost:8080/api/auth/github/callback'],
        setup_url: 'http://localhost:8080/account/connections',
        public: false,
        default_permissions: { ...manifestPermissions },
        default_events: [],
        request_oauth_on_install: false,
      },
    })
  }),

  // ---- Connections → GitHub (FR-81) ----
  http.get('*/api/me/connections/github', () => {
    const user = signedIn()
    if (user instanceof Response) {
      return user
    }
    const c = githubDb.connections[user.id]
    const installUrl = githubDb.app.installUrl
    return HttpResponse.json<Schemas['GitHubConnectionDto']>(
      c
        ? {
            connected: true,
            login: c.login,
            githubUserId: c.githubUserId,
            status: c.status,
            accessExpiresAt: c.accessExpiresAt,
            installations: githubDb.installations,
            installUrl,
          }
        : {
            connected: false,
            login: null,
            githubUserId: null,
            status: null,
            accessExpiresAt: null,
            installations: [],
            installUrl,
          },
    )
  }),

  http.delete('*/api/me/connections/github', async ({ request }) => {
    const user = signedIn()
    if (user instanceof Response) {
      return user
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    await record(request)
    // eslint-disable-next-line @typescript-eslint/no-dynamic-delete -- in-memory mock store keyed by user id
    delete githubDb.connections[user.id]
    return new HttpResponse(null, { status: 204 })
  }),

  // ---- Repo picker and Create repository (FR-81, FR-86) ----
  http.get('*/api/me/github/repos', ({ request }) => {
    const user = connected()
    if (user instanceof Response) {
      return user
    }
    const url = new URL(request.url)
    const search = (url.searchParams.get('search') ?? '').toLowerCase()
    const page = Math.max(1, Number(url.searchParams.get('page') ?? '1') || 1)
    const matches = githubDb.repos.filter((r) => r.fullName.toLowerCase().includes(search))
    return HttpResponse.json<Schemas['GitHubRepoPageDto']>({
      items: matches.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE),
      page,
      hasMore: matches.length > page * PAGE_SIZE,
    })
  }),

  http.post('*/api/me/github/repos', async ({ request }) => {
    const user = connected()
    if (user instanceof Response) {
      return user
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const body = (await record(request)) as Schemas['CreateGitHubRepoRequest']
    const owner = githubDb.owners.find((o) => o.login === body.owner)
    if (!owner?.appInstalled) {
      return problem(409, 'GITHUB_APP_NOT_INSTALLED', 'The App is not installed on that owner.', {
        ...(githubDb.app.installUrl ? { installUrl: githubDb.app.installUrl } : {}),
      })
    }
    if (!owner.canCreate || !githubDb.app.canCreateRepositories) {
      return problem(403, 'FORBIDDEN', 'The App lacks the Administration permission.')
    }
    if (githubDb.repos.some((r) => r.fullName.toLowerCase() === `${body.owner}/${body.name}`.toLowerCase())) {
      return problem(409, 'REPO_NAME_TAKEN', 'A repository with that name exists.')
    }
    const repo = seedRepo({
      owner: body.owner,
      name: body.name,
      private: body.private,
      installationId: owner.installationId ?? 9001,
      empty: true,
    })
    return HttpResponse.json<Repo>(repo, { status: 201 })
  }),

  http.get('*/api/me/github/owners', () => {
    const user = connected()
    return user instanceof Response ? user : HttpResponse.json<Owner[]>(githubDb.owners)
  }),

  http.get('*/api/me/github/repos/:owner/:repo/refs', ({ params }) => {
    const user = connected()
    if (user instanceof Response) {
      return user
    }
    const fullName = `${String(params.owner)}/${String(params.repo)}`
    const repo = githubDb.repos.find((r) => r.fullName === fullName)
    if (!repo) {
      return problem(404, 'NOT_FOUND', 'The repository was not found.')
    }
    return HttpResponse.json<Schemas['GitHubRepoRefsDto']>(
      githubDb.refs[fullName] ?? { defaultBranch: repo.defaultBranch, empty: repo.empty ?? true, releases: [], tags: [] },
    )
  }),

  // ---- Settings → Git → Switch to GitHub App (FR-83) ----
  http.post('*/api/projects/:id/settings/git/switch-to-app', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    if (githubDb.connections[a.userId]?.status !== 'valid') {
      return problem(409, 'GITHUB_NOT_CONNECTED', 'Connect GitHub first.')
    }
    if (githubDb.noPushRepos.has((a.project.git.repoUrl ?? '').replace(/^https:\/\/github\.com\//, ''))) {
      return problem(403, 'FORBIDDEN', 'You need write access to the repository.')
    }
    const git = a.project.git
    if (git.mode !== 'github_pat' || git.appInstalledOnRepo !== true) {
      return problem(409, 'GITHUB_APP_NOT_INSTALLED', 'The App cannot access the repository.', {
        ...(githubDb.app.installUrl ? { installUrl: githubDb.app.installUrl } : {}),
      })
    }
    const fullName = (git.repoUrl ?? '').replace(/^https:\/\/github\.com\//, '').replace(/\.git$/, '')
    Object.assign(git, {
      provider: 'github_app',
      mode: 'github_app',
      installationId: 9001,
      repoId: 4242,
      repoFullName: fullName,
      hasToken: false,
      appInstalledOnRepo: null,
    } satisfies Partial<Schemas['GitSettingsDto']>)
    return HttpResponse.json<Schemas['GitSettingsDto']>(gitDto(a.project, true))
  }),
]
