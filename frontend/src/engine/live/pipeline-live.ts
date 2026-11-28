import type { QueryClient } from '@tanstack/react-query'

import { engineKeys } from '../api/keys'
import type { MapProgress, MapProgressUpdate, PipelineLiveEvent, PipelineRun, RunGate, RunStage } from '../api/types'

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

/**
 * Whether `next` may replace the cached `current` progress: always for another map execution or a changed total; for
 * the same one, only when it doesn't count fewer ended items (a stale message arriving late never undoes a newer one).
 */
export function isNewerMapProgress(current: MapProgress | null | undefined, next: MapProgress): boolean {
  if (current?.executionId !== next.executionId || current.total !== next.total) {
    return true
  }
  return next.completed + next.failed >= current.completed + current.failed
}

/**
 * FR-363: a map's progress goes into its stage list entry (the stage's newest map execution), and the open item list of
 * that execution refetches. Ignored when the stage list isn't cached, and monotonic per map execution (see
 * `isNewerMapProgress`). Returns whether it was applied.
 */
function applyMapProgress(queryClient: QueryClient, update: MapProgressUpdate): boolean {
  let applied = false
  queryClient.setQueryData<RunStage[]>(engineKeys.stages(update.runId), (old) =>
    old?.map((s) => {
      if (s.id !== update.stageId || !isNewerMapProgress(s.map, update.progress)) {
        return s
      }
      applied = true
      return { ...s, map: update.progress }
    }),
  )
  return applied
}

function runIdOf(event: PipelineLiveEvent): string {
  switch (event.type) {
    case 'pipelineRunUpdated':
      return event.run.id
    case 'stageUpdated':
      return event.stage.runId
    case 'gateUpdated':
      return event.gate.runId
    case 'stageBranchUpdated':
    case 'mapProgress':
      return event.update.runId
  }
}

/** The project a live message belongs to; `null` for `stageUpdated`, whose DTO doesn't name it (the group does). */
export function projectIdOf(event: PipelineLiveEvent): string | null {
  switch (event.type) {
    case 'pipelineRunUpdated':
      return event.run.projectId
    case 'gateUpdated':
      return event.gate.projectId
    case 'stageBranchUpdated':
    case 'mapProgress':
      return event.update.projectId
    case 'stageUpdated':
      return null
  }
}

function invalidate(queryClient: QueryClient, queryKey: readonly unknown[]) {
  return queryClient.invalidateQueries({ queryKey }, { cancelRefetch: false })
}

/**
 * FR-302 live messages: `pipelineRunUpdated`, `stageUpdated` and `gateUpdated` each carry the DTO, which goes straight
 * into the cache; then the run's timeline, replay and artifacts refetch (a change of stage, gate or run always adds
 * timeline entries, and may add artifact versions). FR-363: `stageBranchUpdated` refetches the timeline the lanes read
 * (and the replay and artifacts, as a branch's stage may have written one); `mapProgress` patches the stage's map
 * progress (never back to fewer ended items: a stale message is ignored) and refetches that map's item list, and once every item has ended, the artifacts and timeline. The host's hub
 * connection calls this for its project's messages.
 */
export async function applyPipelineLiveEvent(queryClient: QueryClient, event: PipelineLiveEvent): Promise<void> {
  const runId = runIdOf(event)
  if (event.type === 'mapProgress') {
    if (!applyMapProgress(queryClient, event.update) && queryClient.getQueryData(engineKeys.stages(runId))) {
      // A stale message for a map the cache already shows further on: nothing to refresh.
      return
    }
    const { executionId, total, completed, failed } = event.update.progress
    const refetches = [invalidate(queryClient, engineKeys.mapItemsAll(runId, executionId))]
    if (total > 0 && completed + failed >= total) {
      refetches.push(invalidate(queryClient, engineKeys.timeline(runId)), invalidate(queryClient, engineKeys.artifacts(runId)))
    }
    await Promise.all(refetches)
    return
  }
  if (event.type === 'pipelineRunUpdated') {
    applyRun(queryClient, event.run)
  } else if (event.type === 'stageUpdated') {
    applyStage(queryClient, event.stage)
  } else if (event.type === 'gateUpdated') {
    applyGate(queryClient, event.gate)
  }
  await Promise.all([
    invalidate(queryClient, engineKeys.timeline(runId)),
    invalidate(queryClient, engineKeys.replay(runId)),
    invalidate(queryClient, engineKeys.artifacts(runId)),
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
