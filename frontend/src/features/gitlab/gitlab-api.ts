import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { toApiError } from '../../lib/errors'
import { authKeys } from '../auth/auth-api'
import { connectionKeys } from '../connections/connections-api'
import { projectKeys, unwrap } from '../projects/projects-api'

export type AdminGitLabIntegration = Schemas['AdminGitLabIntegrationDto']
export type AdminGitLabInstance = Schemas['AdminGitLabInstanceDto']
export type GitLabInstanceSummary = Schemas['GitLabInstanceSummaryDto']
export type GitLabConnection = Schemas['GitLabConnectionDto']
export type GitLabRepo = Schemas['GitLabRepoDto']
export type GitLabOwner = Schemas['GitLabOwnerDto']
export type GitLabPendingLink = Schemas['GitLabPendingLinkDto']

/** SPEC §4.19: the OAuth scopes every GitLab OAuth application registers (FR-130, FR-131). */
export const GITLAB_OAUTH_SCOPES = ['api', 'write_repository', 'openid', 'profile', 'email'] as const
/** FR-131 token fallback: the scopes an access token needs. */
export const GITLAB_TOKEN_SCOPES = ['api', 'write_repository'] as const

export const gitlabKeys = {
  all: ['gitlab'] as const,
  admin: () => ['admin', 'integrations', 'gitlab'] as const,
  connections: () => [...connectionKeys.all, 'gitlab'] as const,
  instances: () => [...connectionKeys.all, 'gitlab', 'instances'] as const,
  repos: (connectionId: string, search: string) => [...gitlabKeys.all, connectionId, 'repos', search] as const,
  owners: (connectionId: string) => [...gitlabKeys.all, connectionId, 'owners'] as const,
  pendingLink: () => [...authKeys.all, 'gitlab-link'] as const,
}

/**
 * FR-131: full-page navigation to the API's GitLab OAuth start (code + PKCE); GitLab redirects back through the API's
 * callback to `returnUrl`. `mode=signin` is "Continue with GitLab"; `connect` adds a connection for the signed-in user.
 */
export function gitlabStartHref(mode: 'signin' | 'connect', instanceId: string, returnUrl: string): string {
  const query = new URLSearchParams({ mode, instanceId, returnUrl })
  return `/api/auth/gitlab/start?${query.toString()}`
}

/**
 * FR-131: the instance's "new access token" page, prefilled with a name and the two scopes (GitLab reads `name` and
 * `scopes` from the query).
 */
export function gitlabTokenPageUrl(baseUrl: string): string {
  const base = baseUrl.replace(/\/+$/, '')
  return `${base}/-/user_settings/personal_access_tokens?name=AIDE&scopes=${GITLAB_TOKEN_SCOPES.join(',')}`
}

/** "gitlab.com" or "gitlab.example.com" (the host of a base URL), for compact labels. */
export function gitlabHost(baseUrl: string): string {
  try {
    return new URL(baseUrl).host
  } catch {
    return baseUrl
  }
}

// ---- Admin → Integrations → GitLab (FR-130) --------------------------------------------------------------------

export function useAdminGitLab() {
  return useQuery({
    queryKey: gitlabKeys.admin(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/admin/integrations/gitlab', { signal })),
  })
}

/** An instance change can change sign-in (providers), the connect dialog's instances and users' connections. */
function useInvalidateGitLabAdmin() {
  const queryClient = useQueryClient()
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: gitlabKeys.admin() }),
      queryClient.invalidateQueries({ queryKey: authKeys.providers() }),
      queryClient.invalidateQueries({ queryKey: gitlabKeys.connections() }),
    ])
}

/** FR-130: adds a self-managed instance (SSRF guard and reachability check on the server). Holds the client secret. */
export function useCreateGitLabInstance() {
  const invalidate = useInvalidateGitLabAdmin()
  return useMutation({
    gcTime: 0,
    mutationFn: async (body: Schemas['CreateGitLabInstanceRequest']) =>
      unwrap(await api.POST('/api/admin/integrations/gitlab', { body })),
    onSuccess: invalidate,
  })
}

/** FR-130: changes an instance; omitted fields are unchanged. May hold the client secret. */
export function useUpdateGitLabInstance() {
  const invalidate = useInvalidateGitLabAdmin()
  return useMutation({
    gcTime: 0,
    mutationFn: async ({ id, body }: { id: string; body: Schemas['UpdateGitLabInstanceRequest'] }) =>
      unwrap(
        await api.PATCH('/api/admin/integrations/gitlab/{instanceId}', { params: { path: { instanceId: id } }, body }),
      ),
    onSettled: invalidate,
  })
}

/** FR-130: re-runs the SSRF guard and the reachability check, and records the version. */
export function useCheckGitLabInstance() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(
        await api.POST('/api/admin/integrations/gitlab/{instanceId}/check', { params: { path: { instanceId: id } } }),
      ),
    onSuccess: (instance) => {
      queryClient.setQueryData<AdminGitLabIntegration>(gitlabKeys.admin(), (old) =>
        old ? { ...old, instances: old.instances.map((i) => (i.id === instance.id ? instance : i)) } : old,
      )
    },
  })
}

