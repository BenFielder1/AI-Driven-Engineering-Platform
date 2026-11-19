import { useInfiniteQuery, useQuery } from '@tanstack/react-query'

import { api } from '../../api/client'
import { toApiError } from '../../lib/errors'
import { engineKeys } from './keys'

/** The data of an openapi-fetch result, or the API error (ProblemDetails → `ApiError`, mapped to copy in lib/errors). */
function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.data !== undefined) {
    return result.data
  }
  throw toApiError(result.error, result.response)
}

/** Timeline and replay entries per page (the API's default; FR-300). */
export const TIMELINE_PAGE_SIZE = 50

/** FR-299: the registered pipelines. */
export function usePipelines() {
  return useQuery({
    queryKey: engineKeys.pipelines(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/pipelines', { signal })),
  })
}

/** FR-299: a run's pinned definition. Definitions are immutable per hash, so they never go stale. */
export function usePipelineDefinition(pipelineId: string | undefined, hash: string | undefined) {
  return useQuery({
    queryKey: engineKeys.definition(pipelineId ?? '', hash ?? ''),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/pipelines/{pipelineId}/definitions/{hash}', {
          params: { path: { pipelineId: pipelineId ?? '', hash: hash ?? '' } },
          signal,
        }),
      ),
    enabled: Boolean(pipelineId && hash),
    staleTime: Infinity,
  })
}

/** FR-298: prompt comparison for a pipeline, over one project. */
export function usePromptStats(pipelineId: string | undefined, projectId: string) {
  return useQuery({
    queryKey: engineKeys.promptStats(pipelineId ?? '', projectId),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/pipelines/{pipelineId}/prompt-stats', {
          params: { path: { pipelineId: pipelineId ?? '' }, query: { projectId } },
          signal,
        }),
      ),
    enabled: Boolean(pipelineId),
  })
}

/** FR-300: the project's runs, newest first. */
export function usePipelineRuns(projectId: string) {
  return useQuery({
    queryKey: engineKeys.projectRuns(projectId),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/pipeline-runs', { params: { path: { id: projectId } }, signal })),
  })
}

/** FR-300: one run (status, checkpoint, cost and token totals). */
export function usePipelineRun(runId: string | undefined) {
  return useQuery({
    queryKey: engineKeys.runDetail(runId ?? ''),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/pipeline-runs/{runId}', { params: { path: { runId: runId ?? '' } }, signal })),
    enabled: Boolean(runId),
  })
}

/** FR-300: the run's stages in definition order, with state, iterations and timings. */
export function useRunStages(runId: string) {
  return useQuery({
    queryKey: engineKeys.stages(runId),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/pipeline-runs/{runId}/stages', { params: { path: { runId } }, signal })),
  })
}

/** FR-300: the run's timeline in time order, a page at a time ("Load more" follows `next`). */
export function useRunTimeline(runId: string) {
  return useInfiniteQuery({
    queryKey: engineKeys.timeline(runId),
    queryFn: async ({ pageParam, signal }) =>
      unwrap(
        await api.GET('/api/pipeline-runs/{runId}/timeline', {
          params: {
            path: { runId },
            query: { limit: TIMELINE_PAGE_SIZE, ...(pageParam ? { cursor: pageParam } : {}) },
          },
          signal,
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next ?? undefined,
  })
}

/** FR-303: the timeline with each agent run's input and output, for the replay. Read-only. */
export function useRunReplay(runId: string) {
  return useInfiniteQuery({
    queryKey: engineKeys.replay(runId),
    queryFn: async ({ pageParam, signal }) =>
      unwrap(
        await api.GET('/api/pipeline-runs/{runId}/replay', {
          params: {
            path: { runId },
            query: { limit: TIMELINE_PAGE_SIZE, ...(pageParam ? { cursor: pageParam } : {}) },
          },
          signal,
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next ?? undefined,
  })
}

/** FR-301: the run's artifacts by key, each with its versions (newest first). */
export function useRunArtifacts(runId: string) {
  return useQuery({
    queryKey: engineKeys.artifacts(runId),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/pipeline-runs/{runId}/artifacts', { params: { path: { runId } }, signal })),
  })
}

/** FR-301: one artifact version with its content. Versions are immutable once written. */
export function useRunArtifactVersion(runId: string, key: string | null, version: number | null) {
  return useQuery({
    queryKey: engineKeys.artifactVersion(runId, key ?? '', version ?? 0),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/pipeline-runs/{runId}/artifacts/{key}/versions/{version}', {
          params: { path: { runId, key: key ?? '', version: version ?? 0 } },
          signal,
        }),
      ),
    enabled: key !== null && version !== null,
  })
}

/** FR-301: the line diff between two versions of an artifact. */
export function useRunArtifactDiff(runId: string, key: string | null, pair: { from: number; to: number } | null) {
  return useQuery({
    queryKey: engineKeys.artifactDiff(runId, key ?? '', pair?.from ?? 0, pair?.to ?? 0),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/pipeline-runs/{runId}/artifacts/{key}/diff', {
          params: { path: { runId, key: key ?? '' }, query: { from: pair?.from ?? 0, to: pair?.to ?? 0 } },
          signal,
        }),
      ),
    enabled: key !== null && pair !== null,
    staleTime: Infinity,
  })
}

/** FR-301: the run's gates. */
export function useRunGates(runId: string | undefined) {
  return useQuery({
    queryKey: engineKeys.gates(runId ?? ''),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/pipeline-runs/{runId}/gates', { params: { path: { runId: runId ?? '' } }, signal })),
    enabled: Boolean(runId),
  })
}
