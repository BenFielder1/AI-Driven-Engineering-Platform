import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'

import type { PipelineLiveEvent } from '../api/types'
import { applyPipelineLiveEvent, invalidateEngineQueries } from './pipeline-live'

/** What the host's hub connection reports for a project: a pipeline message, or a (re)join after missed messages. */
export type PipelineLiveSignal = { kind: 'event'; event: PipelineLiveEvent } | { kind: 'resync' }

type Listener = (signal: PipelineLiveSignal) => void

/**
 * The engine's side of the live connection (FR-302). The engine doesn't own the hub: the host publishes its project
 * group's `pipelineRunUpdated`, `stageUpdated` and `gateUpdated` messages here, and engine views subscribe while they
 * are mounted, so pages that show no pipeline do no pipeline work.
 */
export function createPipelineLiveBus() {
  const listeners = new Map<string, Set<Listener>>()
  const emit = (projectId: string, signal: PipelineLiveSignal) => {
    for (const listener of [...(listeners.get(projectId) ?? [])]) {
      listener(signal)
    }
  }
  return {
    publish(projectId: string, event: PipelineLiveEvent): void {
      emit(projectId, { kind: 'event', event })
    },
    resync(projectId: string): void {
      emit(projectId, { kind: 'resync' })
    },
    subscribe(projectId: string, listener: Listener): () => void {
      const set = listeners.get(projectId) ?? new Set<Listener>()
      set.add(listener)
      listeners.set(projectId, set)
      return () => {
        set.delete(listener)
        if (set.size === 0) {
          listeners.delete(projectId)
        }
      }
    },
  }
}

export type PipelineLiveBus = ReturnType<typeof createPipelineLiveBus>

/** The app-wide bus the host publishes to. */
export const pipelineLiveBus: PipelineLiveBus = createPipelineLiveBus()

/**
 * FR-302: while an engine view of `projectId` is mounted, apply its live pipeline messages to the cache (and refetch
 * every engine view after a resync). `onEvent` sees each message after it is applied.
 */
export function usePipelineLive(
  projectId: string,
  onEvent?: (event: PipelineLiveEvent) => void,
  bus: PipelineLiveBus = pipelineLiveBus,
): void {
  const queryClient = useQueryClient()
  const onEventRef = useRef(onEvent)
  useEffect(() => {
    onEventRef.current = onEvent
  }, [onEvent])
  useEffect(
    () =>
      bus.subscribe(projectId, (signal) => {
        if (signal.kind === 'resync') {
          void invalidateEngineQueries(queryClient)
          return
        }
        void applyPipelineLiveEvent(queryClient, signal.event)
        onEventRef.current?.(signal.event)
      }),
    [bus, projectId, queryClient],
  )
}
