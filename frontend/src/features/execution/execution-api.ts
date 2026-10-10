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

/** FR-33: the first attempt plus up to 3 retries; the 4th rejection blocks the step. */
export const MAX_STEP_ATTEMPTS = 4

/** Fallback refresh while a step is running, for when the live connection is down (FR-46 normally refreshes first). */
const RUNNING_REFETCH_MS = 15_000

/** Query keys, all under `projectKeys.execution(id)` so live `step`/`artifact` events refresh every steps view. */
export const executionKeys = {
  all: (id: string) => projectKeys.execution(id),
  steps: (id: string, planType: ArtifactType, planVersion: number | null) =>
    [...projectKeys.execution(id), 'steps', planType, planVersion ?? 'current'] as const,
}

const isActive = (data: ExecutionSteps | undefined) =>
  data?.steps.some((s) => s.status === 'in_progress' || s.status === 'rejected') ?? false

/**
 * FR-30: the execution steps of a plan version, in step order (members). `planVersion` null means the current
 * (highest locked) version; with no locked plan the result has `plan: null` and no steps. Steps exist only for plan
 * versions that were approved.
 */
export function useExecutionSteps(id: string, planType: ArtifactType, planVersion: number | null, enabled = true) {
  return useQuery({
    queryKey: executionKeys.steps(id, planType, planVersion),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/execution/steps', {
          params: { path: { id }, query: { planType, ...(planVersion === null ? {} : { planVersion }) } },
          signal,
        }),
      ),
    enabled,
    refetchInterval: (query) => (isActive(query.state.data) ? RUNNING_REFETCH_MS : false),
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
