import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { toApiError } from '../../lib/errors'
import { projectKeys, unwrap, type ProjectSettings } from '../projects/projects-api'

export type AdminGitHubApp = Schemas['AdminGitHubAppDto']

/** FR-143: GitHub Pages deploys need the App's Pages permission, which App registrations from before v4 lack. */
export const PAGES_PERMISSION_MISSING_COPY =
  "Pages permission missing: update the App's permissions in GitHub, then accept them on each installation"

export type GitHubConnection = Schemas['GitHubConnectionDto']
export type GitHubRepo = Schemas['GitHubRepoDto']
export type GitHubOwner = Schemas['GitHubOwnerDto']
export type GitHubManifest = Schemas['GitHubAppManifestResponseDto']

export const githubKeys = {
  all: ['github'] as const,
  adminApp: () => ['admin', 'integrations', 'github'] as const,
  connection: () => ['me', 'connections', 'github'] as const,
  repos: (search: string) => [...githubKeys.all, 'repos', search] as const,
  owners: () => [...githubKeys.all, 'owners'] as const,
  refs: (owner: string, repo: string) => [...githubKeys.all, 'refs', owner, repo] as const,
}

/** FR-81: full-page navigation to the API's GitHub App user authorization; it redirects back to `returnUrl`. */
export function connectGitHubHref(returnUrl: string): string {
  return `/api/me/connections/github/connect?returnUrl=${encodeURIComponent(returnUrl)}`
}

// ---- Admin → Integrations → GitHub (FR-80) -------------------------------------------------------------------

export function useAdminGitHubApp() {
  return useQuery({
    queryKey: githubKeys.adminApp(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/admin/integrations/github', { signal })),
  })
}

/** FR-80: starts the manifest flow; the page then posts `manifest` to `postUrl` with a real form submit. */
export function useCreateGitHubAppManifest() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: Schemas['CreateGitHubAppManifestRequest']) =>
      unwrap(await api.POST('/api/admin/integrations/github/manifest', { body })),
    onError: () => queryClient.invalidateQueries({ queryKey: githubKeys.adminApp() }),
  })
}

// ---- Connections → GitHub (FR-81) ----------------------------------------------------------------------------

export function useGitHubConnection(enabled = true) {
  return useQuery({
    queryKey: githubKeys.connection(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/me/connections/github', { signal })),
    enabled,
  })
}

export function useDisconnectGitHub() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => {
      const { error, response } = await api.DELETE('/api/me/connections/github')
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: async () => {
      // The picker's repos and owners belonged to the connection: drop them.
      queryClient.removeQueries({ queryKey: githubKeys.all })
      await queryClient.invalidateQueries({ queryKey: githubKeys.connection() })
    },
  })
}

// ---- Repo picker and Create repository (FR-81, FR-86) ----------------------------------------------------------

/** FR-81: the repos the App can access for the caller, 30 per page, searchable by name. */
export function useGitHubRepos(search: string, enabled = true) {
  return useInfiniteQuery({
    queryKey: githubKeys.repos(search),
    queryFn: async ({ signal, pageParam }) =>
      unwrap(
        await api.GET('/api/me/github/repos', {
          params: { query: { ...(search ? { search } : {}), page: pageParam } },
          signal,
        }),
      ),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.hasMore ? last.page + 1 : undefined),
    placeholderData: keepPreviousData,
    enabled,
  })
}

export function useGitHubOwners(enabled = true) {
  return useQuery({
    queryKey: githubKeys.owners(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/me/github/owners', { signal })),
    enabled,
  })
}

/** FR-86: a picked repo's default branch and whether it is empty (the list may not know). */
export function useGitHubRepoRefs(fullName: string | null) {
  const [owner = '', repo = ''] = (fullName ?? '').split('/')
  return useQuery({
    queryKey: githubKeys.refs(owner, repo),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/me/github/repos/{owner}/{repo}/refs', { params: { path: { owner, repo } }, signal })),
    enabled: Boolean(owner && repo),
  })
}

export function useCreateGitHubRepo() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: Schemas['CreateGitHubRepoRequest']) =>
      unwrap(await api.POST('/api/me/github/repos', { body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [...githubKeys.all, 'repos'] }),
  })
}

// ---- Settings → Git → Switch to GitHub App (FR-83) -------------------------------------------------------------

export function useSwitchGitToApp(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () =>
      unwrap(
        await api.POST('/api/projects/{id}/settings/git/switch-to-app', { params: { path: { id: projectId } } }),
      ),
    onSuccess: (git) => {
      queryClient.setQueryData<ProjectSettings>(projectKeys.settings(projectId), (old) => (old ? { ...old, git } : old))
    },
  })
}
