import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { toApiError } from '../../lib/errors'
import { unwrap } from './hooks'
import { engineKeys } from './keys'

/** FR-351, FR-365: one agent feedback entry (`human` or `stage`). */
export type AgentFeedback = Schemas['AgentFeedbackDto']

/** CreateAgentFeedbackRequest.text: 1–2,000 characters (the API's limit). */
export const FEEDBACK_TEXT_MAX = 2000

/** Feedback keys sit under `['engine']`, so a live reconnect refreshes them with the rest of the engine UI. */
export const feedbackKeys = {
  all: () => [...engineKeys.all, 'feedback'] as const,
  project: (projectId: string) => [...feedbackKeys.all(), projectId] as const,
  agent: (projectId: string, pipelineId: string, agentKey: string) =>
    [...feedbackKeys.project(projectId), pipelineId, agentKey] as const,
}

/** FR-365: an agent's feedback entries in the project, newest first (Viewer and up). */
export function useAgentFeedback(projectId: string, pipelineId: string, agentKey: string | null) {
  return useQuery({
    queryKey: feedbackKeys.agent(projectId, pipelineId, agentKey ?? ''),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/pipelines/{pipelineId}/agents/{agentKey}/feedback', {
          params: { path: { id: projectId, pipelineId, agentKey: agentKey ?? '' } },
          signal,
        }),
      ),
    enabled: agentKey !== null,
  })
}

/** FR-365: add a feedback entry (Contributor). 400 VALIDATION (blank or too long), 403 FORBIDDEN, 404 NOT_FOUND. */
export function useCreateAgentFeedback(projectId: string, pipelineId: string, agentKey: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (text: string) =>
      unwrap(
        await api.POST('/api/projects/{id}/pipelines/{pipelineId}/agents/{agentKey}/feedback', {
          params: { path: { id: projectId, pipelineId, agentKey } },
          body: { text },
        }),
      ),
    onSettled: () => queryClient.invalidateQueries({ queryKey: feedbackKeys.agent(projectId, pipelineId, agentKey) }),
  })
}

/** FR-365: delete a feedback entry (Owner, or its author). 403 FORBIDDEN, 404 NOT_FOUND (already gone). */
export function useDeleteAgentFeedback(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (feedbackId: string) => {
      const { error, response } = await api.DELETE('/api/projects/{id}/agent-feedback/{feedbackId}', {
        params: { path: { id: projectId, feedbackId } },
      })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: feedbackKeys.project(projectId) }),
  })
}
