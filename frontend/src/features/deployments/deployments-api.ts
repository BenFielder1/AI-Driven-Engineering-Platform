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
export type PreviewCheck = Schemas['PreviewCheckDto']
/** FR-144: one target card of Settings → Deployment (`DeploymentTarget` is the environment, a v2 name). */
export type TargetCard = Schemas['DeploymentTargetDto']
export type TargetBinding = Schemas['DeploymentTargetBindingDto']
export type TargetStatus = Schemas['DeploymentTargetStatus']
export type DeploymentComponent = Schemas['DeploymentComponent']
export type ReadinessItem = Schemas['ReadinessItemDto']
export type ReadinessState = Schemas['ReadinessState']
/** FR-152: one database card per environment. */
export type DatabaseCard = Schemas['DeploymentDatabaseDto']
export type ManagedDatabaseProvider = Schemas['ManagedDatabaseProvider']
/** FR-147, FR-149: a preview or production rollout and its steps (migrations, backend, frontend). */
export type Rollout = Schemas['RolloutDto']
export type RolloutStep = Schemas['RolloutStepDto']
export type RolloutState = Schemas['RolloutState']
export type RolloutStepState = Schemas['RolloutStepState']
export type PreviewEnvironment = Schemas['PreviewEnvironmentDto']
export type LiveTarget = Schemas['LiveTargetDto']
export type PreviewResetMethod = Schemas['PreviewResetMethod']

export const deploymentComponents = ['frontend', 'backend'] as const satisfies readonly DeploymentComponent[]

export const deploymentTargets = ['preview', 'production'] as const satisfies readonly DeploymentTarget[]

/** §4.15: queued and building are still being polled; ready, error and canceled are final. */
export const isActiveDeployment = (status: DeploymentStatus) => status === 'queued' || status === 'building'

export interface DeploymentListParams {
  /** FR-151 filters. */
  component?: DeploymentComponent | undefined
  environment?: DeploymentTarget | undefined
  /** The v2 name of `environment`, still accepted by the API. */
  target?: DeploymentTarget | undefined
  releaseVersionId?: string | undefined
}

/** Keys under the project detail key, so a `release`-category live event (DEPLOYMENT_CREATED/UPDATED) refreshes them. */
export const deploymentKeys = {
  all: (id: string) => [...projectKeys.detail(id), 'deployments'] as const,
  list: (id: string, params: DeploymentListParams) => [...deploymentKeys.all(id), 'list', params] as const,
  settings: (id: string) => [...projectKeys.detail(id), 'deployment-settings'] as const,
  /** Under `all`, so a new or redeployed deployment refreshes the preview environment too. */
  preview: (id: string) => [...deploymentKeys.all(id), 'preview-environment'] as const,
}

/** While a deployment is queued or building, refetch every 10 s as a fallback to the live events (§4.15 cadence). */
const ACTIVE_REFETCH_MS = 10_000

