import { useId, useState } from 'react'

import { Badge } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { SelectField } from '../../components/ui/select-field'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { useMapItems } from '../api/hooks'
import type { MapItemStatus, MapProgress } from '../api/types'
import { formatDateTime, formatNumber } from '../format'
import { agentRunStatusLabels, itemLabel, mapItemStatusLabels, mapItemStatusTones } from '../labels'

const statuses: MapItemStatus[] = ['open', 'completed', 'failed']

/** "150 of 200 done, 3 failed" (done counts completed and failed items; the rest are open). */
export function mapProgressText(progress: MapProgress): string {
  const done = progress.completed + progress.failed
  return `${formatNumber(done)} of ${formatNumber(progress.total)} done, ${formatNumber(progress.failed)} failed`
}

/** FR-363: the paged item list of a map execution, filtered by status, "Load more" following the cursor. */
function MapItemList({ runId, executionId, title }: { runId: string; executionId: string; title: string }) {
  const [status, setStatus] = useState<MapItemStatus | null>(null)
  const items = useMapItems(runId, executionId, status)
  const rows = items.data?.pages.flatMap((p) => p.items) ?? []

  let body
  if (items.isPending) {
    body = <LoadingState label={`Loading the items of ${title}…`} />
  } else if (items.isError) {
    body = <ErrorState error={items.error} onRetry={() => void items.refetch()} title={`The items of ${title} couldn't load`} />
  } else if (rows.length === 0) {
    body = <EmptyState title={status ? `No ${mapItemStatusLabels[status].toLowerCase()} items` : 'No items'} />
  } else {
    body = (
      <>
        <ul aria-label={`Items of ${title}`} className="flex flex-col divide-y divide-border/60 text-sm">
          {rows.map((item) => (
            <li key={item.executionId} className="flex flex-col gap-1 py-2">
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-medium">{itemLabel(item.itemIndex)}</span>
                <Badge tone={mapItemStatusTones[item.status]}>{mapItemStatusLabels[item.status]}</Badge>
                {item.agentRunStatus && <span className="text-muted-foreground">Agent run {agentRunStatusLabels[item.agentRunStatus].toLowerCase()}</span>}
                <time dateTime={item.enteredAt} className="text-muted-foreground">
                  {formatDateTime(item.enteredAt)}
                </time>
                {item.artifactVersion != null && <span className="font-mono text-xs text-muted-foreground">v{item.artifactVersion}</span>}
              </div>
              {item.error && <p className="whitespace-pre-wrap break-words text-destructive">{item.error}</p>}
            </li>
          ))}
        </ul>
        {items.hasNextPage && (
          <Button
            variant="secondary"
            className="self-start"
            pending={items.isFetchingNextPage}
            pendingLabel="Loading more…"
            onClick={() => void items.fetchNextPage()}
          >
            Load more items
          </Button>
        )}
      </>
    )
  }

  return (
    <div className="flex flex-col gap-3">
      <SelectField
        label="Show items"
        className="max-w-xs"
        value={status ?? ''}
        onChange={(e) => {
          setStatus(e.target.value === '' ? null : (e.target.value as MapItemStatus))
        }}
      >
        <option value="">All</option>
        {statuses.map((s) => (
          <option key={s} value={s}>
            {mapItemStatusLabels[s]}
          </option>
        ))}
      </SelectField>
      {body}
    </div>
  )
}

/**
 * FR-355, FR-363: a map stage's progress (done, failed, total) as a bar, with a drill-down into its paged item list.
 * The bar is decorative over the text, which always carries the numbers.
 */
export function MapProgressView({ runId, progress, title }: { runId: string; progress: MapProgress; title: string }) {
  const [open, setOpen] = useState(false)
  const listId = useId()
  const total = Math.max(progress.total, 0)
  const pct = (n: number) => (total === 0 ? 0 : (n / total) * 100)
  const text = mapProgressText(progress)
  return (
    <div className="flex flex-col gap-2" data-testid="map-progress">
      <div
        role="progressbar"
        aria-label={`Items of ${title}`}
        aria-valuemin={0}
        aria-valuemax={total}
        aria-valuenow={progress.completed + progress.failed}
        aria-valuetext={text}
        className="flex h-3 w-full overflow-hidden rounded-full bg-muted"
      >
        <span className="h-full bg-success" style={{ width: `${String(pct(progress.completed))}%` }} />
        <span className="h-full bg-destructive" style={{ width: `${String(pct(progress.failed))}%` }} />
      </div>
      <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
        <p className="tabular-nums">
          <span>Done {formatNumber(progress.completed)}</span> · <span>Failed {formatNumber(progress.failed)}</span> ·{' '}
          <span>Total {formatNumber(progress.total)}</span>
        </p>
        <Button
          variant="secondary"
          aria-expanded={open}
          aria-controls={listId}
          onClick={() => {
            setOpen((o) => !o)
          }}
        >
          {open ? 'Hide items' : 'Show items'}
        </Button>
      </div>
      <div id={listId} hidden={!open}>
        {open && <MapItemList runId={runId} executionId={progress.executionId} title={title} />}
      </div>
    </div>
  )
}
