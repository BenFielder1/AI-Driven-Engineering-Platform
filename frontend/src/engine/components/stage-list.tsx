import { Badge } from '../../components/ui/badge'
import type { PipelineDefinition, RunStage } from '../api/types'
import { StageConstructs } from '../constructs/stage-constructs'
import { formatDateTime, formatDuration } from '../format'
import { definitionLabels, stageStateLabels, stageStateTones } from '../labels'

const head = 'px-3 py-2 font-medium'
const cell = 'px-3 py-2'

function Time({ iso }: { iso: string | null }) {
  return iso ? <time dateTime={iso}>{formatDateTime(iso)}</time> : <>—</>
}

/**
 * FR-300, FR-306: the run's stages in definition order with their state, how often each was entered, and the latest
 * entry's timings. Titles come from the definition (FR-305). After a rollback or loop, the stages after the current one
 * read Pending again (D-0.7.0-35) while their earlier entries still count. The table scrolls inside its card on narrow
 * screens. FR-363: stages with a construct (lanes, a map, variants, a conversation) show it under the table.
 */
export function StageList({ stages, definition }: { stages: readonly RunStage[]; definition: PipelineDefinition | undefined }) {
  const labels = definitionLabels(definition)
  return (
    <>
      <div className="overflow-x-auto" tabIndex={0} role="region" aria-label="Stages (scrolls sideways)">
        <table className="w-full min-w-[40rem] border-collapse text-sm">
          <caption className="sr-only">Stages of the run, in pipeline order</caption>
          <thead>
            <tr className="border-b border-border/60 text-left text-xs text-muted-foreground">
              <th scope="col" className={head}>
                Stage
              </th>
              <th scope="col" className={head}>
                State
              </th>
              <th scope="col" className={`${head} text-right`}>
                Entries
              </th>
              <th scope="col" className={head}>
                Entered
              </th>
              <th scope="col" className={head}>
                Exited
              </th>
              <th scope="col" className={`${head} text-right`}>
                Duration
              </th>
            </tr>
          </thead>
          <tbody>
            {stages.map((stage) => (
              <tr
                key={stage.id}
                data-state={stage.state}
                aria-current={stage.state === 'active' ? 'step' : undefined}
                className="border-b border-border last:border-b-0"
              >
                <th scope="row" className={`${cell} text-left font-medium`}>
                  {stage.title || labels.stage(stage.id)}
                </th>
                <td className={cell}>
                  <Badge tone={stageStateTones[stage.state]}>{stageStateLabels[stage.state]}</Badge>
                </td>
                <td className={`${cell} text-right tabular-nums`}>{stage.iterations}</td>
                <td className={`${cell} whitespace-nowrap`}>
                  <Time iso={stage.enteredAt} />
                </td>
                <td className={`${cell} whitespace-nowrap`}>
                  <Time iso={stage.exitedAt} />
                </td>
                <td className={`${cell} text-right tabular-nums`}>
                  {stage.durationMs === null ? '—' : formatDuration(stage.durationMs)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <StageConstructs stages={stages} definition={definition} />
    </>
  )
}
