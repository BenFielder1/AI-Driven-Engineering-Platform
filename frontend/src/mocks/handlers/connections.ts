import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { antiforgeryFailure, authDb, currentUser, nextId, unauthenticated } from './auth'
import { isLlmProviderEnabled, llmProviderUnavailable } from './llm'
import { problem } from './problem'
import { access, forbidden, llmDto, ownerMutation, projectsDb, record, type StoredProject } from './projects'

type Credential = Schemas['LlmCredentialDto']
type LlmSettings = Schemas['LlmSettingsDto']

/** A stored LLM connection: the DTO minus the derived `boundProjects`, plus its owner and key. */
export interface StoredCredential extends Omit<Credential, 'boundProjects'> {
  userId: string
  apiKey: string
}

/** In-memory connections backend for tests (reset after each test by src/test/setup.ts). */
export const connectionsDb = {
  credentials: [] as StoredCredential[],
  /** Keys the "provider" rejects (CREDENTIAL_INVALID on add; status invalid on test). */
  rejectedKeys: new Set<string>(),
  /** When true, add and test answer 502 UPSTREAM_ERROR (provider unreachable). */
  providerDown: false,
  policy: { allowSystemKeys: false, systemKeyProviders: [] } as Schemas['AdminLlmPolicyDto'],
}

export function resetConnectionsDb() {
  connectionsDb.credentials = []
  connectionsDb.rejectedKeys = new Set()
  connectionsDb.providerDown = false
  connectionsDb.policy = { allowSystemKeys: false, systemKeyProviders: [] }
}

const last4Of = (key: string) => key.slice(-4)

export function seedCredential(
  overrides: Partial<StoredCredential> & Pick<StoredCredential, 'userId'>,
): StoredCredential {
  const apiKey = overrides.apiKey ?? `sk-test-${nextId().slice(-4)}`
  const c: StoredCredential = {
    id: nextId(),
    provider: 'anthropic',
    label: 'Work key',
    last4: last4Of(apiKey),
    status: 'valid',
    lastValidatedAt: '2026-09-01T09:00:00Z',
    createdAt: '2026-09-01T09:00:00Z',
    ...overrides,
    apiKey,
  }
  connectionsDb.credentials.unshift(c)
  return c
}

/** Binds `credential` to `project` as its owner did (FR-79). */
export function bindCredential(project: StoredProject, credential: StoredCredential) {
  const user = authDb.users.find((u) => u.id === credential.userId)
  project.llm.provider = credential.provider
  project.llm.binding = {
    credentialId: credential.id,
    provider: credential.provider,
    label: credential.label,
    last4: credential.last4,
    status: credential.status,
    boundBy: { id: credential.userId, name: user?.name ?? 'Unknown' },
  }
}

function toDto(c: StoredCredential): Credential {
  return {
    id: c.id,
    provider: c.provider,
    label: c.label,
    last4: c.last4,
    status: c.status,
    lastValidatedAt: c.lastValidatedAt,
    createdAt: c.createdAt,
    boundProjects: projectsDb.projects
      .filter((p) => p.llm.binding?.credentialId === c.id)
      .map((p) => ({ id: p.id, name: p.name })),
  }
}

/** Keeps every binding's label and status in step with its connection. */
function syncBindings(c: StoredCredential) {
  for (const p of projectsDb.projects) {
    if (p.llm.binding?.credentialId === c.id) {
      p.llm.binding = { ...p.llm.binding, label: c.label, status: c.status }
    }
  }
}

/** Signed-in user + antiforgery for a mutation of the caller's own connections. */
function userMutation(request: Request) {
  const user = currentUser()
  if (!user) {
    return unauthenticated()
  }
  return antiforgeryFailure(request) ?? user
}

function ownCredential(id: unknown, userId: string) {
  return connectionsDb.credentials.find((c) => c.id === id && c.userId === userId)
}

const notFound = () => problem(404, 'NOT_FOUND', 'The connection was not found.')
const upstream = () => problem(502, 'UPSTREAM_ERROR', 'The provider could not be reached.')

