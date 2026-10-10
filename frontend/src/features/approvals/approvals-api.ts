import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { invalidateProject } from '../lifecycle/lifecycle-api'
import { notificationKeys } from '../notifications/notifications-api'
import { unwrap } from '../projects/projects-api'

export type ApprovalLinkView = Schemas['ApprovalLinkViewDto']

/**
 * FR-48: the token is a credential. It only ever goes into the API path (never logged, never in another request), and
 * the view isn't kept in the cache after the page closes.
 */
const approvalKey = (token: string) => ['approvals', token] as const

/** FR-48: what the link would decide (reading never uses the link). */
export function useApprovalLink(token: string) {
  return useQuery({
    queryKey: approvalKey(token),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/approvals/{token}', { params: { path: { token } }, signal })),
    gcTime: 0,
    retry: false,
    refetchOnWindowFocus: false,
  })
}

/** FR-48: confirms the link's decision; refreshes the gate's project and the user's notifications. */
export function useConfirmApprovalLink(token: string, projectId: string | undefined) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: Schemas['ConfirmApprovalLinkRequest']) =>
      unwrap(await api.POST('/api/approvals/{token}', { params: { path: { token } }, body })),
    onSettled: () =>
      Promise.all([
        projectId ? invalidateProject(queryClient, projectId) : Promise.resolve(),
        queryClient.invalidateQueries({ queryKey: notificationKeys.all }),
      ]),
  })
}
