import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { projectKeys, unwrap, type ProjectState } from '../projects/projects-api'

export type ReleaseVersion = Schemas['ReleaseVersionDto']
export type ReleaseVersionStatus = Schemas['ReleaseVersionStatus']
export type Checkpoint = Schemas['CheckpointDto']
export type CheckpointBlock = NonNullable<Schemas['CheckpointBlock']>
export type StartReleaseVersionRequest = Schemas['StartReleaseVersionRequest']

export const releaseVersionsQueryOptions = (id: string) =>
  queryOptions({
    queryKey: projectKeys.releaseVersions(id),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/versions', { params: { path: { id } }, signal })),
  })

/** FR-62: every release version of the project (in development, awaiting merge, released, imported), highest first. */
export function useReleaseVersions(id: string, enabled = true) {
  return useQuery({ ...releaseVersionsQueryOptions(id), enabled })
}

/**
 * FR-44, FR-63: the checkpoints of a release version (default: the one in development) with the phase recorded at each
 * and whether it is a rollback target now.
 */
export function useCheckpoints(id: string, releaseVersionId: string | undefined, enabled = true) {
  return useQuery({
    queryKey: [...projectKeys.checkpoints(id), releaseVersionId ?? 'current'] as const,
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/checkpoints', {
          params: { path: { id }, query: releaseVersionId ? { releaseVersionId } : {} },
          signal,
        }),
      ),
    enabled,
  })
}

/**
 * FR-60: starts a new release version (Owner). The project goes back to refinement at checkpoint 1, so every query of
 * the project and the projects list are refreshed. 400 VERSION_INVALID / PRESET_NOT_FOUND / VALIDATION; 409
 * VERSION_NOT_GREATER, TAG_EXISTS, VERSION_IN_PROGRESS, RELEASE_NOT_MERGED, NOT_RELEASED, CONFLICT; 502 UPSTREAM_ERROR.
 */
export function useStartReleaseVersion(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: StartReleaseVersionRequest) =>
      unwrap(await api.POST('/api/projects/{id}/versions', { params: { path: { id } }, body })),
    onSuccess: (result) => {
      queryClient.setQueryData<ProjectState>(projectKeys.state(id), result.state)
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: projectKeys.detail(id) }),
        queryClient.invalidateQueries({ queryKey: projectKeys.lists() }),
      ])
    },
  })
}
