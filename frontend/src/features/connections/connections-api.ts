import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { toApiError } from '../../lib/errors'
import { projectKeys, unwrap } from '../projects/projects-api'

export type LlmConnections = Schemas['LlmConnectionsDto']
export type LlmCredential = Schemas['LlmCredentialDto']
export type LlmCredentialStatus = Schemas['LlmCredentialStatus']
export type AdminLlmPolicy = Schemas['AdminLlmPolicyDto']

/** Providers that take a user key (FR-78); Ollama and Fake are keyless. */
export const keyedProviders = ['anthropic', 'openai'] as const satisfies readonly Schemas['LlmProvider'][]

export const connectionKeys = {
  all: ['me', 'connections'] as const,
  llm: () => [...connectionKeys.all, 'llm'] as const,
  adminLlmPolicy: () => ['admin', 'integrations', 'llm'] as const,
}

/** FR-78: the caller's LLM connections (no keys) and the keyless providers. */
export function useLlmConnections(enabled = true) {
  return useQuery({
    queryKey: connectionKeys.llm(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/me/connections/llm', { signal })),
    enabled,
  })
}

/**
 * A connection change can change any project's binding (delete clears bindings; FR-79), so the project settings
 * caches are refreshed too.
 */
function useInvalidateConnections() {
  const queryClient = useQueryClient()
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: connectionKeys.all }),
      queryClient.invalidateQueries({ queryKey: projectKeys.all }),
    ])
}

/**
 * FR-78: adds a key; the API checks it with the provider first (CREDENTIAL_INVALID → nothing saved). The variables
 * carry the plaintext key, so the mutation is garbage-collected as soon as it has no observer (`gcTime: 0`): callers
 * `reset()` it after every outcome, which drops it from the MutationCache. Use the same pattern for any secret
 * (e.g. a future Vercel token).
 */
export function useCreateLlmConnection() {
  const invalidate = useInvalidateConnections()
  return useMutation({
    gcTime: 0,
    mutationFn: async (body: Schemas['CreateLlmCredentialRequest']) =>
      unwrap(await api.POST('/api/me/connections/llm', { body })),
    onSuccess: invalidate,
  })
}

export function useRenameLlmConnection() {
  const invalidate = useInvalidateConnections()
  return useMutation({
    mutationFn: async ({ id, label }: { id: string; label: string }) =>
      unwrap(await api.PATCH('/api/me/connections/llm/{id}', { params: { path: { id } }, body: { label } })),
    onSuccess: invalidate,
  })
}

/** FR-78/FR-79: deletes a connection; the projects it was bound to lose their binding. */
export function useDeleteLlmConnection() {
  const invalidate = useInvalidateConnections()
  return useMutation({
    mutationFn: async (id: string) => {
      const { error, response } = await api.DELETE('/api/me/connections/llm/{id}', { params: { path: { id } } })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: invalidate,
  })
}

/** FR-78: re-checks a key with the provider (status valid/invalid; 502 when unreachable, status unchanged). */
export function useTestLlmConnection() {
  const invalidate = useInvalidateConnections()
  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(await api.POST('/api/me/connections/llm/{id}/test', { params: { path: { id } } })),
    onSettled: invalidate,
  })
}

/** Admin → Integrations: the system-key policy (read-only, set in configuration). */
export function useAdminLlmPolicy() {
  return useQuery({
    queryKey: connectionKeys.adminLlmPolicy(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/admin/integrations/llm', { signal })),
  })
}
