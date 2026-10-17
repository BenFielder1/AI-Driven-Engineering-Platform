import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { projectKeys, unwrap } from '../projects/projects-api'

export type MergeReadiness = Schemas['MergeReadinessDto']
export type MergeReadinessReason = Schemas['MergeReadinessReasonDto']
export type CheckRun = Schemas['CheckRunDto']

/**
 * FR-84: the last merge-readiness result of the version's PR (members). Only in the review and release phases (else
 * 409 WRONG_PHASE), so callers pass `enabled`. Live release events refresh it with the rest of the project.
 */
export function useMergeReadiness(id: string, enabled: boolean) {
  return useQuery({
    queryKey: projectKeys.mergeReadiness(id),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/git/merge-readiness', { params: { path: { id } }, signal })),
    enabled,
    // 409 WRONG_PHASE won't change on a retry.
    retry: false,
  })
}

/** FR-84 "Check again": asks GitHub again (`refresh=true`) and replaces the cached result. */
export function useRefreshMergeReadiness(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () =>
      unwrap(
        await api.GET('/api/projects/{id}/git/merge-readiness', { params: { path: { id }, query: { refresh: true } } }),
      ),
    onSuccess: (readiness) => {
      queryClient.setQueryData<MergeReadiness>(projectKeys.mergeReadiness(id), readiness)
    },
  })
}
