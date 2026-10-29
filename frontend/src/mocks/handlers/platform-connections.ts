import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { antiforgeryFailure, currentUser, nextId, unauthenticated } from './auth'
import { problem } from './problem'
import { projectsDb, record } from './projects'

/**
 * Platform connections (FR-142, §4.20, §4.21): pasted deployment-platform and database tokens, Vercel's included
 * (FR-87 migrated). These handlers follow the contract (backend/openapi/api.json) and its error cases.
 */

type PlatformConnection = Schemas['PlatformConnectionDto']
type Provider = Schemas['PlatformProvider']

/** A stored connection: the DTO minus the derived `boundProjects`, plus its owner and token. */
export interface StoredPlatformConnection extends Omit<PlatformConnection, 'boundProjects'> {
  userId: string
  token: string
}

/** In-memory platforms for tests (reset after each test by src/test/setup.ts). */
export const platformDb = {
  connections: [] as StoredPlatformConnection[],
  /** Tokens the platform rejects (CREDENTIAL_INVALID on add; status invalid on Re-test). */
  rejectedTokens: new Set<string>(),
  /** Tokens the platform answers 403 for, naming what's missing (saved as insufficient_scope; Supabase). */
  missingPermissions: new Map<string, string[]>(),
  /** The platform is unreachable: add and Re-test answer 502 UPSTREAM_ERROR. */
  down: false,
  /** The platform rate limits: add and Re-test answer 429 UPSTREAM_RATE_LIMITED with this Retry-After (seconds). */
  rateLimitedFor: null as number | null,
}

export function resetPlatformDb() {
  platformDb.connections = []
  platformDb.rejectedTokens = new Set()
  platformDb.missingPermissions = new Map()
  platformDb.down = false
  platformDb.rateLimitedFor = null
}

const emptyMetadata = (): Schemas['PlatformConnectionMetadataDto'] => ({
  teamId: null,
  teamSlug: null,
  accounts: [],
  policies: [],
  missingPermissions: [],
  tokenKind: null,
})

export function seedPlatformConnection(
  overrides: Partial<Omit<StoredPlatformConnection, 'metadata'>> & {
    userId: string
    metadata?: Partial<Schemas['PlatformConnectionMetadataDto']>
  },
): StoredPlatformConnection {
  const token = overrides.token ?? `tok_test${nextId().slice(-4)}`
  const { metadata, ...rest } = overrides
  const c: StoredPlatformConnection = {
    id: nextId(),
    provider: 'vercel',
    label: 'Platform token',
    last4: token.slice(-4),
    accountId: null,
    accountName: 'ada',
    scopes: [],
    expiresAt: null,
    status: 'valid',
    lastValidatedAt: '2026-09-01T09:00:00Z',
    createdAt: '2026-09-01T09:00:00Z',
    ...rest,
    metadata: { ...emptyMetadata(), ...metadata },
    token,
  }
  platformDb.connections.unshift(c)
  return c
}

export function platformDto(c: StoredPlatformConnection): PlatformConnection {
  return {
    id: c.id,
    provider: c.provider,
    label: c.label,
    last4: c.last4,
    accountId: c.accountId,
    accountName: c.accountName,
    metadata: {
      ...c.metadata,
      accounts: [...c.metadata.accounts],
      policies: [...c.metadata.policies],
      missingPermissions: [...c.metadata.missingPermissions],
    },
    scopes: [...c.scopes],
    expiresAt: c.expiresAt,
    status: c.status,
    lastValidatedAt: c.lastValidatedAt,
    createdAt: c.createdAt,
    boundProjects: projectsDb.projects
      .filter((p) => p.deployment?.binding?.connectionId === c.id)
      .map((p) => ({ id: p.id, name: p.name })),
  }
}

