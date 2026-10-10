import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { projectKeys, unwrap } from '../projects/projects-api'

export type Release = Schemas['ReleaseDto']
export type ReleaseSummary = Schemas['ReleaseSummaryDto']
export type ReleaseStatus = Schemas['ReleaseStatus']
export type Rollback = Schemas['RollbackDto']
export type RollbackRequest = Schemas['RollbackRequest']
export type ProjectVersion = Schemas['ProjectVersionDto']
export type RollbackTargetBlock = NonNullable<Schemas['RollbackTargetBlock']>

/** FR-42, FR-43: every release attempt, newest first. Live `release` events refresh it (FR-46). */
export function useReleases(id: string, enabled = true) {
  return useQuery({
    queryKey: projectKeys.releases(id),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/releases', { params: { path: { id } }, signal })),
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
      queryClient.setQueryData<Release[]>(projectKeys.releases(id), (old) =>
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

/** FR-44: project versions with their rollback eligibility, for the target picker (Owner dialog). */
export function useProjectVersions(id: string, enabled: boolean) {
  return useQuery({
    queryKey: projectKeys.versions(id),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/versions', { params: { path: { id } }, signal })),
    enabled,
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
 * FR-44, FR-45: rolls the project back (Owner). On success the phase, version, artifacts, steps, gates and review all
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