/** FR-91, FR-151: the project's deployments, newest first, optionally by component, environment and release version. */
export function useDeployments(projectId: string, params: DeploymentListParams = {}, enabled = true) {
  return useQuery({
    queryKey: deploymentKeys.list(projectId, params),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/deployments', {
          params: {
            path: { id: projectId },
            query: {
              ...(params.component ? { component: params.component } : {}),
              ...(params.environment ? { environment: params.environment } : {}),
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

/**
 * Polling while a target's `deployment_setup` job is queued or running (FR-144). An object so tests can shorten it.
 */
export const deploymentSettingsPolling = { setupMs: 2_000 }

const jobActive = (state: string | undefined) => state === 'queued' || state === 'running'

/** A target's setup job or a database's provisioning job (FR-152) is still working. */
const setupRunning = (s: DeploymentSettings | undefined) =>
  (s?.targets.some((t) => jobActive(t.setup?.state)) ?? false) || (s?.databases.some((d) => jobActive(d.provisioning?.state)) ?? false)

/** FR-144: Settings → Deployment (targets, databases, env inventory, origin model), for any member. */
export function useDeploymentSettings(projectId: string, enabled = true) {
  return useQuery({
    queryKey: deploymentKeys.settings(projectId),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/settings/deployment', { params: { path: { id: projectId } }, signal })),
    enabled,
    refetchInterval: (query) => (setupRunning(query.state.data) ? deploymentSettingsPolling.setupMs : false),
  })
}

/** Replaces one target in the cached settings (after PUT or verify), so the card updates before the refetch. */
function useReplaceTarget(projectId: string) {
  const queryClient = useQueryClient()
  return (target: TargetCard) => {
    queryClient.setQueryData<DeploymentSettings>(deploymentKeys.settings(projectId), (old) =>
      old ? { ...old, targets: old.targets.map((t) => (t.component === target.component ? target : t)) } : old,
    )
  }
}

/** A target change moves the project's deploymentsEnabled and the connections' boundProjects too. */
function useInvalidateTargets(projectId: string) {
  const queryClient = useQueryClient()
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: deploymentKeys.settings(projectId) }),
      queryClient.invalidateQueries({ queryKey: projectKeys.detail(projectId), exact: true }),
      queryClient.invalidateQueries({ queryKey: connectionKeys.platforms() }),
    ])
}

/** FR-144 (Owner): bind a connection and create or link the platform resource; answers 202 with the target (setup job). */
export function usePutDeploymentTarget(projectId: string, component: DeploymentComponent) {
  const replace = useReplaceTarget(projectId)
  const invalidate = useInvalidateTargets(projectId)
  return useMutation({
    mutationFn: async (body: Schemas['PutDeploymentTargetRequest']) =>
      unwrap(
        await api.PUT('/api/projects/{id}/settings/deployment/targets/{component}', {
          params: { path: { id: projectId, component } },
          body,
        }),
      ),
    onSuccess: replace,
    onSettled: invalidate,
  })
}

/** FR-144 (Owner): unbind the target (the platform resource itself is left alone). */
export function useDeleteDeploymentTarget(projectId: string, component: DeploymentComponent) {
  const invalidate = useInvalidateTargets(projectId)
  return useMutation({
    mutationFn: async () => {
      const { error, response } = await api.DELETE('/api/projects/{id}/settings/deployment/targets/{component}', {
        params: { path: { id: projectId, component } },
      })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: invalidate,
  })
}

/** FR-144 (Owner): the readiness checklist; the target in the cache is replaced with the checked one. */
export function useVerifyDeploymentTarget(projectId: string, component: DeploymentComponent) {
  const replace = useReplaceTarget(projectId)
  return useMutation({
    mutationFn: async () =>
      unwrap(
        await api.POST('/api/projects/{id}/settings/deployment/targets/{component}/verify', {
          params: { path: { id: projectId, component } },
        }),
      ),
    onSuccess: (result) => {
      replace({ ...result.target, readiness: result.items })
    },
  })
}

export interface PutEnvVariables {
  key: string
  value: string
  targets: DeploymentTarget[]
  appliesTo: DeploymentComponent[]
  sensitive: boolean
  public: boolean
}

/**
 * FR-145: creates or replaces a user variable. The variables hold the plaintext value, so (as for connection tokens)
 * the mutation is collected as soon as it has no observer (`gcTime: 0`) and callers `reset()` it after every outcome.
 * A 502 still saved the value (with `syncError`), so the inventory is refreshed whatever happens.
 */
export function usePutDeploymentEnv(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    gcTime: 0,
    mutationFn: async ({ key, ...body }: PutEnvVariables) =>
      unwrap(
        await api.PUT('/api/projects/{id}/settings/deployment/env/{key}', {
          params: { path: { id: projectId, key } },
          body,
        }),
      ),
    onSettled: () => queryClient.invalidateQueries({ queryKey: deploymentKeys.settings(projectId) }),
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
    onSettled: () => queryClient.invalidateQueries({ queryKey: deploymentKeys.settings(projectId) }),
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

/** Polling while the current preview rollout runs (a fallback to the live events). An object so tests can shorten it. */
export const previewRolloutPolling = { runningMs: 10_000 }

/** FR-147: the project's persistent preview environment (the current rollout, notes, divergence), for any member. */
export function usePreviewEnvironment(projectId: string, enabled = true) {
  return useQuery({
    queryKey: deploymentKeys.preview(projectId),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/deployments/preview', { params: { path: { id: projectId } }, signal })),
    enabled,
    refetchInterval: (query) => (query.state.data?.current?.state === 'running' ? previewRolloutPolling.runningMs : false),
  })
}

/** FR-147: deploy the version branch head as a preview rollout now (writer); answers 202 with the rollout. */
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

/**
 * FR-91, FR-150: make an earlier ready production deployment current again (Owner), with the platform's own action
 * (Vercel promote, Netlify restore, Cloudflare version deploy, Render/Railway rollback). Git is left alone.
 */
export function useRollbackDeployment(projectId: string) {
  const invalidate = useInvalidateDeployments(projectId)
  return useMutation({
    mutationFn: async (deploymentId: string) =>
      unwrap(
        await api.POST('/api/projects/{id}/deployments/{deploymentId}/rollback', {
          params: { path: { id: projectId, deploymentId } },
        }),
      ),
    onSettled: invalidate,
  })
}

/** Replaces one database card in the cached settings (after PUT or restore), so it updates before the refetch. */
function useReplaceDatabase(projectId: string) {
  const queryClient = useQueryClient()
  return (card: DatabaseCard) => {
    queryClient.setQueryData<DeploymentSettings>(deploymentKeys.settings(projectId), (old) =>
      old ? { ...old, databases: old.databases.map((d) => (d.environment === card.environment ? card : d)) } : old,
    )
  }
}

/**
 * FR-152 (Owner): bind a connection and provision, link or enter an External database; answers 202 with the card
 * (provisioning job). The body can hold an External connection string, so the mutation is collected as soon as it
 * has no observer (`gcTime: 0`), as for connection tokens.
 */
export function usePutManagedDatabase(projectId: string, environment: DeploymentTarget) {
  const replace = useReplaceDatabase(projectId)
  const invalidate = useInvalidateTargets(projectId)
  return useMutation({
    gcTime: 0,
    mutationFn: async (body: Schemas['PutManagedDatabaseRequest']) =>
      unwrap(
        await api.PUT('/api/projects/{id}/settings/deployment/databases/{environment}', {
          params: { path: { id: projectId, environment } },
          body,
        }),
      ),
    onSuccess: replace,
    onSettled: invalidate,
  })
}

/** FR-152 (Owner): unlink the environment's database (nothing is deleted on the platform). */
export function useDeleteManagedDatabase(projectId: string, environment: DeploymentTarget) {
  const invalidate = useInvalidateTargets(projectId)
  return useMutation({
    mutationFn: async () => {
      const { error, response } = await api.DELETE('/api/projects/{id}/settings/deployment/databases/{environment}', {
        params: { path: { id: projectId, environment } },
      })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: invalidate,
  })
}

/** FR-152, §4.21 (Owner): restore a paused (Supabase `INACTIVE`) database; answers 202 with the card. */
export function useRestoreManagedDatabase(projectId: string, environment: DeploymentTarget) {
  const replace = useReplaceDatabase(projectId)
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () =>
      unwrap(
        await api.POST('/api/projects/{id}/settings/deployment/databases/{environment}/restore', {
          params: { path: { id: projectId, environment } },
        }),
      ),
    onSuccess: replace,
    onSettled: () => queryClient.invalidateQueries({ queryKey: deploymentKeys.settings(projectId) }),
  })
}

/**
 * FR-148 (Owner): reset the preview database (it drops the preview data); answers 202 with the job and how it is done
 * (`manual` on Railway: the Owner empties it there). Refreshes the cards and the preview environment's divergence.
 */
export function useResetPreviewDatabase(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () =>
      unwrap(
        await api.POST('/api/projects/{id}/settings/deployment/databases/preview/reset', {
          params: { path: { id: projectId } },
        }),
      ),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: deploymentKeys.settings(projectId) }),
        queryClient.invalidateQueries({ queryKey: deploymentKeys.preview(projectId) }),
      ]),
  })
}
