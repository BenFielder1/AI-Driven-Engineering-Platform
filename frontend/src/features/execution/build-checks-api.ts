import { useQuery, type QueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { projectKeys, unwrap } from '../projects/projects-api'

export type BuildCheck = Schemas['BuildCheckDto']
export type BuildCheckStatus = Schemas['BuildCheckStatus']
export type BuildCheckSource = Schemas['BuildCheckSourceDto']
export type BuildCheckFailure = Schemas['BuildCheckFailureDto']
export type BuildCheckUpdatedMessage = Schemas['BuildCheckUpdatedMessage']

/** Fallback refresh while a check waits, for when the live connection is down (FR-259 normally pushes first). */
const WAITING_REFETCH_MS = 15_000

/**
 * Under `projectKeys.execution(id)`, so the live `step` events (BUILD_CHECK_STARTED/COMPLETED, BUILD_FIX_PLANNED and the
 * step events of the fix steps) refresh it like the steps and runs.
 */
export const buildCheckKeys = {
  list: (id: string, versionId: string) => [...projectKeys.execution(id), 'build-checks', versionId] as const,
}

/**
 * Newest first, as the API lists them: by `startedAt`, latest first (ties by id, descending). Not by cycle and attempt:
 * after a rollback a new "Initial check" (attempt 0) starts later than a superseded attempt 2 of the same cycle.
 */
export const newestFirst = (a: BuildCheck, b: BuildCheck) =>
  Date.parse(b.startedAt) - Date.parse(a.startedAt) || (a.id < b.id ? 1 : a.id > b.id ? -1 : 0)

/** FR-259: the build checks of a release version, newest first (project viewers can read them). */
export function useBuildChecks(id: string, versionId: string | null) {
  return useQuery({
    queryKey: buildCheckKeys.list(id, versionId ?? 'none'),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{projectId}/versions/{versionId}/build-checks', {
          params: { path: { projectId: id, versionId: versionId ?? '' } },
          signal,
        }),
      ),
    enabled: versionId !== null,
    refetchInterval: (query) => (query.state.data?.some((c) => c.status === 'waiting') ? WAITING_REFETCH_MS : false),
  })
}

/**
 * FR-259: a live `buildCheckUpdated` message puts the check into the cached list of its version straight away (replacing
 * the same id, or adding it in newest-first order). A list that isn't cached yet is left to its first fetch.
 */
export function applyBuildCheckUpdate(queryClient: QueryClient, projectId: string, check: BuildCheck) {
  queryClient.setQueryData<BuildCheck[]>(buildCheckKeys.list(projectId, check.releaseVersionId), (current) =>
    current ? [...current.filter((c) => c.id !== check.id), check].sort(newestFirst) : current,
  )
}
