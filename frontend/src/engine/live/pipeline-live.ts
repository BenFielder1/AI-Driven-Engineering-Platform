import type { QueryClient } from '@tanstack/react-query'

import { engineKeys } from '../api/keys'
import type { PipelineLiveEvent, PipelineRun, RunGate, RunStage } from '../api/types'

/** Replaces the item with the same id in a cached list (unchanged when the list isn't cached or lacks the item). */
function replaceById<T extends { id: string }>(list: T[] | undefined, item: T): T[] | undefined {
  if (!list) {
    return list
  }
  return list.some((x) => x.id === item.id) ? list.map((x) => (x.id === item.id ? item : x)) : list
}

function applyRun(queryClient: QueryClient, run: PipelineRun) {
  queryClient.setQueryData<PipelineRun>(engineKeys.runDetail(run.id), run)
  const runs = queryClient.getQueryData<PipelineRun[]>(engineKeys.projectRuns(run.projectId))
  if (runs && !runs.some((r) => r.id === run.id)) {
    // A new run (a new version started): the list is newest first.
    queryClient.setQueryData<PipelineRun[]>(engineKeys.projectRuns(run.projectId), [run, ...runs])
  } else {
    queryClient.setQueryData<PipelineRun[]>(engineKeys.projectRuns(run.projectId), (old) => replaceById(old, run))
  }
}

function applyStage(queryClient: QueryClient, stage: RunStage) {
  queryClient.setQueryData<RunStage[]>(engineKeys.stages(stage.runId), (old) =>
    old?.map((s) => (s.id === stage.id ? stage : s)),
  )
}

function applyGate(queryClient: QueryClient, gate: RunGate) {
  queryClient.setQueryData<RunGate[]>(engineKeys.gates(gate.runId), (old) => {
    if (!old) {
      return old
    }
    return old.some((g) => g.id === gate.id) ? old.map((g) => (g.id === gate.id ? gate : g)) : [gate, ...old]
  })
}

function runIdOf(event: PipelineLiveEvent): string {
  switch (event.type) {
    case 'pipelineRunUpdated':
      return event.run.id
    case 'stageUpdated':
      return event.stage.runId
    case 'gateUpdated':
      return event.gate.runId
  }
}

/**
 * FR-302 live messages: `pipelineRunUpdated`, `stageUpdated` and `gateUpdated` each carry the DTO, which goes straight
 * into the cache; then the run's timeline, replay and artifacts refetch (a change of stage, gate or run always adds
 * timeline entries, and may add artifact versions). The host's hub connection calls this for its project's messages.
 */
export async function applyPipelineLiveEvent(queryClient: QueryClient, event: PipelineLiveEvent): Promise<void> {
  if (event.type === 'pipelineRunUpdated') {
    applyRun(queryClient, event.run)
  } else if (event.type === 'stageUpdated') {
    applyStage(queryClient, event.stage)
  } else {
    applyGate(queryClient, event.gate)
  }
  const runId = runIdOf(event)
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: engineKeys.timeline(runId) }, { cancelRefetch: false }),
    queryClient.invalidateQueries({ queryKey: engineKeys.replay(runId) }, { cancelRefetch: false }),
    queryClient.invalidateQueries({ queryKey: engineKeys.artifacts(runId) }, { cancelRefetch: false }),
  ])
}

/**
 * After (re)joining the live group, messages sent while disconnected are lost: refetch every engine view. Definitions
 * are immutable per hash, so they're left alone.
 */
export function invalidateEngineQueries(queryClient: QueryClient): Promise<void> {
  return queryClient.invalidateQueries({
    queryKey: engineKeys.all,
    predicate: (query) => query.queryKey[1] !== 'definition',
  })
}
