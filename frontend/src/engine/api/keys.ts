/**
 * Query keys of the engine UI, all under `['engine']` so a host can refresh every engine view at once (after a live
 * reconnect, `invalidateEngineQueries`). Run-scoped keys sit under `run(runId)`, so a live message for a run refreshes
 * its timeline, artifacts and gates together.
 */
export const engineKeys = {
  all: ['engine'] as const,
  pipelines: () => [...engineKeys.all, 'pipelines'] as const,
  definition: (pipelineId: string, hash: string) => [...engineKeys.all, 'definition', pipelineId, hash] as const,
  promptStats: (pipelineId: string, projectId: string | null) =>
    [...engineKeys.all, 'prompt-stats', pipelineId, projectId ?? 'all'] as const,
  projectRuns: (projectId: string) => [...engineKeys.all, 'project', projectId, 'runs'] as const,
  run: (runId: string) => [...engineKeys.all, 'run', runId] as const,
  runDetail: (runId: string) => [...engineKeys.run(runId), 'detail'] as const,
  stages: (runId: string) => [...engineKeys.run(runId), 'stages'] as const,
  timeline: (runId: string) => [...engineKeys.run(runId), 'timeline'] as const,
  replay: (runId: string) => [...engineKeys.run(runId), 'replay'] as const,
  artifacts: (runId: string) => [...engineKeys.run(runId), 'artifacts'] as const,
  artifactVersion: (runId: string, key: string, version: number) =>
    [...engineKeys.artifacts(runId), 'version', key, version] as const,
  artifactDiff: (runId: string, key: string, from: number, to: number) =>
    [...engineKeys.artifacts(runId), 'diff', key, from, to] as const,
  gates: (runId: string) => [...engineKeys.run(runId), 'gates'] as const,
  /** FR-363: a map execution's item lists (every status filter), refreshed together by a live `mapProgress`. */
  mapItemsAll: (runId: string, executionId: string) => [...engineKeys.run(runId), 'map-items', executionId] as const,
  /** FR-363: a map execution's items, per status filter. */
  mapItems: (runId: string, executionId: string, status: string | null) =>
    [...engineKeys.mapItemsAll(runId, executionId), status ?? 'all'] as const,
  /** FR-307: the gate inbox, per filter (all filters sit under `gateInboxAll`, so a decision refreshes every one). */
  gateInboxAll: () => [...engineKeys.all, 'gate-inbox'] as const,
  gateInbox: (pipelineId: string | null) => [...engineKeys.gateInboxAll(), pipelineId ?? 'all'] as const,
}
