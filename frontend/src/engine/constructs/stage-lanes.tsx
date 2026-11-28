import { Badge, type BadgeTone } from '../../components/ui/badge'
import { useRunTimeline } from '../api/hooks'
import type { StageDefinition, TimelineItem } from '../api/types'
import { humanise } from '../format'
import { branchLabel, type DefinitionLabels } from '../labels'

/** One lane: the branch id the stage executions carry, and the stages that run in it (in order). */
export interface Lane {
  branch: string
  stages: { id: string; title: string }[]
}

/**
 * FR-343, FR-345, FR-363: a stage's lanes from its definition: a parallel group's branches (ids `0`, `1`…, each with
 * its own stages), or a fan-out's branches (one per agent key, or `#1`…`#n` of the stage's agent), each running the stage
 * itself. Empty for other stages.
 */
export function lanesOf(stage: StageDefinition): Lane[] {
  if (stage.branches && stage.branches.length > 0) {
    return stage.branches.map((b) => ({ branch: b.id, stages: b.stages.map((s) => ({ id: s.id, title: s.title })) }))
  }
  if (stage.fanOut) {
    const ids =
      stage.fanOut.agents.length > 0
        ? stage.fanOut.agents
        : Array.from({ length: stage.fanOut.count ?? 0 }, (_, i) => `#${String(i + 1)}`)
    return ids.map((branch) => ({ branch, stages: [{ id: stage.id, title: stage.title }] }))
  }
  return []
}

interface LaneState {
  label: string
  tone: BadgeTone
}

/** The newest entry of `stageId` in `branch`, as a state; `null` when the loaded timeline has none. */
function laneStageState(entries: readonly TimelineItem[], stageId: string, branch: string): LaneState | null {
  const entry = entries.findLast((e) => e.stageId === stageId && e.branch === branch)
  if (!entry) {
    return null
  }
  if (!entry.exitedAt) {
    return { label: 'Active', tone: 'info' }
  }
  if (!entry.outcome) {
    return { label: 'Ended', tone: 'neutral' }
  }
  const failed = /fail/i.test(entry.outcome)
  return { label: humanise(entry.outcome), tone: failed ? 'danger' : 'success' }
}

/**
 * FR-363 lanes: each branch of a parallel group or fan-out as a lane, labelled by branch, with its stages and the state
 * of each from the run's timeline (stage entries carry their `branch`). A stage with no entry in the loaded timeline
 * reads "Not started", or "—" while older timeline pages aren't loaded (it may have run in one).
 */
export function StageLanes({ runId, stage, labels }: { runId: string; stage: StageDefinition; labels: DefinitionLabels }) {
  const lanes = lanesOf(stage)
  const timeline = useRunTimeline(runId)
  const entries = (timeline.data?.pages.flatMap((p) => p.items) ?? []).filter((i) => i.kind === 'stage_entered' && i.branch != null)
  const complete = timeline.isSuccess && !timeline.hasNextPage
  const title = labels.stage(stage.id)
  if (lanes.length === 0) {
    return null
  }
  return (
    <ul aria-label={`Lanes of ${title}`} className="grid gap-3 sm:grid-cols-2" data-testid="stage-lanes">
      {lanes.map((lane) => {
        const name = branchLabel(lane.branch)
        return (
          <li key={lane.branch} aria-label={name} className="flex min-w-0 flex-col gap-2 rounded-lg border border-border/60 p-3">
            <p className="text-sm font-semibold">{name}</p>
            <ol className="flex flex-col gap-1 text-sm" aria-label={`Stages in ${name}`}>
              {lane.stages.map((s) => {
                const state = laneStageState(entries, s.id, lane.branch)
                return (
                  <li key={s.id} className="flex flex-wrap items-center justify-between gap-2">
                    <span className="min-w-0 break-words">{s.title || labels.stage(s.id)}</span>
                    {state ? (
                      <Badge tone={state.tone}>{state.label}</Badge>
                    ) : complete ? (
                      <Badge>Not started</Badge>
                    ) : (
                      <span className="text-muted-foreground">
                        <span aria-hidden="true">—</span>
                        <span className="sr-only">State not loaded</span>
                      </span>
                    )}
                  </li>
                )
              })}
            </ol>
          </li>
        )
      })}
    </ul>
  )
}
