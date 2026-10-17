import { useMutation, useQuery } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { toApiError } from '../../lib/errors'
import { unwrap } from '../projects/projects-api'
import { connectionKeys, useInvalidateConnections } from './connections-api'

export type VercelConnection = Schemas['VercelConnectionDto']
export type VercelConnectionStatus = Schemas['VercelConnectionStatus']

/** FR-87: the caller's Vercel connections, newest first (never the tokens). */
export function useVercelConnections(enabled = true) {
  return useQuery({
    queryKey: connectionKeys.vercel(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/me/connections/vercel', { signal })),
    enabled,
  })
}

/**
 * FR-87: adds a Vercel token; the API checks it with Vercel first (CREDENTIAL_INVALID → nothing saved). The
 * variables carry the plaintext token, so the mutation is garbage-collected as soon as it has no observer
 * (`gcTime: 0`) and callers `reset()` it after every outcome (the LLM key pattern).
 */
export function useCreateVercelConnection() {
  const invalidate = useInvalidateConnections()
  return useMutation({
    gcTime: 0,
    mutationFn: async (body: Schemas['CreateVercelConnectionRequest']) =>
      unwrap(await api.POST('/api/me/connections/vercel', { body })),
    onSuccess: invalidate,
  })
}

export function useRenameVercelConnection() {
  const invalidate = useInvalidateConnections()
  return useMutation({
    mutationFn: async ({ id, label }: { id: string; label: string }) =>
      unwrap(await api.PATCH('/api/me/connections/vercel/{id}', { params: { path: { id } }, body: { label } })),
    onSuccess: invalidate,
  })
}

/** FR-87/FR-88: deletes a connection; projects it was bound to lose their binding (settings and history stay). */
export function useDeleteVercelConnection() {
  const invalidate = useInvalidateConnections()
  return useMutation({
    mutationFn: async (id: string) => {
      const { error, response } = await api.DELETE('/api/me/connections/vercel/{id}', { params: { path: { id } } })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: invalidate,
  })
}

/** FR-87: re-checks the token with Vercel (valid/invalid; 502 when unreachable, status unchanged). */
export function useTestVercelConnection() {
  const invalidate = useInvalidateConnections()
  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(await api.POST('/api/me/connections/vercel/{id}/test', { params: { path: { id } } })),
    onSettled: invalidate,
  })
}
