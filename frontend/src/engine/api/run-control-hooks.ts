import { useMutation, useQueries, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { unwrap } from './hooks'
import { engineKeys } from './keys'
import type { PipelineDefinition, PipelineRun, PipelineSummary } from './types'

export type ApprovalMode = Schemas['ApprovalMode']

/** The generated request types `inputs` as `Record<string, never>` (a JSON object per key); the values are JSON. */
type InputsBody = NonNullable<Schemas['StartPipelineRunRequest']['inputs']>
const asInputs = (inputs: Record<string, unknown>): InputsBody => inputs as InputsBody

/** FR-362: the definitions of the given pipelines (each at its registered hash), sharing the definition cache. */
export function usePipelineDefinitions(pipelines: readonly PipelineSummary[]) {
  return useQueries({
    queries: pipelines.map((p) => ({
      queryKey: engineKeys.definition(p.id, p.hash),
      queryFn: async ({ signal }: { signal: AbortSignal }) =>
        unwrap(
          await api.GET('/api/pipelines/{pipelineId}/definitions/{hash}', {
            params: { path: { pipelineId: p.id, hash: p.hash } },
            signal,
          }),
        ),
      staleTime: Infinity,
    })),
    combine: (results) => ({
      definitions: results.map((r) => r.data).filter((d): d is PipelineDefinition => d !== undefined),
      isPending: results.some((r) => r.isPending),
      isError: results.some((r) => r.isError),
    }),
  })
}

export interface StartRunVariables {
  pipelineId: string
  approvalMode: ApprovalMode | null
  inputs: Record<string, unknown>
}

/**
 * FR-331, FR-362: start a run of an engine-controlled pipeline (Contributor). Errors: 403 FORBIDDEN, 404 NOT_FOUND,
 * 409 RUN_CONTROLLED_BY_HOST or RUN_ALREADY_ACTIVE, 400 VALIDATION or INPUT_INVALID (details: the failing paths).
 */
export function useStartPipelineRun(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ pipelineId, approvalMode, inputs }: StartRunVariables) =>
      unwrap(
        await api.POST('/api/projects/{id}/pipeline-runs', {
          params: { path: { id: projectId } },
          body: { pipelineId, approvalMode, inputs: Object.keys(inputs).length > 0 ? asInputs(inputs) : null },
        }),
      ),
    onSuccess: (run) => {
      queryClient.setQueryData(engineKeys.runDetail(run.id), run)
      // The new run is in the list at once, so landing on it never shows "Run not found" while the list refetches.
      queryClient.setQueryData<PipelineRun[]>(engineKeys.projectRuns(projectId), (runs) =>
        runs && !runs.some((r) => r.id === run.id) ? [run, ...runs] : runs,
      )
      return queryClient.invalidateQueries({ queryKey: engineKeys.projectRuns(projectId) })
    },
  })
}

export interface SubmitInputVariables {
  artifactKey: string
  content: unknown
}

/**
 * FR-335, FR-362: add a version of an input artifact to a run waiting on input (Contributor). Errors: 400
 * INPUT_INVALID, 403 FORBIDDEN, 404 NOT_FOUND, 409 RUN_NOT_ACTIVE or RUN_CONTROLLED_BY_HOST. The run refetches.
 */
export function useSubmitPipelineInput(runId: string, projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ artifactKey, content }: SubmitInputVariables) =>
      unwrap(
        await api.POST('/api/pipeline-runs/{runId}/inputs/{artifactKey}', {
          params: { path: { runId, artifactKey } },
          body: content,
        }),
      ),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: engineKeys.run(runId) }),
        queryClient.invalidateQueries({ queryKey: engineKeys.projectRuns(projectId) }),
      ]),
  })
}
