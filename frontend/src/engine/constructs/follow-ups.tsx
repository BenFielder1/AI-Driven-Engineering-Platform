import type { RunFollowUp } from '../api/types'
import { formatDateTime, humanise } from '../format'
import { followUpLabel, type DefinitionLabels } from '../labels'

const head = 'px-3 py-2 font-medium'
const cell = 'px-3 py-2'

function Time({ iso }: { iso: string | null }) {
  return iso ? <time dateTime={iso}>{formatDateTime(iso)}</time> : <>—</>
}

/** What a follow-up has done so far, in words. */
function followUpState(f: RunFollowUp): string {
  if (f.exitedAt) {
    return f.outcome ? humanise(f.outcome) : 'Ran'
  }
  if (f.enteredAt) {
    return 'Running'
  }
  return f.dueAt ? 'Scheduled' : 'Not scheduled'
}

/**
 * FR-354, FR-363 follow-ups: the run's follow-up stages in declared order with when each is due (set when the run
 * completes), and once one has run, its times and outcome. Labels come from the definition.
 */
export function FollowUps({ followUps, labels, headingId }: { followUps: readonly RunFollowUp[]; labels: DefinitionLabels; headingId: string }) {
  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-2" data-testid="run-follow-ups">
      <h5 id={headingId} className="text-sm font-semibold">
        Follow-ups
      </h5>
      {followUps.every((f) => f.dueAt === null) && (
        <p className="text-sm text-muted-foreground">Follow-ups are scheduled when the run completes.</p>
      )}
      <div className="overflow-x-auto" tabIndex={0} role="region" aria-label="Follow-ups (scrolls sideways)">
        <table className="w-full min-w-[36rem] border-collapse text-sm">
          <caption className="sr-only">Follow-ups of the run, with their due times</caption>
          <thead>
            <tr className="border-b border-border/60 text-left text-xs text-muted-foreground">
              <th scope="col" className={head}>
                Follow-up
              </th>
              <th scope="col" className={head}>
                Due
              </th>
              <th scope="col" className={head}>
                State
              </th>
              <th scope="col" className={head}>
                Started
              </th>
              <th scope="col" className={head}>
                Ended
              </th>
            </tr>
          </thead>
          <tbody>
            {followUps.map((f) => (
              <tr key={`${f.stage}-${f.label}`} className="border-b border-border last:border-b-0">
                <th scope="row" className={`${cell} text-left font-medium`}>
                  {labels.stage(f.stage)}
                  <span className="block text-xs font-normal text-muted-foreground">{followUpLabel(f.label)}</span>
                </th>
                <td className={`${cell} whitespace-nowrap`}>{f.dueAt ? <Time iso={f.dueAt} /> : 'When the run completes'}</td>
                <td className={cell}>{followUpState(f)}</td>
                <td className={`${cell} whitespace-nowrap`}>
                  <Time iso={f.enteredAt} />
                </td>
                <td className={`${cell} whitespace-nowrap`}>
                  <Time iso={f.exitedAt} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}