export const connectionHandlers = [
  http.get('*/api/me/connections/llm', () => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    return HttpResponse.json<Schemas['LlmConnectionsDto']>({
      credentials: connectionsDb.credentials.filter((c) => c.userId === user.id).map(toDto),
      // FR-157: only providers enabled on this server.
      keylessProviders: (['ollama', 'fake'] as const).filter(isLlmProviderEnabled),
    })
  }),

  http.post('*/api/me/connections/llm', async ({ request }) => {
    const user = userMutation(request)
    if (user instanceof Response) {
      return user
    }
    const body = (await record(request)) as Schemas['CreateLlmCredentialRequest']
    const errors: Record<string, string[]> = {}
    if (body.provider !== 'anthropic' && body.provider !== 'openai') {
      errors.provider = [`provider: ${body.provider} needs no key; it is always available.`]
    }
    if (!body.label.trim()) {
      errors.label = ['The label is required.']
    }
    if (!body.apiKey) {
      errors.apiKey = ['The API key is required.']
    }
    if (Object.keys(errors).length > 0) {
      return problem(400, 'VALIDATION', 'The connection is invalid.', { errors })
    }
    if (connectionsDb.providerDown) {
      return upstream()
    }
    if (connectionsDb.rejectedKeys.has(body.apiKey)) {
      return problem(400, 'CREDENTIAL_INVALID', 'The provider rejected the key.')
    }
    const now = new Date().toISOString()
    const created = seedCredential({
      userId: user.id,
      provider: body.provider,
      label: body.label.trim(),
      apiKey: body.apiKey,
      status: 'valid',
      lastValidatedAt: now,
      createdAt: now,
    })
    return HttpResponse.json<Credential>(toDto(created), { status: 201 })
  }),

  http.patch('*/api/me/connections/llm/:id', async ({ request, params }) => {
    const user = userMutation(request)
    if (user instanceof Response) {
      return user
    }
    const body = (await record(request)) as Schemas['UpdateLlmCredentialRequest']
    const c = ownCredential(params.id, user.id)
    if (!c) {
      return notFound()
    }
    if (!body.label.trim()) {
      return problem(400, 'VALIDATION', 'The connection is invalid.', { errors: { label: ['The label is required.'] } })
    }
    c.label = body.label.trim()
    syncBindings(c)
    return HttpResponse.json<Credential>(toDto(c))
  }),

  http.delete('*/api/me/connections/llm/:id', async ({ request, params }) => {
    const user = userMutation(request)
    if (user instanceof Response) {
      return user
    }
    await record(request)
    const c = ownCredential(params.id, user.id)
    if (!c) {
      return notFound()
    }
    for (const p of projectsDb.projects) {
      if (p.llm.binding?.credentialId === c.id) {
        p.llm.binding = null
      }
    }
    connectionsDb.credentials = connectionsDb.credentials.filter((x) => x !== c)
    return new HttpResponse(null, { status: 204 })
  }),

  http.post('*/api/me/connections/llm/:id/test', async ({ request, params }) => {
    const user = userMutation(request)
    if (user instanceof Response) {
      return user
    }
    await record(request)
    const c = ownCredential(params.id, user.id)
    if (!c) {
      return notFound()
    }
    if (connectionsDb.providerDown) {
      return upstream()
    }
    c.status = connectionsDb.rejectedKeys.has(c.apiKey) ? 'invalid' : 'valid'
    c.lastValidatedAt = new Date().toISOString()
    syncBindings(c)
    return HttpResponse.json<Credential>(toDto(c))
  }),

  // ---- Project LLM settings and binding (FR-9, FR-79) ----
  http.get('*/api/projects/:id/settings/llm', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    return HttpResponse.json<LlmSettings>(llmDto(a.project, a.level === 'owner'))
  }),

  http.patch('*/api/projects/:id/settings/llm', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['UpdateLlmSettingsRequest']
    const llm = a.project.llm
    const provider = body.provider ?? llm.provider
    if (body.credentialId !== undefined && a.level !== 'owner') {
      return forbidden()
    }
    // FR-157: a provider turned off on this server can't be chosen (keeping a disabled one is refused too).
    if (!isLlmProviderEnabled(provider)) {
      return llmProviderUnavailable()
    }
    let binding = llm.binding
    if (body.credentialId === null) {
      binding = null
    } else if (body.credentialId !== undefined && body.credentialId !== binding?.credentialId) {
      const c = connectionsDb.credentials.find((x) => x.id === body.credentialId)
      if (!c) {
        return problem(404, 'NOT_FOUND', 'The connection was not found.')
      }
      if (c.userId !== a.userId) {
        return problem(403, 'CREDENTIAL_NOT_OWNER', 'You can only bind your own connection.')
      }
      if (c.provider !== provider) {
        return problem(400, 'VALIDATION', 'The connection is for another provider.', {
          errors: { credentialId: ['The connection must be for the chosen provider.'] },
        })
      }
      const user = authDb.users.find((u) => u.id === c.userId)
      binding = {
        credentialId: c.id,
        provider: c.provider,
        label: c.label,
        last4: c.last4,
        status: c.status,
        boundBy: { id: c.userId, name: user?.name ?? 'Unknown' },
      }
    } else if (body.credentialId === undefined && binding && binding.provider !== provider) {
      // Changing the provider without a credentialId drops a binding of the old provider.
      binding = null
    }
    llm.provider = provider
    llm.defaultModel = body.defaultModel ?? llm.defaultModel
    llm.modelsByAgent = body.modelsByAgent ?? llm.modelsByAgent
    llm.binding = binding
    return HttpResponse.json<LlmSettings>(llmDto(a.project, a.level === 'owner'))
  }),

  http.get('*/api/admin/integrations/llm', () => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    if (user.systemRole !== 'admin') {
      return problem(403, 'FORBIDDEN', 'Admins only.')
    }
    return HttpResponse.json<Schemas['AdminLlmPolicyDto']>(connectionsDb.policy)
  }),
]
