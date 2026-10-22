import type { QueryClient, QueryKey } from '@tanstack/react-query'

import type { Schemas } from '../../api/client'
import { projectKeys } from '../projects/projects-api'

export type EventCategory = NonNullable<Schemas['EventCategory']>

interface Target {
  queryKey: QueryKey
  exact?: boolean
  /**
   * `false` for a safety-net refresh: an in-flight fetch of the same query is reused rather than aborted and
   * restarted, so a burst of gate events doesn't cancel the artifact refetch over and over. Only safe where a fetch
   * already in flight started after the change it guards against (see `artifactsDeduped`). The authoritative
   * ARTIFACT_* events and RUN_COMPLETED keep the default (cancel and refetch).
   */
  cancelRefetch?: false
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
const review = (id: string): Target => ({ queryKey: projectKeys.review(id) })
const lists = (): Target => ({ queryKey: projectKeys.lists() })
/**
 * The artifact queries as a safety net under a gate event (see `Target.cancelRefetch`). A gate is created or decided
 * after the run that drafted its artifact completed, and RUN_COMPLETED already cancelled and restarted the artifact
 * fetch, so an in-flight fetch reused here started after the draft existed.
 */
const artifactsDeduped = (id: string): Target => ({ ...artifacts(id), cancelRefetch: false })

/** Run events after which the run's artifacts exist: a completed run has written its draft (FR-23, FR-26). */
const ARTIFACT_RUN_EVENTS: ReadonlySet<string> = new Set(['RUN_COMPLETED'])
/**
 * Gate events tied to an artifact change: a gate is created for the draft a run just produced, and approving or
 * rejecting it locks or rejects that draft. GATE_OVERDUE and GATE_CANCELLED change no artifact.
 */
const ARTIFACT_GATE_EVENTS: ReadonlySet<string> = new Set(['GATE_CREATED', 'GATE_APPROVED', 'GATE_REJECTED'])

/** Everything about the project (the detail key prefixes all of its queries). */
const everything = (id: string): Target => ({ queryKey: projectKeys.detail(id) })

/**
 * FR-46: which cached queries a live `projectEvent` makes stale, by category. Exhaustive over the contract's
 * `EventCategory`, so a new category is a type error until it's mapped. The activity feed takes every event.
 */
export const liveInvalidation: Record<EventCategory, (id: string, type: string) => Target[]> = {
  // Phase or status change: header, timeline, state and the projects list all change.
  transition: (id) => [everything(id), lists()],
  // Gate created/decided: approving moves the phase (that also arrives as a transition event). A gate is created for
  // a draft the next stage's run just produced, so for GATE_CREATED/APPROVED/REJECTED the artifact queries are
  // refreshed too (deduped): the page must never show "awaiting approval" next to "no plan yet" if the draft's own
  // artifact event was missed.
  gate: (id, type) => [
    project(id),
    state(id),
    gates(id),
    activity(id),
    ...(ARTIFACT_GATE_EVENTS.has(type) ? [artifactsDeduped(id)] : []),
  ],
  // New version, approval, rejection, invalidation cascade (§4.4) or corruption. A new project request draft ends a
  // refinement round (FR-27); approving a plan creates its execution steps and invalidates older ones (FR-30).
  artifact: (id) => [artifacts(id), state(id), activity(id), refinement(id), execution(id)],
  integrity: (id) => [artifacts(id), activity(id)],
  // Agent runs (started, completed, failed, refused by the budget, connection invalid) change the runs list and the
  // tokens used; a refinement round's status is its run's (FR-27). The artifacts a run produces arrive as artifact
  // events (FR-23, FR-26); only RUN_COMPLETED also refreshes the artifact queries, so a missed artifact event can't
  // leave a stage page empty. It cancels and restarts an in-flight artifact fetch: one started before the run wrote its
  // draft would otherwise settle on pre-draft data when the ARTIFACT_* event itself was missed. RUN_STARTED,
  // RUN_FAILED, BUDGET_EXCEEDED and CONNECTION_INVALID create no artifact.
  run: (id, type) => [
    project(id),
    state(id),
    runs(id),
    activity(id),
    refinement(id),
    ...(ARTIFACT_RUN_EVENTS.has(type) ? [artifacts(id)] : []),
  ],
  // STEP_STARTED/COMPLETED/REJECTED/BLOCKED (FR-31–FR-34) and pause/resume (FR-37): steps, counts and the pause flag
  // (on both /state and the project). A blocked step's escalation gate arrives as a gate event. PULL_REQUEST_OPENED/
  // FAILED and GIT_PUSH_FAILED are step events too: /state's pullRequest summary and the PR comparison change (FR-36).
  step: (id) => [project(id), state(id), activity(id), execution(id), pullRequest(id)],
  // REVIEW_STARTED/REVIEW_COMPLETED (FR-38, FR-39): the review status on /state and the compliance report, trend and
  // drift change; a review_required cycle drafts an optimisation plan (FR-40). The decision's gate (pre_release or
  // escalation) arrives as a gate event and a re-entry to execution as a transition event.
  review: (id) => [project(id), state(id), activity(id), review(id), artifacts(id)],
  // RELEASE_STARTED/COMPLETED/FAILED (FR-42, FR-43, FR-64): /state's latestRelease and status, the release attempts,
  // the release versions and the version badges on the header and the list. VERSION_STARTED (FR-60) is a release
  // event too and resets the lifecycle to refinement at checkpoint 1, so everything about the project is refreshed.
  // DEPLOYMENT_CREATED/UPDATED/FAILED/PREVIEW_READY (FR-90, FR-91) are release events: the deployments list, /state's
  // latestDeployment, the release's production deployment and the review preview all live under the project key.
  release: (id) => [everything(id), lists()],
  // A rollback changes version, phase, artifacts and steps (§4.8).
  rollback: (id) => [everything(id), lists()],
  // Settings, members, rename.
  project: (id) => [everything(id), lists()],
}

/**
 * FR-46: after joining a project's group (first join, rejoin after a reconnect, or a failed rejoin), refetch everything
 * about the project: events sent before the join completed never reach this client, and any data fetched or still
 * being fetched before then may predate one of them. That includes the page's own first-load queries when they settled
 * (or are still in flight) before Join resolved, so nothing is filtered by fetch time. A fetch in flight started
 * before the join and can return pre-event data, so it is never the last word: one with data is cancelled and
 * restarted; one without data yet (a first load, which TanStack Query reuses even with `cancelRefetch`) is refetched
 * again once it settles. The cost is at most one extra request per project query on page open.
 */
export async function invalidateAfterJoin(queryClient: QueryClient, id: string) {
  const filters = everything(id)
  const pendingFirstLoads = queryClient
    .getQueryCache()
    .findAll(filters)
    .filter((query) => query.state.fetchStatus !== 'idle' && query.state.data === undefined)
  await queryClient.invalidateQueries(filters)
  await Promise.all(
    pendingFirstLoads.map((query) => queryClient.invalidateQueries({ queryKey: query.queryKey, exact: true })),
  )
}

/** Invalidates what a live event of `category` and `type` affects for project `id`. */
export function invalidateForEvent(queryClient: QueryClient, id: string, category: EventCategory, type: string) {
  return Promise.all(
    liveInvalidation[category](id, type).map(({ cancelRefetch, ...target }) =>
      queryClient.invalidateQueries(target, cancelRefetch === undefined ? undefined : { cancelRefetch }),
    ),
  )
}
