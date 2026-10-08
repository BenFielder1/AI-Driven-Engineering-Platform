import { useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { projectKeys, unwrap } from '../projects/projects-api'

export type AdvanceResult = Schemas['AdvanceResultDto']

/**
 * Refreshes everything about a project after a lifecycle or gate change (project header, /state, gates, activity).
 * Live updates replace most of this in phase 5 (FR-46); mutations keep invalidating as the fallback.
 */
export function invalidateProject(queryClient: QueryClient, id: string) {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: projectKeys.detail(id) }),
    queryClient.invalidateQueries({ queryKey: projectKeys.lists() }),
  ])
}

/**
 * FR-11/FR-12: "advance to next phase" (writers). The result is either the new state, or the pending gate the approval
 * mode required (phase unchanged). 409 PHASE_PRECONDITION_FAILED (with reasons), GATE_PENDING or CONFLICT.
 */
export function useAdvancePhase(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: Schemas['AdvancePhaseRequest'] = {}) =>
      unwrap(await api.POST('/api/projects/{id}/phase/advance', { params: { path: { id } }, body })),
    onSuccess: (result) => {
      queryClient.setQueryData(projectKeys.state(id), result.state)
      return invalidateProject(queryClient, id)
    },
    // A conflict or an existing pending gate means what we show is stale.
    onError: () => invalidateProject(queryClient, id),
  })
}
