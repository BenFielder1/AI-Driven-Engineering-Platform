import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query'

import { api } from '../../api/client'
import { unwrap } from './hooks'
import { engineKeys } from './keys'

/** Gates per inbox page ("Load more" follows `next`). */
export const GATE_INBOX_PAGE_SIZE = 25

/** The inbox refetches this often while the page is visible (and whenever the window regains focus). */
export const GATE_INBOX_REFRESH_MS = 30_000

/**
 * FR-302, FR-307: pending gates across every project the user can see, newest first, a page at a time. The inbox spans
 * projects, so it doesn't join their live groups: it refetches on window focus and every 30 s while visible, and the
 * host's own gate decisions invalidate `engineKeys.gateInboxAll()`.
 */
export function useGateInbox(pipelineId: string | null) {
  return useInfiniteQuery({
    queryKey: engineKeys.gateInbox(pipelineId),
    queryFn: async ({ pageParam, signal }) =>
      unwrap(
        await api.GET('/api/pipeline-gates', {
          params: {
            query: {
              status: 'pending',
              limit: GATE_INBOX_PAGE_SIZE,
              ...(pipelineId ? { pipelineId } : {}),
              ...(pageParam ? { cursor: pageParam } : {}),
            },
          },
          signal,
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next ?? undefined,
    refetchOnWindowFocus: 'always',
    refetchInterval: GATE_INBOX_REFRESH_MS,
    refetchIntervalInBackground: false,
  })
}

export interface DecideGateVariables {
  runId: string
  gateId: string
  action: 'approve' | 'reject'
  comment: string | null
}

/**
 * FR-301, FR-307: approve or reject a pipeline run's gate (the project gate rules apply: 403 FORBIDDEN, 409
 * GATE_NOT_PENDING, plus 409 DEFINITION_UNAVAILABLE). Either way the inbox and the run's gates refetch: success removes
 * the gate, GATE_NOT_PENDING means the list was stale.
 */
export function useDecidePipelineGate() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ runId, gateId, action, comment }: DecideGateVariables) => {
      const init = { params: { path: { runId, gateId } }, body: { comment } }
      return unwrap(
        action === 'approve'
          ? await api.POST('/api/pipeline-runs/{runId}/gates/{gateId}/approve', init)
          : await api.POST('/api/pipeline-runs/{runId}/gates/{gateId}/reject', init),
      )
    },
    onSettled: (_data, _error, { runId }) =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: engineKeys.gateInboxAll() }),
        queryClient.invalidateQueries({ queryKey: engineKeys.run(runId) }),
      ]),
  })
}
