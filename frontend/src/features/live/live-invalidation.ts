import type { QueryClient, QueryKey } from '@tanstack/react-query'

import type { Schemas } from '../../api/client'
import { projectKeys } from '../projects/projects-api'

export type EventCategory = NonNullable<Schemas['EventCategory']>

interface Target {
  queryKey: QueryKey
  exact?: boolean
}

const project = (id: string): Target => ({ queryKey: projectKeys.detail(id), exact: true })
const state = (id: string): Target => ({ queryKey: projectKeys.state(id) })
const gates = (id: string): Target => ({ queryKey: projectKeys.gates(id) })
const activity = (id: string): Target => ({ queryKey: [...projectKeys.detail(id), 'activity'] })
const artifacts = (id: string): Target => ({ queryKey: projectKeys.artifacts(id) })
const runs = (id: string): Target => ({ queryKey: projectKeys.runs(id) })
const refinement = (id: string): Target => ({ queryKey: projectKeys.refinement(id) })
const execution = (id: string): Target => ({ queryKey: projectKeys.execution(id) })
const pullRequest = (id: string): Target => ({ queryKey: projectKeys.pullRequest(id) })
const lists = (): Target => ({ queryKey: projectKeys.lists() })
/** Everything about the project (the detail key prefixes all of its queries). */
const everything = (id: string): Target => ({ queryKey: projectKeys.detail(id) })

/**
 * FR-46: which cached queries a live `projectEvent` makes stale, by category. Exhaustive over the contract's
 * `EventCategory`, so a new category is a type error until it's mapped. The activity feed takes every event.
 */
export const liveInvalidation: Record<EventCategory, (id: string) => Target[]> = {
  // Phase or status change: header, timeline, state and the projects list all change.
  transition: (id) => [everything(id), lists()],
  // Gate created/decided: approving moves the phase (that also arrives as a transition event).
  gate: (id) => [project(id), state(id), gates(id), activity(id)],
  // New version, approval, rejection, invalidation cascade (§4.4) or corruption. A new project request draft ends a
  // refinement round (FR-27); approving a plan creates its execution steps and invalidates older ones (FR-30).
  artifact: (id) => [artifacts(id), state(id), activity(id), refinement(id), execution(id)],
  integrity: (id) => [artifacts(id), activity(id)],
  // Agent runs (started, completed, failed, refused by the budget) change the runs list and the tokens used; the
  // artifacts they produce arrive as artifact events (FR-23, FR-26). A refinement round's status is its run's (FR-27).
  run: (id) => [project(id), state(id), runs(id), activity(id), refinement(id)],
  // STEP_STARTED/COMPLETED/REJECTED/BLOCKED (FR-31–FR-34) and pause/resume (FR-37): steps, counts and the pause flag
  // (on both /state and the project). A blocked step's escalation gate arrives as a gate event. PULL_REQUEST_OPENED/
  // FAILED and GIT_PUSH_FAILED are step events too: /state's pullRequest summary and the PR comparison change (FR-36).
  step: (id) => [project(id), state(id), activity(id), execution(id), pullRequest(id)],
  review: (id) => [state(id), activity(id)],
  release: (id) => [project(id), state(id), activity(id), lists()],
  // A rollback changes version, phase, artifacts and steps (§4.8).
  rollback: (id) => [everything(id), lists()],
  // Settings, members, rename.
  project: (id) => [everything(id), lists()],
}

/** Invalidates what a live event of `category` affects for project `id`. */
export function invalidateForEvent(queryClient: QueryClient, id: string, category: EventCategory) {
  return Promise.all(liveInvalidation[category](id).map((target) => queryClient.invalidateQueries(target)))
}
