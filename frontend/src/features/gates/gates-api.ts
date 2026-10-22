import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { invalidateProject } from '../lifecycle/lifecycle-api'
import { projectKeys, unwrap } from '../projects/projects-api'

export type Gate = Schemas['GateDto']
export type GateType = Schemas['GateType']
export type GateStatus = Schemas['GateStatus']
export type EscalationDecision = NonNullable<Schemas['EscalationDecision']>
export type EscalationReason = NonNullable<Schemas['EscalationReason']>
export type DecideGateRequest = Schemas['DecideGateRequest']

/**
 * FR-12–FR-15: every gate of the project, newest first (members); with `releaseVersionId`, only that release
 * version's gates (FR-62), filtered by the API.
 */
export function useGates(id: string, releaseVersionId?: string) {
  return useQuery({
    queryKey: releaseVersionId ? [...projectKeys.gates(id), 'version', releaseVersionId] : projectKeys.gates(id),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/gates', {
          params: { path: { id }, query: releaseVersionId ? { releaseVersionId } : {} },
          signal,
        }),
      ),
  })
}

interface DecideVariables {
  gateId: string
  body: DecideGateRequest
}

/**
 * FR-13/FR-14: approve a pending gate (Owner). Escalation gates send `decision` (and `targetCheckpoint` for a rollback).
 * Both outcomes refresh the project: success applies the transition, GATE_NOT_PENDING means our list is stale.
 */
export function useApproveGate(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ gateId, body }: DecideVariables) =>
      unwrap(await api.POST('/api/projects/{id}/gates/{gateId}/approve', { params: { path: { id, gateId } }, body })),
    onSettled: () => invalidateProject(queryClient, id),
  })
}

/** FR-13: reject a pending gate (Owner); the comment is the feedback for the re-run agent. */
export function useRejectGate(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ gateId, body }: DecideVariables) =>
      unwrap(await api.POST('/api/projects/{id}/gates/{gateId}/reject', { params: { path: { id, gateId } }, body })),
    onSettled: () => invalidateProject(queryClient, id),
  })
}