/** What each platform reports for a valid token in the mock (the account it reaches, its scopes or policies). */
function validated(provider: Provider, token: string, accountId: string | null) {
  const metadata = emptyMetadata()
  let accountName: string | null = 'ada'
  const scopes: string[] = []
  switch (provider) {
    case 'vercel':
      // A token naming a team (`team_…`) is team-scoped in the mock.
      if (token.startsWith('team_')) {
        metadata.teamId = 'team_acme'
        metadata.teamSlug = 'acme'
      }
      break
    case 'netlify':
      metadata.accounts = [{ id: 'acc_acme', name: 'Acme', slug: 'acme', kind: 'team' }]
      accountName = 'Acme'
      break
    case 'cloudflare':
      metadata.accounts = [{ id: 'cf_acc_1', name: 'Acme Cloudflare', slug: null, kind: 'account' }]
      metadata.policies = ['Workers Scripts: Edit', 'Workers CI: Edit', 'Account Settings: Read', 'User Details: Read']
      metadata.tokenKind = 'user'
      accountName = 'Acme Cloudflare'
      break
    case 'render':
      metadata.accounts = [
        { id: 'own_ada', name: 'Ada', slug: null, kind: 'user' },
        { id: 'own_acme', name: 'Acme', slug: null, kind: 'team' },
      ]
      accountName = 'Ada'
      break
    case 'railway':
      metadata.tokenKind = accountId ? 'workspace' : 'account'
      metadata.accounts = [{ id: accountId ?? 'ws_ada', name: accountId ? 'Acme workspace' : 'Ada', slug: null, kind: 'workspace' }]
      accountName = accountId ? 'Acme workspace' : 'Ada'
      break
    case 'supabase':
      metadata.accounts = [{ id: 'org_acme', name: 'Acme', slug: 'acme', kind: 'organization' }]
      accountName = 'Acme'
      break
  }
  return { metadata, accountName, scopes }
}

function userMutation(request: Request) {
  const user = currentUser()
  if (!user) {
    return unauthenticated()
  }
  return antiforgeryFailure(request) ?? user
}

const providerNames: Record<Provider, string> = {
  vercel: 'Vercel',
  netlify: 'Netlify',
  cloudflare: 'Cloudflare',
  render: 'Render',
  railway: 'Railway',
  supabase: 'Supabase',
}

const ownConnection = (id: unknown, userId: string) => platformDb.connections.find((c) => c.id === id && c.userId === userId)
const notFound = () => problem(404, 'NOT_FOUND', 'The connection was not found.')

/** 502 or 429 when the platform is down or rate limiting (checked before anything changes). */
function platformUnavailable(provider: Provider): Response | null {
  if (platformDb.rateLimitedFor !== null) {
    return problem(429, 'UPSTREAM_RATE_LIMITED', `${providerNames[provider]} is rate limiting requests.`, {
      headers: { 'Retry-After': String(platformDb.rateLimitedFor) },
    })
  }
  if (platformDb.down) {
    return problem(502, 'UPSTREAM_ERROR', `${providerNames[provider]} could not be reached.`)
  }
  return null
}

const ACCOUNT_ID = /^[A-Za-z0-9._:-]{1,200}$/

