import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { projectKeys, unwrap } from '../projects/projects-api'

export type Release = Schemas['ReleaseDto']
export type ReleaseSummary = Schemas['ReleaseSummaryDto']
export type ReleaseStatus = Schemas['ReleaseStatus']
export type Rollback = Schemas['RollbackDto']
export type RollbackRequest = Schemas['RollbackRequest']

/**
 * FR-42, FR-43, FR-64: the release attempts of a release version (default: the current one), newest first. Live
 * `release` events refresh it (FR-46).
 */
export function useReleases(id: string, enabled = true, releaseVersionId?: string) {
  return useQuery({
    queryKey: [...projectKeys.releases(id), releaseVersionId ?? 'current'] as const,
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/releases', {
          params: { path: { id }, query: releaseVersionId ? { releaseVersionId } : {} },
          signal,
        }),
      ),
    enabled,
  })
}

/**
 * FR-43: queues the release job again (Owner) after the latest attempt failed, e.g. once a merge conflict is resolved
 * on the project branch. 202 with the new `in_progress` release; the outcome arrives as RELEASE_COMPLETED or
 * RELEASE_FAILED. 409 NOT_RELEASING, RELEASE_IN_PROGRESS or CONFLICT.
 */
export function useRetryRelease(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/projects/{id}/release', { params: { path: { id } } })),
    onSuccess: (release) => {
      queryClient.setQueryData<Release[]>([...projectKeys.releases(id), 'current'], (old) =>
        old ? [release, ...old.filter((r) => r.id !== release.id)] : old,
      )
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: projectKeys.state(id) }),
        queryClient.invalidateQueries({ queryKey: projectKeys.releases(id) }),
      ])
    },
    onError: () =>
      // NOT_RELEASING / RELEASE_IN_PROGRESS mean the page is stale: show the latest state.
      Promise.all([
        queryClient.invalidateQueries({ queryKey: projectKeys.state(id) }),
        queryClient.invalidateQueries({ queryKey: projectKeys.releases(id) }),
      ]),
  })
}

/** FR-44: recorded rollbacks. */
export function useRollbacks(id: string) {
  return useQuery({
    queryKey: projectKeys.rollbacks(id),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/rollbacks', { params: { path: { id } }, signal })),
  })
}

/**
 * FR-44, FR-45, FR-63: rolls the project back to a checkpoint of the version in development (Owner). On success the
 * phase, checkpoint, artifacts, steps, gates and review all
 * change (§4.8), so every query of the project and the projects list are invalidated. 400 VALIDATION /
 * INVALID_TARGET_VERSION, 409 ROLLBACK_BLOCKED / CONFLICT, 429 RATE_LIMITED (Retry-After).
 */
export function useRollback(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: RollbackRequest) =>
      unwrap(await api.POST('/api/projects/{id}/rollback', { params: { path: { id } }, body })),
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: projectKeys.detail(id) }),
        queryClient.invalidateQueries({ queryKey: projectKeys.lists() }),
      ]),
  })
}
