import { useQuery } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import type { ArtifactType } from '../artifacts/artifacts-api'
import { projectKeys, unwrap } from '../projects/projects-api'

export type ExecutionSteps = Schemas['ExecutionStepsDto']
export type ExecutionStep = Schemas['ExecutionStepDto']
export type StepStatus = Schemas['StepStatus']

/** Query keys, all under `projectKeys.execution(id)` so live `step`/`artifact` events refresh every steps view. */
export const executionKeys = {
  all: (id: string) => projectKeys.execution(id),
  steps: (id: string, planType: ArtifactType, planVersion: number | null) =>
    [...projectKeys.execution(id), 'steps', planType, planVersion ?? 'current'] as const,
}

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
  })
}
