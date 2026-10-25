import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { currentUser, unauthenticated } from './auth'
import { connectionsDb } from './connections'
import { problem } from './problem'

type LlmProvider = Schemas['LlmProvider']
type LlmProviderDto = Schemas['LlmProviderDto']
type LlmModel = Schemas['LlmModelDto']

/** Server order (FR-158): anthropic, openai, ollama, fake. */
export const llmProviderOrder: readonly LlmProvider[] = ['anthropic', 'openai', 'ollama', 'fake']

const keyless = (p: LlmProvider) => p === 'ollama' || p === 'fake'

/** Mirrors the shipped catalog (backend appsettings.json `Llm:Providers:<P>:Catalog`, §4.24). */
const defaultCatalogs = (): Record<LlmProvider, LlmModel[]> => ({
  anthropic: [
    { id: 'claude-opus-5-5', name: 'Claude Opus 5.5', maxOutputTokens: 128_000, contextTokens: 1_000_000, recommended: true },
    { id: 'claude-sonnet-5-5', name: 'Claude Sonnet 5.5', maxOutputTokens: 128_000, contextTokens: 1_000_000, recommended: false },
    { id: 'claude-haiku-4-5', name: 'Claude Haiku 4.5', maxOutputTokens: 64_000, contextTokens: 200_000, recommended: false },
  ],
  openai: [
    { id: 'gpt-6-astra', name: 'GPT-6 Astra', maxOutputTokens: 16_000, contextTokens: 400_000, recommended: false },
    { id: 'gpt-6.1-sol', name: 'GPT-6.1 Sol', maxOutputTokens: 16_000, contextTokens: 400_000, recommended: false },
  ],
  ollama: [{ id: 'qwen2.5-coder', name: 'Qwen2.5 Coder', maxOutputTokens: 16_000, contextTokens: 32_768, recommended: false }],
  fake: [{ id: 'fake-1', name: 'Fake (offline)', maxOutputTokens: 128_000, contextTokens: 1_000_000, recommended: false }],
})

const defaultModels: Record<LlmProvider, string> = {
  anthropic: 'claude-opus-5-5',
  openai: 'gpt-6-astra',
  ollama: 'qwen2.5-coder',
  fake: 'fake-1',
}

/** In-memory LLM provider config for tests (reset after each test by src/test/setup.ts). */
export const llmDb = {
  /** FR-157: `Llm:Providers:<P>:Enabled` (all four by default, as in the dev loop and CI). */
  enabled: new Set<LlmProvider>(llmProviderOrder),
  catalogs: defaultCatalogs(),
  /** `Llm:DefaultProvider` (appsettings.json: anthropic; CI sets fake). */
  defaultProvider: 'anthropic' as LlmProvider,
}

export function resetLlmDb() {
  llmDb.enabled = new Set(llmProviderOrder)
  llmDb.catalogs = defaultCatalogs()
  llmDb.defaultProvider = 'anthropic'
}

/** FR-157: turns providers off on this "server". */
export function disableLlmProviders(...providers: LlmProvider[]) {
  for (const p of providers) {
    llmDb.enabled.delete(p)
  }
}

export const isLlmProviderEnabled = (p: LlmProvider) => llmDb.enabled.has(p)

/** §4.24 `LlmProviderRules.Available`: `null` for a disabled provider. */
function availability(provider: LlmProvider, userId: string): Pick<LlmProviderDto, 'usable' | 'reason'> | null {
  if (!llmDb.enabled.has(provider)) {
    return null
  }
  if (keyless(provider)) {
    return { usable: true, reason: 'keyless' }
  }
  const policy = connectionsDb.policy
  if (policy.allowSystemKeys && policy.systemKeyProviders.includes(provider)) {
    return { usable: true, reason: 'system_key' }
  }
  return connectionsDb.credentials.some((c) => c.userId === userId && c.provider === provider)
    ? { usable: true, reason: 'own_connection' }
    : { usable: false, reason: 'needs_connection' }
}

/** GET /api/llm/providers body for `userId` (FR-158). */
export function llmProvidersFor(userId: string): LlmProviderDto[] {
  const list = llmProviderOrder.flatMap((provider) => {
    const a = availability(provider, userId)
    if (!a) {
      return []
    }
    return [
      {
        provider,
        ...a,
        defaultModel: defaultModels[provider],
        connections: connectionsDb.credentials
          .filter((c) => c.userId === userId && c.provider === provider)
          .map((c) => ({ id: c.id, label: c.label, last4: c.last4 })),
        catalog: llmDb.catalogs[provider],
        isDefault: false,
      },
    ]
  })
  // §4.24 DefaultForNewProject: the configured default if usable, else the first usable one in order, else none.
  const chosen = list.find((p) => p.usable && p.provider === llmDb.defaultProvider) ?? list.find((p) => p.usable)
  return list.map((p) => ({ ...p, isDefault: p === chosen }))
}

const unavailable = () =>
  problem(400, 'LLM_PROVIDER_UNAVAILABLE', "This AI provider isn't available here. Choose another provider or add a connection.")

/**
 * FR-159: checks `POST /api/projects` `llm` for `userId` as the API does: the provider enabled and usable
 * (LLM_PROVIDER_UNAVAILABLE), the credential the caller's own (404 / 403 CREDENTIAL_NOT_OWNER), a model id.
 * Returns the error response, or `null` when the choice is fine.
 */
export function checkCreateLlm(llm: Schemas['CreateProjectLlmRequest'], userId: string): Response | null {
  const a = availability(llm.provider, userId)
  if (!a?.usable) {
    return unavailable()
  }
  if (!llm.defaultModel.trim()) {
    return problem(400, 'VALIDATION', 'One or more validation errors occurred.', {
      errors: { 'llm.defaultModel': ['The default model is required.'] },
    })
  }
  if (llm.credentialId) {
    const c = connectionsDb.credentials.find((x) => x.id === llm.credentialId)
    if (!c) {
      return problem(404, 'NOT_FOUND', 'The connection was not found.')
    }
    if (c.userId !== userId) {
      return problem(403, 'CREDENTIAL_NOT_OWNER', 'You can only bind your own connection.')
    }
  }
  return null
}

/** FR-157: PATCH …/settings/llm refuses a disabled provider. */
export const llmProviderUnavailable = unavailable

export const llmHandlers = [
  http.get('*/api/llm/providers', () => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    return HttpResponse.json<LlmProviderDto[]>(llmProvidersFor(user.id))
  }),
]
