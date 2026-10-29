import { useMutation, useQuery } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { toApiError } from '../../lib/errors'
import { unwrap } from '../projects/projects-api'
import { connectionKeys, useInvalidateConnections } from './connections-api'

export type PlatformConnection = Schemas['PlatformConnectionDto']
export type PlatformConnectionStatus = Schemas['PlatformConnectionStatus']
export type PlatformProvider = Schemas['PlatformProvider']

/**
 * FR-142: the caller's platform connections (deployment platforms and databases), never the tokens. `provider` keeps
 * only that provider's connections (one shared cache entry; filtered with `select`).
 */
export function usePlatformConnections({ enabled = true, provider }: { enabled?: boolean; provider?: PlatformProvider } = {}) {
  return useQuery({
    queryKey: connectionKeys.platforms(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/me/connections/platforms', { signal })),
    enabled,
    select: provider ? (all: PlatformConnection[]) => all.filter((c) => c.provider === provider) : undefined,
  })
}

/**
 * FR-142: adds a pasted platform token; the API checks it with the platform first (CREDENTIAL_INVALID or
 * CONNECTION_SCOPE_INSUFFICIENT → nothing saved; a token missing permissions is saved as `insufficient_scope`). The
 * variables carry the plaintext token, so the mutation is garbage-collected as soon as it has no observer
 * (`gcTime: 0`) and callers `reset()` it after every outcome (the LLM key pattern).
 */
export function useCreatePlatformConnection() {
  const invalidate = useInvalidateConnections()
  return useMutation({
    gcTime: 0,
    mutationFn: async (body: Schemas['CreatePlatformConnectionRequest']) =>
      unwrap(await api.POST('/api/me/connections/platforms', { body })),
    onSuccess: invalidate,
  })
}

/** FR-142: renames a connection (the token is unchanged). */
export function useRenamePlatformConnection() {
  const invalidate = useInvalidateConnections()
  return useMutation({
    mutationFn: async ({ id, label }: { id: string; label: string }) =>
      unwrap(await api.PATCH('/api/me/connections/platforms/{id}', { params: { path: { id } }, body: { label } })),
    onSuccess: invalidate,
  })
}

/** FR-142: deletes a connection; the projects whose targets it was bound to lose that binding (history stays). */
export function useDeletePlatformConnection() {
  const invalidate = useInvalidateConnections()
  return useMutation({
    mutationFn: async (id: string) => {
      const { error, response } = await api.DELETE('/api/me/connections/platforms/{id}', { params: { path: { id } } })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: invalidate,
  })
}

/** FR-142: re-checks the token with the platform (valid, invalid or insufficient_scope; 502 when unreachable). */
export function useTestPlatformConnection() {
  const invalidate = useInvalidateConnections()
  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(await api.POST('/api/me/connections/platforms/{id}/test', { params: { path: { id } } })),
    onSettled: invalidate,
  })
}
