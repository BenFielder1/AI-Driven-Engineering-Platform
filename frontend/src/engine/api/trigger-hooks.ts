import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { toApiError } from '../../lib/errors'
import { unwrap } from './hooks'
import { engineKeys } from './keys'

export type PipelineSchedule = Schemas['PipelineScheduleDto']
export type PipelineWebhook = Schemas['PipelineWebhookDto']
/** A webhook as created: the only time (with a rotation) its secret is in the browser. Never cached (FR-366). */
export type CreatedWebhook = Schemas['CreatedPipelineWebhookDto']

/** Trigger keys sit under `['engine']`. Secrets are never put under any query key. */
export const triggerKeys = {
  all: () => [...engineKeys.all, 'triggers'] as const,
  schedules: (projectId: string) => [...triggerKeys.all(), projectId, 'schedules'] as const,
  preview: (projectId: string, cron: string, timezone: string) =>
    [...triggerKeys.all(), projectId, 'schedule-preview', cron, timezone] as const,
  webhooks: (projectId: string) => [...triggerKeys.all(), projectId, 'webhooks'] as const,
}

async function expectOk(result: Promise<{ error?: unknown; response: Response }>): Promise<void> {
  const { error, response } = await result
  if (!response.ok) {
    throw toApiError(error, response)
  }
}

/** FR-336, FR-366: the project's schedules, each with its next 3 fire times (UTC). */
export function usePipelineSchedules(projectId: string) {
  return useQuery({
    queryKey: triggerKeys.schedules(projectId),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/pipeline-schedules', { params: { path: { id: projectId } }, signal })),
  })
}

/** FR-366: the next 3 fire times of an unsaved cron and timezone (Owner). 400 SCHEDULE_INVALID. */
export function useSchedulePreview(projectId: string, cron: string, timezone: string, enabled: boolean) {
  return useQuery({
    queryKey: triggerKeys.preview(projectId, cron, timezone),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/pipeline-schedules/preview', {
          params: { path: { id: projectId }, query: { cron, timezone } },
          signal,
        }),
      ),
    enabled,
    placeholderData: keepPreviousData,
    staleTime: 60_000,
  })
}

export interface CreateScheduleVariables {
  pipelineId: string
  cron: string
  timezone: string
}

/** FR-336: schedule a pipeline (Owner). 400 SCHEDULE_INVALID, VALIDATION or INPUT_INVALID; 409 TRIGGER_NOT_SUPPORTED. */
export function useCreatePipelineSchedule(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: CreateScheduleVariables) =>
      unwrap(await api.POST('/api/projects/{id}/pipeline-schedules', { params: { path: { id: projectId } }, body })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: triggerKeys.schedules(projectId) }),
  })
}

/** FR-336: pause or resume a schedule (Owner). */
export function useSetSchedulePaused(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ scheduleId, paused }: { scheduleId: string; paused: boolean }) =>
      unwrap(
        await api.PATCH('/api/projects/{id}/pipeline-schedules/{scheduleId}', {
          params: { path: { id: projectId, scheduleId } },
          body: { paused },
        }),
      ),
    onSettled: () => queryClient.invalidateQueries({ queryKey: triggerKeys.schedules(projectId) }),
  })
}

/** FR-336: delete a schedule (Owner). */
export function useDeletePipelineSchedule(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (scheduleId: string) =>
      expectOk(
        api.DELETE('/api/projects/{id}/pipeline-schedules/{scheduleId}', { params: { path: { id: projectId, scheduleId } } }),
      ),
    onSettled: () => queryClient.invalidateQueries({ queryKey: triggerKeys.schedules(projectId) }),
  })
}

/** FR-337, FR-366: the project's webhooks, never with a secret. */
export function usePipelineWebhooks(projectId: string) {
  return useQuery({
    queryKey: triggerKeys.webhooks(projectId),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/pipeline-webhooks', { params: { path: { id: projectId } }, signal })),
  })
}

/**
 * FR-337, FR-366: create a webhook (Owner). The response's secret goes to the caller once and nowhere else: the list is
 * refetched (without secrets) rather than updated from the response, and the mutation keeps no data (`gcTime: 0`).
 */
export function useCreatePipelineWebhook(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (pipelineId: string) =>
      unwrap(await api.POST('/api/projects/{id}/pipeline-webhooks', { params: { path: { id: projectId } }, body: { pipelineId } })),
    gcTime: 0,
    onSettled: () => queryClient.invalidateQueries({ queryKey: triggerKeys.webhooks(projectId) }),
  })
}

/** FR-337, FR-366: a new secret, shown once; the old one stays valid for 24 hours (Owner). 409 CONFLICT. */
export function useRotatePipelineWebhook(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (webhookId: string) =>
      unwrap(
        await api.POST('/api/projects/{id}/pipeline-webhooks/{webhookId}/rotate', {
          params: { path: { id: projectId, webhookId } },
        }),
      ),
    gcTime: 0,
    onSettled: () => queryClient.invalidateQueries({ queryKey: triggerKeys.webhooks(projectId) }),
  })
}

/** FR-337: delete a webhook (Owner). */
export function useDeletePipelineWebhook(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (webhookId: string) =>
      expectOk(api.DELETE('/api/projects/{id}/pipeline-webhooks/{webhookId}', { params: { path: { id: projectId, webhookId } } })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: triggerKeys.webhooks(projectId) }),
  })
}