export const platformConnectionHandlers = [
  http.get('*/api/me/connections/platforms', () => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    return HttpResponse.json<PlatformConnection[]>(platformDb.connections.filter((c) => c.userId === user.id).map(platformDto))
  }),

  http.post('*/api/me/connections/platforms', async ({ request }) => {
    const user = userMutation(request)
    if (user instanceof Response) {
      return user
    }
    const body = (await record(request)) as Schemas['CreatePlatformConnectionRequest']
    const errors: Record<string, string[]> = {}
    if (!body.label.trim()) {
      errors.label = ['The label is required.']
    }
    if (!body.token) {
      errors.token = ['The token is required.']
    }
    if (body.accountId != null && !ACCOUNT_ID.test(body.accountId)) {
      errors.accountId = ["Letters, digits, '.', '_', ':' or '-', up to 200 characters."]
    }
    if (Object.keys(errors).length > 0) {
      return problem(400, 'VALIDATION', 'The connection is invalid.', { errors })
    }
    if (body.provider === 'cloudflare' && body.token.startsWith('cfat_')) {
      return problem(
        400,
        'CONNECTION_SCOPE_INSUFFICIENT',
        'This is a Cloudflare account-owned token (cfat_). Workers Builds needs a user API token.',
        { details: ['user API token'] },
      )
    }
    const unavailable = platformUnavailable(body.provider)
    if (unavailable) {
      return unavailable
    }
    if (platformDb.rejectedTokens.has(body.token)) {
      return problem(400, 'CREDENTIAL_INVALID', `${providerNames[body.provider]} rejected the token.`)
    }
    const now = new Date().toISOString()
    const accountId = body.accountId ?? null
    const { metadata, accountName, scopes } = validated(body.provider, body.token, accountId)
    const missing = platformDb.missingPermissions.get(body.token) ?? []
    metadata.missingPermissions = missing
    const created = seedPlatformConnection({
      userId: user.id,
      provider: body.provider,
      label: body.label.trim(),
      token: body.token,
      accountId: accountId ?? metadata.accounts[0]?.id ?? null,
      accountName,
      metadata,
      scopes,
      expiresAt: body.provider === 'render' || body.provider === 'railway' ? null : '2027-01-01T00:00:00Z',
      status: missing.length > 0 ? 'insufficient_scope' : 'valid',
      lastValidatedAt: now,
      createdAt: now,
    })
    return HttpResponse.json<PlatformConnection>(platformDto(created), { status: 201 })
  }),

  http.patch('*/api/me/connections/platforms/:id', async ({ request, params }) => {
    const user = userMutation(request)
    if (user instanceof Response) {
      return user
    }
    const body = (await record(request)) as Schemas['UpdatePlatformConnectionRequest']
    const c = ownConnection(params.id, user.id)
    if (!c) {
      return notFound()
    }
    if (!body.label.trim()) {
      return problem(400, 'VALIDATION', 'The connection is invalid.', { errors: { label: ['The label is required.'] } })
    }
    c.label = body.label.trim()
    return HttpResponse.json<PlatformConnection>(platformDto(c))
  }),

  http.delete('*/api/me/connections/platforms/:id', async ({ request, params }) => {
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
    platformDb.connections = platformDb.connections.filter((x) => x !== c)
    return new HttpResponse(null, { status: 204 })
  }),

  http.post('*/api/me/connections/platforms/:id/test', async ({ request, params }) => {
    const user = userMutation(request)
    if (user instanceof Response) {
      return user
    }
    await record(request)
    const c = ownConnection(params.id, user.id)
    if (!c) {
      return notFound()
    }
    const unavailable = platformUnavailable(c.provider)
    if (unavailable) {
      return unavailable
    }
    const missing = platformDb.missingPermissions.get(c.token) ?? []
    c.status = platformDb.rejectedTokens.has(c.token) ? 'invalid' : missing.length > 0 ? 'insufficient_scope' : 'valid'
    c.metadata = { ...c.metadata, missingPermissions: c.status === 'insufficient_scope' ? missing : [] }
    c.lastValidatedAt = new Date().toISOString()
    return HttpResponse.json<PlatformConnection>(platformDto(c))
  }),

  http.get('*/api/me/connections/platforms/:id/accounts', ({ params }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const c = ownConnection(params.id, user.id)
    if (!c) {
      return notFound()
    }
    return HttpResponse.json<Schemas['PlatformAccountDto'][]>(c.metadata.accounts)
  }),

  http.get('*/api/me/connections/platforms/:id/resources', ({ params, request }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const c = ownConnection(params.id, user.id)
    if (!c) {
      return notFound()
    }
    const unavailable = platformUnavailable(c.provider)
    if (unavailable) {
      return unavailable
    }
    const type = new URL(request.url).searchParams.get('type') ?? 'projects'
    return HttpResponse.json<Schemas['PlatformResourceDto'][]>([
      { id: `${type}_1`, name: `inventory-${type}`, url: null, detail: `A ${c.provider} ${type.replace(/s$/, '')}` },
    ])
  }),
]
