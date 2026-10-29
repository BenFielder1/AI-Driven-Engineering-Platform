import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { toApiError } from '../../lib/errors'
import { authKeys } from '../auth/auth-api'
import { connectionKeys } from '../connections/connections-api'
import { projectKeys, unwrap } from '../projects/projects-api'

export type AdminBitbucketIntegration = Schemas['AdminBitbucketIntegrationDto']
export type BitbucketConnection = Schemas['BitbucketConnectionDto']
export type BitbucketRepo = Schemas['BitbucketRepoDto']
export type BitbucketWorkspace = Schemas['BitbucketWorkspaceDto']
export type BitbucketProject = Schemas['BitbucketProjectDto']
export type BitbucketPendingLink = Schemas['BitbucketPendingLinkDto']

/** FR-133, §4.19: the permissions to tick on the Bitbucket OAuth consumer (Bitbucket fixes scopes on the consumer). */
export const BITBUCKET_CONSUMER_PERMISSIONS = [
  'account',
  'email',
  'repository:write',
  'repository:admin',
  'pullrequest:write',
  'project',
  'webhook',
] as const

/** FR-134 token fallback: the scopes an Atlassian API token needs (each is `<scope>:bitbucket`). */
export const BITBUCKET_TOKEN_SCOPES = [
  'read:user',
  'read:workspace',
  'read:project',
  'read:repository',
  'write:repository',
  'admin:repository',
  'read:pullrequest',
  'write:pullrequest',
  'read:pipeline',
].map((s) => `${s}:bitbucket`)

/** FR-134: Atlassian's page for creating API tokens ("Create API token with scopes"). */
export const ATLASSIAN_API_TOKEN_PAGE = 'https://id.atlassian.com/manage-profile/security/api-tokens'

export const bitbucketKeys = {
  all: ['bitbucket'] as const,
  admin: () => ['admin', 'integrations', 'bitbucket'] as const,
  connections: () => [...connectionKeys.all, 'bitbucket'] as const,
  repos: (connectionId: string, search: string) => [...bitbucketKeys.all, connectionId, 'repos', search] as const,
  owners: (connectionId: string) => [...bitbucketKeys.all, connectionId, 'owners'] as const,
  pendingLink: () => [...authKeys.all, 'bitbucket-link'] as const,
}

/**
 * FR-134: full-page navigation to the API's Bitbucket OAuth start; Bitbucket redirects back through the API's callback
 * to `returnUrl`. `mode=signin` is "Continue with Bitbucket"; `connect` adds a connection for the signed-in user.
 */
export function bitbucketStartHref(mode: 'signin' | 'connect', returnUrl: string): string {
  const query = new URLSearchParams({ mode, returnUrl })
  return `/api/auth/bitbucket/start?${query.toString()}`
}

// ---- Admin → Integrations → Bitbucket (FR-133) -----------------------------------------------------------------

export function useAdminBitbucket() {
  return useQuery({
    queryKey: bitbucketKeys.admin(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/admin/integrations/bitbucket', { signal })),
  })
}

/** A consumer change turns sign-in and OAuth connect on or off (providers) and can invalidate OAuth connections. */
function useInvalidateBitbucketAdmin() {
  const queryClient = useQueryClient()
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: bitbucketKeys.admin() }),
      queryClient.invalidateQueries({ queryKey: authKeys.providers() }),
      queryClient.invalidateQueries({ queryKey: bitbucketKeys.connections() }),
    ])
}

/** FR-133: saves the consumer's key and secret (an omitted secret keeps the saved one). Holds the secret: gcTime 0. */
export function useUpdateBitbucketConsumer() {
  const invalidate = useInvalidateBitbucketAdmin()
  return useMutation({
    gcTime: 0,
    mutationFn: async (body: Schemas['UpdateBitbucketConsumerRequest']) =>
      unwrap(await api.PUT('/api/admin/integrations/bitbucket', { body })),
    onSettled: invalidate,
  })
}

