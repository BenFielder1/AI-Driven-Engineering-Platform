import { Button } from '../../components/ui/button'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { useRunTimeline } from '../api/hooks'
import type { TimelineItem } from '../api/types'
import { formatDateTime } from '../format'
import { timelineKindLabels, type DefinitionLabels } from '../labels'
import { describeEntry } from '../timeline-text'
import { useNewEntriesAnnouncement } from './use-new-entries'

/** One entry: its kind, time, a one-line title, the facts and any reason, comment or error. */
export function TimelineEntry({ item, labels }: { item: TimelineItem; labels: DefinitionLabels }) {
  const text = describeEntry(item, labels)
  return (
    <div className="min-w-0" data-kind={item.kind}>
      <p className="flex flex-wrap items-baseline gap-x-2 text-xs text-muted-foreground">
        <span className="font-medium uppercase tracking-wide">{timelineKindLabels[item.kind]}</span>
        <time dateTime={item.at}>{formatDateTime(item.at)}</time>
      </p>
      <p className="mt-0.5 break-words font-medium">{text.title}</p>
      {text.facts.length > 0 && <p className="break-words text-sm text-muted-foreground">{text.facts.join(' · ')}</p>}
      {text.note && <p className="mt-1 whitespace-pre-wrap break-words text-sm">{text.note}</p>}
    </div>
  )
}

/**
 * FR-300, FR-306: the run's timeline in time order (transitions, stage entries, agent runs, gate events and artifact
 * versions), 50 at a time with "Load more" following the cursor. Live messages refetch it, and entries they add are
 * announced in a polite live region (SPEC §7).
 */
export function RunTimeline({ runId, labels }: { runId: string; labels: DefinitionLabels }) {
  const timeline = useRunTimeline(runId)
  const items = timeline.data?.pages.flatMap((page) => page.items) ?? []
  const { announcement, paging } = useNewEntriesAnnouncement(items.length, timeline.isSuccess)
  const liveRegion = (
    <p role="status" aria-live="polite" aria-atomic="true" className="sr-only" data-testid="timeline-live-region">
      {announcement}
    </p>
  )

  if (timeline.isPending) {
    return <LoadingState label="Loading the timeline…" />
  }
  if (timeline.isError) {
    return <ErrorState error={timeline.error} onRetry={() => void timeline.refetch()} title="The timeline couldn't load" />
  }
  if (items.length === 0) {
    return (
      <>
        {liveRegion}
        <EmptyState title="Nothing has happened in this run yet" />
      </>
    )
  }
  return (
    <div className="flex flex-col gap-3">
      {liveRegion}
      <ol className="flex flex-col" aria-label="Timeline, oldest first">
        {items.map((item) => (
          <li key={`${item.kind}-${item.id}`} className="border-l-2 border-border py-2 pl-4">
            <TimelineEntry item={item} labels={labels} />
          </li>
        ))}
      </ol>
      {timeline.hasNextPage && (
        <Button
          variant="secondary"
          className="self-start"
          onClick={() => {
            paging(() => timeline.fetchNextPage())
          }}
          pending={timeline.isFetchingNextPage}
          pendingLabel="Loading more…"
        >
          Load more
        </Button>
      )}
    </div>
  )
}
