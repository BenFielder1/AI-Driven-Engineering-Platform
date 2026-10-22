import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import type { ArtifactType } from '../artifacts/artifacts-api'
import { projectKeys, unwrap, type ProjectState } from '../projects/projects-api'

export type ExecutionSteps = Schemas['ExecutionStepsDto']
export type ExecutionStep = Schemas['ExecutionStepDto']
export type StepStatus = Schemas['StepStatus']
export type StepViolation = Schemas['StepViolationDto']
export type ExecutionState = Schemas['ExecutionStateDto']
export type ExecutionRunStatus = Schemas['ExecutionRunStatus']
export type ExecutionCounts = Schemas['ExecutionCountsDto']
export type ExecutionAction = 'start' | 'pause' | 'resume'
export type ExecutionRun = Schemas['ExecutionRunDto']
export type ExecutionStepSummary = Schemas['ExecutionStepSummaryDto']
export type PlanRunStatus = Schemas['PlanRunStatus']
export type StepAttempt = Schemas['StepAttemptDto']
export type ToolCall = Schemas['ToolCallDto']

/** FR-33: the first attempt plus up to 3 retries; the 4th rejection blocks the step. */
export const MAX_STEP_ATTEMPTS = 4

/** Fallback refresh while a step is running, for when the live connection is down (FR-46 normally refreshes first). */
const RUNNING_REFETCH_MS = 15_000

/** Query keys, all under `projectKeys.execution(id)` so live `step`/`artifact` events refresh every steps view. */
export const executionKeys = {
  all: (id: string) => projectKeys.execution(id),
  steps: (id: string, planType: ArtifactType, planVersion: number | null, releaseVersionId?: string) =>
    [...projectKeys.execution(id), 'steps', planType, planVersion ?? 'current', releaseVersionId ?? 'current'] as const,
  runs: (id: string, releaseVersionId?: string) => [...projectKeys.execution(id), 'runs', releaseVersionId ?? 'current'] as const,
  attempts: (id: string, stepId: string) => [...projectKeys.execution(id), 'attempts', stepId] as const,
}

const isActive = (data: ExecutionSteps | undefined) =>
  data?.steps.some((s) => s.status === 'in_progress' || s.status === 'rejected') ?? false

/**
 * FR-30: the execution steps of a plan version, in step order (members). `planVersion` null means the current
 * (highest locked) version; with no locked plan the result has `plan: null` and no steps. Steps exist only for plan
 * versions that were approved.
 */
export function useExecutionSteps(
  id: string,
  planType: ArtifactType,
  planVersion: number | null,
  enabled = true,
  releaseVersionId?: string,
) {
  return useQuery({
    queryKey: executionKeys.steps(id, planType, planVersion, releaseVersionId),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/execution/steps', {
          params: {
            path: { id },
            query: {
              planType,
              ...(planVersion === null ? {} : { planVersion }),
              ...(releaseVersionId ? { releaseVersionId } : {}),
            },
          },
          signal,
        }),
      ),
    enabled,
    refetchInterval: (query) => (isActive(query.state.data) ? RUNNING_REFETCH_MS : false),
  })
}

const runActive = (runs: ExecutionRun[] | undefined) =>
  runs?.some((r) => r.status === 'running' || r.steps.some((s) => s.status === 'in_progress')) ?? false

/**
 * FR-57: every execution run of a release version (default: the current one): the development run first, then one
 * review-fix run per review cycle. Live `step` events refresh it (it sits under `projectKeys.execution`).
 */
export function useExecutionRuns(id: string, releaseVersionId?: string) {
  return useQuery({
    queryKey: executionKeys.runs(id, releaseVersionId),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/execution/runs', {
          params: { path: { id }, query: releaseVersionId ? { releaseVersionId } : {} },
          signal,
        }),
      ),
    refetchInterval: (query) => (runActive(query.state.data) ? RUNNING_REFETCH_MS : false),
  })
}

/** FR-57: a step's attempt history, oldest first; loaded only once the user opens it (`enabled`). */
export function useStepAttempts(id: string, stepId: string, enabled: boolean) {
  return useQuery({
    queryKey: executionKeys.attempts(id, stepId),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/execution/steps/{stepId}/attempts', { params: { path: { id, stepId } }, signal })),
    enabled,
  })
}

const paths = {
  start: '/api/projects/{id}/execution/start',
  pause: '/api/projects/{id}/execution/pause',
  resume: '/api/projects/{id}/execution/resume',
} as const

/**
 * FR-31, FR-37: start, pause or resume execution (writers). The 202 state goes into the cached /state straight away
 * (status, pause flag and counts); step progress arrives with the live `step` events. Errors (PHASE_PRECONDITION_FAILED,
 * BUDGET_EXCEEDED, CONFLICT) mean our view may be stale, so the project is refetched either way.
 */
export function useExecutionControl(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (action: ExecutionAction) => unwrap(await api.POST(paths[action], { params: { path: { id } } })),
    onSuccess: (state) => {
      queryClient.setQueryData<ProjectState>(projectKeys.state(id), (current) =>
        current
          ? { ...current, executionStatus: state.status, executionPaused: state.paused, execution: state.counts }
          : current,
      )
    },
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: projectKeys.state(id) }),
        queryClient.invalidateQueries({ queryKey: projectKeys.detail(id), exact: true }),
        queryClient.invalidateQueries({ queryKey: projectKeys.execution(id) }),
      ]),
  })
}
