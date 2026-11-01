import { useMutation, useQuery } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import type { operations } from '../../api/generated/schema'
import { toApiError } from '../../lib/errors'
import { unwrap } from '../projects/projects-api'
import { connectionKeys, useInvalidateConnections } from './connections-api'

export type PlatformConnection = Schemas['PlatformConnectionDto']
export type PlatformConnectionStatus = Schemas['PlatformConnectionStatus']
export type PlatformProvider = Schemas['PlatformProvider']
export type PlatformResource = Schemas['PlatformResourceDto']
/** FR-142 picker `type` (`GET …/platforms/:id/resources?type=`). */
export type PlatformResourceType = NonNullable<NonNullable<operations['ListPlatformConnectionResources']['parameters']['query']>['type']>

export const platformProviderIds = ['vercel', 'netlify', 'cloudflare', 'render', 'railway', 'supabase'] as const satisfies readonly PlatformProvider[]

/** Whether a deployment provider (or any id) is a platform with connections (GitHub Pages and Fake aren't). */
export const isPlatformProvider = (id: string): id is PlatformProvider => (platformProviderIds as readonly string[]).includes(id)

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

/**
 * FR-142, FR-144: the resources a connection reaches, for the "link an existing one" picker (Vercel `projects`, Netlify
 * `sites`, Cloudflare `workers`/`projects`, Render `services`, Railway `projects`).
 */
export function usePlatformResources(connectionId: string | null, type: PlatformResourceType | null) {
  return useQuery({
    queryKey: connectionKeys.platformResources(connectionId ?? '', type ?? ''),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/me/connections/platforms/{id}/resources', {
          params: { path: { id: connectionId ?? '' }, query: type ? { type } : {} },
          signal,
        }),
      ),
    enabled: connectionId !== null && type !== null,
  })
}