/** FR-130: removes an instance no project uses (409 CONFLICT otherwise), with its connections and sign-in links. */
export function useDeleteGitLabInstance() {
  const invalidate = useInvalidateGitLabAdmin()
  return useMutation({
    mutationFn: async (id: string) => {
      const { error, response } = await api.DELETE('/api/admin/integrations/gitlab/{instanceId}', {
        params: { path: { instanceId: id } },
      })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: invalidate,
  })
}

// ---- Connections → GitLab (FR-131) -----------------------------------------------------------------------------

export function useGitLabConnections(enabled = true) {
  return useQuery({
    queryKey: gitlabKeys.connections(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/me/connections/gitlab', { signal })),
    enabled,
  })
}

/** FR-131: the enabled instances the caller can connect to (`oauthConfigured`: OAuth connect offered). */
export function useGitLabInstances(enabled = true) {
  return useQuery({
    queryKey: gitlabKeys.instances(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/me/connections/gitlab/instances', { signal })),
    enabled,
  })
}

/** A connection change can change any project's binding (FR-136), and drops the picker's cached repos and owners. */
function useInvalidateGitLabConnections() {
  const queryClient = useQueryClient()
  return () => {
    queryClient.removeQueries({ queryKey: gitlabKeys.all })
    return Promise.all([
      queryClient.invalidateQueries({ queryKey: gitlabKeys.connections() }),
      queryClient.invalidateQueries({ queryKey: projectKeys.all }),
    ])
  }
}

/**
 * FR-131 token fallback: GitLab checks the token first (CREDENTIAL_INVALID / CONNECTION_SCOPE_INSUFFICIENT save
 * nothing). The variables hold the token, so the mutation is garbage-collected at once and callers `reset()` it after
 * every outcome (the LLM key pattern).
 */
export function useCreateGitLabTokenConnection() {
  const invalidate = useInvalidateGitLabConnections()
  return useMutation({
    gcTime: 0,
    mutationFn: async (body: Schemas['CreateGitLabTokenConnectionRequest']) =>
      unwrap(await api.POST('/api/me/connections/gitlab', { body })),
    onSuccess: invalidate,
  })
}

export function useDeleteGitLabConnection() {
  const invalidate = useInvalidateGitLabConnections()
  return useMutation({
    mutationFn: async (id: string) => {
      const { error, response } = await api.DELETE('/api/me/connections/gitlab/{id}', { params: { path: { id } } })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: invalidate,
  })
}

/** FR-131: checks the connection with GitLab again (valid, invalid or insufficient_scope; 502 leaves it unchanged). */
export function useTestGitLabConnection() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(await api.POST('/api/me/connections/gitlab/{id}/test', { params: { path: { id } } })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: gitlabKeys.connections() }),
  })
}

// ---- Sign-in link (FR-131, D-V4-18) ----------------------------------------------------------------------------

/** The GitLab sign-in waiting in this browser to be linked to the account with its email; `null` when none (404). */
export function useGitLabPendingLink(enabled = true) {
  return useQuery({
    queryKey: gitlabKeys.pendingLink(),
    queryFn: async ({ signal }): Promise<GitLabPendingLink | null> => {
      const { data, error, response } = await api.GET('/api/auth/gitlab/link', { signal })
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

/** FR-131: the signed-in owner of the matching account confirms the link (never silent). */
export function useConfirmGitLabLink() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => {
      const { error, response } = await api.POST('/api/auth/gitlab/link')
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSuccess: async () => {
      queryClient.setQueryData(gitlabKeys.pendingLink(), null)
      await queryClient.invalidateQueries({ queryKey: gitlabKeys.connections() })
    },
  })
}

export function useCancelGitLabLink() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => {
      const { error, response } = await api.DELETE('/api/auth/gitlab/link')
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSuccess: () => {
      queryClient.setQueryData(gitlabKeys.pendingLink(), null)
    },
  })
}

// ---- Repo picker and Create project (FR-132) --------------------------------------------------------------------

/** FR-132: the projects the connection's user can develop in, 30 per page (keyset cursor), searchable by name. */
export function useGitLabRepos(connectionId: string, search: string, enabled = true) {
  return useInfiniteQuery({
    queryKey: gitlabKeys.repos(connectionId, search),
    queryFn: async ({ signal, pageParam }) =>
      unwrap(
        await api.GET('/api/me/git/gitlab/repos', {
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

/** FR-132: the personal namespace and the groups where the user is Developer or above. */
export function useGitLabOwners(connectionId: string, enabled = true) {
  return useQuery({
    queryKey: gitlabKeys.owners(connectionId),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/me/git/gitlab/owners', { params: { query: { connectionId } }, signal })),
    enabled: enabled && connectionId !== '',
  })
}

export function useCreateGitLabRepo() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: Schemas['CreateGitLabRepoRequest']) =>
      unwrap(await api.POST('/api/me/git/gitlab/repos', { body })),
    onSuccess: (_repo, body) => queryClient.invalidateQueries({ queryKey: [...gitlabKeys.all, body.connectionId, 'repos'] }),
  })
}
