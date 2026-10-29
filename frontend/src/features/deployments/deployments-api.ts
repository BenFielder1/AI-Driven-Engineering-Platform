import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { toApiError } from '../../lib/errors'
import { connectionKeys } from '../connections/connections-api'
import { projectKeys, unwrap } from '../projects/projects-api'

export type Deployment = Schemas['DeploymentDto']
export type DeploymentStatus = Schemas['DeploymentStatus']
export type DeploymentTarget = Schemas['DeploymentEnvironment']
export type DeploymentSettings = Schemas['DeploymentSettingsDto']
export type DeploymentEnvVar = Schemas['DeploymentEnvVarDto']
export type RequiredEnvVar = Schemas['RequiredEnvVarDto']
export type PreviewCheck = Schemas['PreviewCheckDto']

export const deploymentTargets = ['preview', 'production'] as const satisfies readonly DeploymentTarget[]

/** §4.15: queued and building are still being polled; ready, error and canceled are final. */
export const isActiveDeployment = (status: DeploymentStatus) => status === 'queued' || status === 'building'

export interface DeploymentListParams {
  target?: DeploymentTarget | undefined
  releaseVersionId?: string | undefined
}

/** Keys under the project detail key, so a `release`-category live event (DEPLOYMENT_CREATED/UPDATED) refreshes them. */
export const deploymentKeys = {
  all: (id: string) => [...projectKeys.detail(id), 'deployments'] as const,
  list: (id: string, params: DeploymentListParams) => [...deploymentKeys.all(id), 'list', params] as const,
  settings: (id: string) => [...projectKeys.detail(id), 'deployment-settings'] as const,
  env: (id: string) => [...projectKeys.detail(id), 'deployment-env'] as const,
}

/** While a deployment is queued or building, refetch every 10 s as a fallback to the live events (§4.15 cadence). */
const ACTIVE_REFETCH_MS = 10_000

/** FR-91: the project's deployments, newest first, optionally by target and release version. */
export function useDeployments(projectId: string, params: DeploymentListParams = {}, enabled = true) {
  return useQuery({
    queryKey: deploymentKeys.list(projectId, params),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/deployments', {
          params: {
            path: { id: projectId },
            query: {
              ...(params.target ? { target: params.target } : {}),
              ...(params.releaseVersionId ? { releaseVersionId: params.releaseVersionId } : {}),
            },
          },
          signal,
        }),
      ),
    enabled,
    refetchInterval: (query) => (query.state.data?.some((d) => isActiveDeployment(d.status)) ? ACTIVE_REFETCH_MS : false),
  })
}

/** FR-88: Settings → Deployment, for any member. */
export function useDeploymentSettings(projectId: string, enabled = true) {
  return useQuery({
    queryKey: deploymentKeys.settings(projectId),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/settings/deployment', { params: { path: { id: projectId } }, signal })),
    enabled,
  })
}

/** FR-88: the env vars (Owner): keys, targets and sync state, never values. */
export function useDeploymentEnv(projectId: string, enabled = true) {
  return useQuery({
    queryKey: deploymentKeys.env(projectId),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/settings/deployment/env', { params: { path: { id: projectId } }, signal })),
    enabled,
  })
}

/** The project (deploymentsEnabled), its deployment settings and the caller's connections (boundProjects) change. */
export function useUpdateDeploymentSettings(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: Schemas['UpdateDeploymentSettingsRequest']) =>
      unwrap(await api.PATCH('/api/projects/{id}/settings/deployment', { params: { path: { id: projectId } }, body })),
    onSuccess: (settings) => {
      queryClient.setQueryData(deploymentKeys.settings(projectId), settings)
    },
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: projectKeys.detail(projectId) }),
        queryClient.invalidateQueries({ queryKey: connectionKeys.platforms() }),
      ]),
  })
}

export interface PutEnvVariables {
  key: string
  value: string
  targets: DeploymentTarget[]
  sensitive: boolean
}

/**
 * FR-88: creates or replaces an env var. The variables hold the plaintext value, so (as for connection tokens) the
 * mutation is collected as soon as it has no observer (`gcTime: 0`) and callers `reset()` it after every outcome.
 * A 502 still saved the value (with `syncError`), so the list is refreshed whatever happens.
 */
export function usePutDeploymentEnv(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    gcTime: 0,
    mutationFn: async ({ key, value, targets, sensitive }: PutEnvVariables) =>
      unwrap(
        await api.PUT('/api/projects/{id}/settings/deployment/env/{key}', {
          params: { path: { id: projectId, key } },
          body: { value, targets, sensitive },
        }),
      ),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: deploymentKeys.env(projectId) }),
        queryClient.invalidateQueries({ queryKey: deploymentKeys.settings(projectId) }),
      ]),
  })
}

export function useDeleteDeploymentEnv(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (key: string) => {
      const { error, response } = await api.DELETE('/api/projects/{id}/settings/deployment/env/{key}', {
        params: { path: { id: projectId, key } },
      })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: deploymentKeys.env(projectId) }),
        queryClient.invalidateQueries({ queryKey: deploymentKeys.settings(projectId) }),
      ]),
  })
}

/** A new deployment changes the list, /state's latestDeployment and the release's production deployment. */
function useInvalidateDeployments(projectId: string) {
  const queryClient = useQueryClient()
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: deploymentKeys.all(projectId) }),
      queryClient.invalidateQueries({ queryKey: projectKeys.state(projectId) }),
      queryClient.invalidateQueries({ queryKey: projectKeys.releases(projectId) }),
    ])
}

/** FR-90: deploy the version branch head as a preview now (writer). */
export function useCreatePreviewDeployment(projectId: string) {
  const invalidate = useInvalidateDeployments(projectId)
  return useMutation({
    mutationFn: async () =>
      unwrap(await api.POST('/api/projects/{id}/deployments/preview', { params: { path: { id: projectId } } })),
    onSettled: invalidate,
  })
}

/** FR-90, FR-91: deploy the same commit again (preview: writer; production: Owner). */
export function useRedeployDeployment(projectId: string) {
  const invalidate = useInvalidateDeployments(projectId)
  return useMutation({
    mutationFn: async (deploymentId: string) =>
      unwrap(
        await api.POST('/api/projects/{id}/deployments/{deploymentId}/redeploy', {
          params: { path: { id: projectId, deploymentId } },
        }),
      ),
    onSettled: invalidate,
  })
}

/** FR-91: promote an earlier ready production deployment (Owner): a production rollback that leaves Git alone. */
export function usePromoteDeployment(projectId: string) {
  const invalidate = useInvalidateDeployments(projectId)
  return useMutation({
    mutationFn: async (deploymentId: string) =>
      unwrap(
        await api.POST('/api/projects/{id}/deployments/{deploymentId}/promote', {
          params: { path: { id: projectId, deploymentId } },
        }),
      ),
    onSettled: invalidate,
  })
}
