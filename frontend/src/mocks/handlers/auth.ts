import { http, HttpResponse } from 'msw'

import { XSRF_COOKIE, XSRF_HEADER, type Schemas } from '../../api/client'
import { problem } from './problem'

type User = Schemas['UserDto']
type Token = Schemas['ApiTokenDto']

interface StoredUser extends User {
  password: string
}

/** In-memory auth backend for tests (reset after each test by src/test/setup.ts). */
export const authDb = {
  users: [] as StoredUser[],
  currentUserId: null as string | null,
  /** FR-2, FR-80: how "Continue with GitHub" signs in (`none` hides it). */
  githubMode: 'none' as Schemas['GitHubSignInMode'],
  /** FR-131: "Continue with GitLab" instances (kept in sync by mocks/handlers/gitlab.ts). */
  gitlabSignIn: [] as Schemas['GitLabInstanceSummaryDto'][],
  tokens: [] as Token[],
  xsrfCounter: 0,
  /** XSRF values the "server" accepts (the latest issued one). */
  validXsrf: null as string | null,
}

let idCounter = 0
export const nextId = () => {
  idCounter += 1
  return `00000000-0000-7000-8000-${String(idCounter).padStart(12, '0')}`
}

export function resetAuthDb() {
  authDb.users = []
  authDb.currentUserId = null
  authDb.githubMode = 'none'
  authDb.gitlabSignIn = []
  authDb.tokens = []
  authDb.xsrfCounter = 0
  authDb.validXsrf = null
  document.cookie = `${XSRF_COOKIE}=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/`
}

function toDto(stored: StoredUser): User {
  const { id, email, name, systemRole, avatarUrl, githubLinked } = stored
  return { id, email, name, systemRole, avatarUrl, githubLinked }
}

export function seedUser(overrides: Partial<StoredUser> = {}): User {
  const user: StoredUser = {
    id: nextId(),
    email: 'ada@example.com',
    name: 'Ada Lovelace',
    systemRole: authDb.users.length === 0 ? 'admin' : 'user',
    avatarUrl: null,
    githubLinked: false,
    password: 'password1',
    ...overrides,
  }
  authDb.users.push(user)
  return toDto(user)
}

/** Seeds a user and signs them in. */
export function signInAs(overrides: Partial<StoredUser> = {}): User {
  const user = seedUser(overrides)
  authDb.currentUserId = user.id
  return user
}

export function seedToken(overrides: Partial<Token> = {}): Token {
  const token: Token = {
    id: nextId(),
    name: 'Laptop CLI',
    createdAt: '2026-09-01T10:00:00Z',
    lastUsedAt: null,
    expiresAt: null,
    revokedAt: null,
    ...overrides,
  }
  authDb.tokens.push(token)
  return token
}

/** The signed-in mock user, if any. */
export function currentUser(): StoredUser | undefined {
  return authDb.users.find((u) => u.id === authDb.currentUserId)
}

function issueXsrf() {
  authDb.xsrfCounter += 1
  authDb.validXsrf = `xsrf-${String(authDb.xsrfCounter)}`
  document.cookie = `${XSRF_COOKIE}=${authDb.validXsrf}; path=/`
}

export const unauthenticated = () => problem(401, 'UNAUTHENTICATED', 'Sign in to continue.')

/** Cookie-authenticated mutations need a valid X-XSRF-TOKEN (as the real API). */
export function antiforgeryFailure(request: Request) {
  if (authDb.currentUserId && request.headers.get(XSRF_HEADER) !== authDb.validXsrf) {
    return problem(403, 'ANTIFORGERY', 'The request is missing a valid antiforgery token.')
  }
  return null
}

export const authHandlers = [
  http.get('*/api/auth/antiforgery', () => {
    issueXsrf()
    return new HttpResponse(null, { status: 204 })
  }),

  http.get('*/api/auth/providers', () => HttpResponse.json<Schemas['AuthProvidersDto']>({
      github: authDb.githubMode !== 'none',
      githubMode: authDb.githubMode,
      gitlab: authDb.gitlabSignIn,
    })),

  http.get('*/api/auth/me', () => {
    const user = currentUser()
    return user ? HttpResponse.json<User>(toDto(user)) : unauthenticated()
  }),

  http.post('*/api/auth/register', async ({ request }) => {
    const body = (await request.json()) as Schemas['RegisterRequest']
    if (authDb.users.some((u) => u.email.toLowerCase() === body.email.toLowerCase())) {
      return problem(409, 'EMAIL_TAKEN', 'An account with this email already exists.')
    }
    const user = seedUser({ email: body.email, name: body.name, password: body.password })
    authDb.currentUserId = user.id
    issueXsrf()
    return HttpResponse.json<User>(user, { status: 201 })
  }),

  http.post('*/api/auth/login', async ({ request }) => {
    const body = (await request.json()) as Schemas['LoginRequest']
    const user = authDb.users.find((u) => u.email.toLowerCase() === body.email.toLowerCase())
    if (user?.password !== body.password) {
      return problem(401, 'INVALID_CREDENTIALS', 'The email or password is incorrect.')
    }
    authDb.currentUserId = user.id
    issueXsrf()
    return new HttpResponse(null, { status: 204 })
  }),

  http.post('*/api/auth/logout', ({ request }) => {
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    authDb.currentUserId = null
    issueXsrf()
    return new HttpResponse(null, { status: 204 })
  }),

  http.get('*/api/me/tokens', () =>
    currentUser() ? HttpResponse.json<Token[]>(authDb.tokens) : unauthenticated(),
  ),

  http.post('*/api/me/tokens', async ({ request }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const body = (await request.json()) as Schemas['CreateApiTokenRequest']
    const token = seedToken({ name: body.name, expiresAt: body.expiresAt ?? null, createdAt: new Date().toISOString() })
    return HttpResponse.json<Schemas['CreatedApiTokenDto']>(
      { id: token.id, name: token.name, expiresAt: token.expiresAt, token: 'aide_pat_' + 'x'.repeat(43) },
      { status: 201 },
    )
  }),

  http.delete('*/api/me/tokens/:id', ({ request, params }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const token = authDb.tokens.find((t) => t.id === params.id)
    if (!token) {
      return problem(404, 'NOT_FOUND', 'The token was not found.')
    }
    token.revokedAt = new Date().toISOString()
    return new HttpResponse(null, { status: 204 })
  }),
]