/** FR-133: removes the saved consumer (409 CONFLICT when it comes from configuration). */
export function useDeleteBitbucketConsumer() {
  const invalidate = useInvalidateBitbucketAdmin()
  return useMutation({
    mutationFn: async () => {
      const { error, response } = await api.DELETE('/api/admin/integrations/bitbucket')
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: invalidate,
  })
}

// ---- Connections → Bitbucket (FR-134) --------------------------------------------------------------------------

export function useBitbucketConnections(enabled = true) {
  return useQuery({
    queryKey: bitbucketKeys.connections(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/me/connections/bitbucket', { signal })),
    enabled,
  })
}

/** A connection change can change any project's binding (FR-136), and drops the picker's cached repos and owners. */
function useInvalidateBitbucketConnections() {
  const queryClient = useQueryClient()
  return () => {
    queryClient.removeQueries({ queryKey: bitbucketKeys.all })
    return Promise.all([
      queryClient.invalidateQueries({ queryKey: bitbucketKeys.connections() }),
      queryClient.invalidateQueries({ queryKey: projectKeys.all }),
    ])
  }
}

/**
 * FR-134 token fallback: Bitbucket checks the token and each scope first (CREDENTIAL_INVALID /
 * CONNECTION_SCOPE_INSUFFICIENT save nothing). The variables hold the token, so the mutation is garbage-collected at
 * once and callers `reset()` it after every outcome.
 */
export function useCreateBitbucketTokenConnection() {
  const invalidate = useInvalidateBitbucketConnections()
  return useMutation({
    gcTime: 0,
    mutationFn: async (body: Schemas['CreateBitbucketTokenConnectionRequest']) =>
      unwrap(await api.POST('/api/me/connections/bitbucket', { body })),
    onSuccess: invalidate,
  })
}

export function useDeleteBitbucketConnection() {
  const invalidate = useInvalidateBitbucketConnections()
  return useMutation({
    mutationFn: async (id: string) => {
      const { error, response } = await api.DELETE('/api/me/connections/bitbucket/{id}', { params: { path: { id } } })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: invalidate,
  })
}

/** FR-134: checks the connection with Bitbucket again (valid, invalid or insufficient_scope; 502 leaves it unchanged). */
export function useTestBitbucketConnection() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(await api.POST('/api/me/connections/bitbucket/{id}/test', { params: { path: { id } } })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: bitbucketKeys.connections() }),
  })
}

// ---- Sign-in link (FR-134, D-V4-18) ----------------------------------------------------------------------------

/** The Bitbucket sign-in waiting in this browser to be linked to the account with its email; `null` when none (404). */
export function useBitbucketPendingLink(enabled = true) {
  return useQuery({
    queryKey: bitbucketKeys.pendingLink(),
    queryFn: async ({ signal }): Promise<BitbucketPendingLink | null> => {
      const { data, error, response } = await api.GET('/api/auth/bitbucket/link', { signal })
      if (data) {
        return data
      }
      if (response.status === 404) {
        return null
      }
      throw toApiError(error, response)
    },
    enabled,
    retry: false,
  })
}

/** FR-134: the signed-in owner of the matching account confirms the link (never silent). */
export function useConfirmBitbucketLink() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => {
      const { error, response } = await api.POST('/api/auth/bitbucket/link')
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSuccess: async () => {
      queryClient.setQueryData(bitbucketKeys.pendingLink(), null)
      await queryClient.invalidateQueries({ queryKey: bitbucketKeys.connections() })
    },
  })
}

export function useCancelBitbucketLink() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => {
      const { error, response } = await api.DELETE('/api/auth/bitbucket/link')
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSuccess: () => {
      queryClient.setQueryData(bitbucketKeys.pendingLink(), null)
    },
  })
}

// ---- Repo picker and Create repository (FR-135) -----------------------------------------------------------------

/** FR-135: the repositories the connection's user can write to, 30 per page (opaque cursor), searchable by name. */
export function useBitbucketRepos(connectionId: string, search: string, enabled = true) {
  return useInfiniteQuery({
    queryKey: bitbucketKeys.repos(connectionId, search),
    queryFn: async ({ signal, pageParam }) =>
      unwrap(
        await api.GET('/api/me/git/bitbucket/repos', {
          params: {
            query: { connectionId, ...(search ? { search } : {}), ...(pageParam ? { cursor: pageParam } : {}) },
          },
          signal,
        }),
      ),
    initialPageParam: '',
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    placeholderData: keepPreviousData,
    enabled: enabled && connectionId !== '',
  })
}

/** FR-135: the caller's workspaces, each with its projects (Bitbucket requires a project for a new repository). */
export function useBitbucketOwners(connectionId: string, enabled = true) {
  return useQuery({
    queryKey: bitbucketKeys.owners(connectionId),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/me/git/bitbucket/owners', { params: { query: { connectionId } }, signal })),
    enabled: enabled && connectionId !== '',
  })
}

export function useCreateBitbucketRepo() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: Schemas['CreateBitbucketRepoRequest']) =>
      unwrap(await api.POST('/api/me/git/bitbucket/repos', { body })),
    onSuccess: (_repo, body) =>
      queryClient.invalidateQueries({ queryKey: [...bitbucketKeys.all, body.connectionId, 'repos'] }),
  })
}
