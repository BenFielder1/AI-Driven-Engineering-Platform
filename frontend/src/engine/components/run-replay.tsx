import { useState } from 'react'

import { Button } from '../../components/ui/button'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { useRunReplay } from '../api/hooks'
import type { PipelineDefinition } from '../api/types'
import type { DefinitionLabels } from '../labels'
import { describeEntry } from '../timeline-text'
import { ReplayEntry } from './replay-entry'
import { useNewEntriesAnnouncement } from './use-new-entries'

/**
 * FR-303, FR-306 Replay: steps through the run's timeline one entry at a time with Previous / Next and a scrubber (a
 * native range input, so arrow keys, Page Up/Down, Home and End work). Agent entries show their input and output side
 * by side; artifact entries their diff to the previous version. Pages load as the stepper reaches the end. Entries that
 * arrive live are announced in a polite live region. Read-only: nothing is re-run.
 */
export function RunReplay({ runId, definition, labels }: { runId: string; definition: PipelineDefinition | undefined; labels: DefinitionLabels }) {
  const replay = useRunReplay(runId)
  const [index, setIndex] = useState(0)
  const items = replay.data?.pages.flatMap((page) => page.items) ?? []
  const count = items.length
  // Announce entries that arrive without the user paging (live updates refetch the replay).
  const { announcement, paging } = useNewEntriesAnnouncement(count, replay.isSuccess)

  if (replay.isPending) {
    return <LoadingState label="Loading the replay…" />
  }
  if (replay.isError) {
    return <ErrorState error={replay.error} onRetry={() => void replay.refetch()} title="The replay couldn't load" />
  }

  const loadMore = () => {
    paging(() => replay.fetchNextPage())
  }
  const current = Math.min(index, Math.max(0, count - 1))
  const item = items[current]
  const atEnd = current >= count - 1
  const goTo = (next: number) => {
    if (next < 0 || (next >= count && !replay.hasNextPage)) {
      // At an end: the button stays focusable (aria-disabled) and does nothing.
      return
    }
    if (next >= count && replay.hasNextPage) {
      loadMore()
    }
    setIndex(Math.max(0, Math.min(next, replay.hasNextPage ? next : count - 1)))
  }
  /** Previous/Next at an end keep focus (aria-disabled, not disabled) but look disabled. */
  const endClasses = 'aria-disabled:cursor-not-allowed aria-disabled:opacity-60'
  const position = `Entry ${String(current + 1)} of ${String(count)}${replay.hasNextPage ? '+' : ''}`

  return (
    <div className="flex flex-col gap-4">
      <p role="status" aria-live="polite" aria-atomic="true" className="sr-only" data-testid="replay-live-region">
        {announcement}
      </p>
      {count === 0 || !item ? (
        <EmptyState title="Nothing to replay yet">Entries appear here as the run moves through its stages.</EmptyState>
      ) : (
        <>
          <div className="flex flex-col gap-3 rounded-lg border border-border/60 p-3" role="group" aria-label="Replay controls">
            <div className="flex flex-wrap items-center gap-2">
              <Button
                variant="secondary"
                className={endClasses}
                aria-disabled={current === 0}
                onClick={() => {
                  goTo(current - 1)
                }}
              >
                Previous
              </Button>
              <Button
                variant="secondary"
                className={endClasses}
                aria-disabled={atEnd && !replay.hasNextPage}
                aria-busy={(atEnd && replay.isFetchingNextPage) || undefined}
                onClick={() => {
                  goTo(current + 1)
                }}
              >
                Next
              </Button>
              <span className="text-sm tabular-nums text-muted-foreground" aria-hidden="true">
                {position}
              </span>
            </div>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium">Timeline position</span>
              <input
                type="range"
                min={1}
                max={count}
                step={1}
                value={current + 1}
                aria-valuetext={`${position}: ${describeEntry(item, labels).title}`}
                onChange={(e) => {
                  goTo(Number(e.target.value) - 1)
                }}
                className="w-full accent-primary"
              />
            </label>
          </div>
          <ReplayEntry key={`${item.kind}-${item.id}`} runId={runId} item={item} definition={definition} labels={labels} />
        </>
      )}
    </div>
  )
}
