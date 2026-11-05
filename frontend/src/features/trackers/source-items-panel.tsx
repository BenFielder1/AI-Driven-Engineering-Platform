import { useState } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Badge, type BadgeTone } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage, isApiError, trackerCopy } from '../../lib/errors'
import { Link } from '../../lib/navigation'
import { PlatformLogo } from '../connections/platform-logos'
import { useProjectAccess } from '../projects/project-context'
import { RelativeTime } from '../projects/relative-time'
import { StageSection } from '../stages/stage-section'
import { ExternalItemLink } from './external-item-link'
import { TrackerBrowseDialog } from './tracker-browse-dialog'
import { MAX_SOURCE_ITEMS, toSourceImportRequest, trackerNames } from './tracker-picks'
import {
  useAddSource,
  useProjectSources,
  useReimportSource,
  useRemoveSource,
  useSourcesCheck,
  type SourceImport,
  type SourceItemDrift,
} from './trackers-api'

const driftLabels: Readonly<Record<SourceItemDrift, { label: string; tone: BadgeTone }>> = {
  unchanged: { label: 'Unchanged', tone: 'neutral' },
  changed: { label: 'Changed', tone: 'warning' },
  removed: { label: 'Removed', tone: 'danger' },
  unknown: { label: 'Unknown', tone: 'neutral' },
}

const statusBadges: Readonly<Record<SourceImport['status'], { label: string; tone: BadgeTone }>> = {
  queued: { label: 'Queued', tone: 'info' },
  running: { label: 'Importing', tone: 'info' },
  completed: { label: 'Imported', tone: 'success' },
  failed: { label: 'Failed', tone: 'danger' },
  superseded: { label: 'Superseded', tone: 'neutral' },
  removed: { label: 'Removed', tone: 'neutral' },
}

const isPending = (i: Pick<SourceImport, 'status'>) => i.status === 'queued' || i.status === 'running'

/** FR-173: a queued or running import repeating this one's query, so a second Retry or Re-import would only duplicate it. */
const hasRepeatPending = (item: SourceImport, all: readonly SourceImport[]) =>
  all.some(
    (other) =>
      other.id !== item.id &&
      isPending(other) &&
      other.provider === item.provider &&
      JSON.stringify(other.selection) === JSON.stringify(item.selection),
  )

const itemsCopy = (n: number) => (n === 1 ? '1 item' : `${String(n)} items`)

/** §6: a failed import's copy. The job's message is composed by the server, and names the size and limit when too large. */
function failureCopy(i: SourceImport): string {
  if (i.errorMessage) {
    return i.errorMessage
  }
  return i.errorCode === 'SOURCE_ITEMS_TOO_LARGE'
    ? trackerCopy('SOURCE_ITEMS_TOO_LARGE')
    : trackerCopy('TRACKER_IMPORT_FAILED', trackerNames[i.provider])
}

function ImportRow({
  item,
  drift,
  controls,
  retrying,
  repeated,
  onReimport,
  onRemove,
}: {
  item: SourceImport
  drift: ReadonlyMap<string, SourceItemDrift>
  controls: boolean
  retrying: boolean
  /** A repeat of this import is queued, running, or was just submitted: another would be a duplicate. */
  repeated: boolean
  onReimport: () => void
  onRemove: () => void
}) {
  const name = trackerNames[item.provider]
  const pending = isPending(item)
  const badge = statusBadges[item.status]
  const failed = item.status === 'failed'
  return (
    <li aria-label={`${name} import, ${badge.label}`} className="flex flex-col gap-2 p-3" data-testid="source-import">
      <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
        <div className="flex min-w-0 flex-col gap-1 text-sm">
          <span className="flex flex-wrap items-center gap-2 font-medium">
            <PlatformLogo provider={item.provider} />
            {name}
            <Badge tone={badge.tone}>{badge.label}</Badge>
            {item.status === 'completed' && <span className="font-normal text-muted-foreground">{itemsCopy(item.itemCount)}</span>}
          </span>
          <span className="text-muted-foreground">
            By {item.createdBy.name} · <RelativeTime iso={item.completedAt ?? item.createdAt} />
          </span>
        </div>
        {controls && !pending && (
          <div className="flex flex-wrap gap-2">
            {repeated ? (
              <p className="self-center text-sm text-muted-foreground">{failed ? 'Retry queued.' : 'Re-import queued.'}</p>
            ) : (
              <Button variant="secondary" pending={retrying} pendingLabel="Queuing…" onClick={onReimport} aria-label={`${failed ? 'Retry' : 'Re-import'} ${name} import`}>
                {failed ? 'Retry' : 'Re-import'}
              </Button>
            )}
            <Button variant="danger" onClick={onRemove} aria-label={`Remove ${name} import`}>
              Remove
            </Button>
          </div>
        )}
      </div>
      {pending &&
        (item.errorCode === 'TRACKER_RATE_LIMITED' ? (
          <Alert tone="warning" role="status">
            <p>{trackerCopy('TRACKER_RATE_LIMITED', name)}</p>
          </Alert>
        ) : (
          <p role="status" className="text-sm text-muted-foreground">
            {item.status === 'queued' ? 'Waiting to start.' : 'Reading the items.'} A refinement round started now waits for it.
          </p>
        ))}
      {failed && (
        <Alert tone="error" role="alert" title="This import failed">
          <p className="break-words">{failureCopy(item)}</p>
          <p className="mt-1">Nothing from it reached the snapshot, and the rest of the project carries on.</p>
        </Alert>
      )}
      {item.items.length > 0 && (
        <details open={item.items.length <= 10}>
          <summary className="cursor-pointer text-sm font-medium">{`Items (${String(item.items.length)})`}</summary>
          <ul aria-label={`Items of this ${name} import`} className="mt-2 flex flex-col gap-1">
            {item.items.map((i) => {
              const d = drift.get(`${item.id}:${i.key}`)
              return (
                <li key={i.key} className="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm" data-testid="source-item">
                  <ExternalItemLink url={i.url} itemKey={i.key} tracker={name} />
                  <span className="min-w-0 break-words">{i.title}</span>
                  {d && <Badge tone={driftLabels[d].tone}>{driftLabels[d].label}</Badge>}
                </li>
              )
            })}
          </ul>
        </details>
      )}
    </li>
  )
}

/**
 * Refinement's Source items panel (FR-171, FR-173): the version's imports with status, the rate-limit wait, failure and
 * Retry, per-item drift from a check on opening the page, Add items, Re-import, Remove, and "Sources changed since this
 * draft". The snapshot's rendered text is on the artifact page, through the sanitised Markdown component.
 */
export function SourceItemsPanel() {
  const { project, can, readOnly, releaseVersionId } = useProjectAccess()
  const sources = useProjectSources(project.id, releaseVersionId)
  const add = useAddSource(project.id)
  const reimport = useReimportSource(project.id)
  const remove = useRemoveSource(project.id)
  const [adding, setAdding] = useState(false)
  const [addError, setAddError] = useState<string | null>(null)
  const [toRemove, setToRemove] = useState<SourceImport | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  /** Imports repeated from this page, so their Retry stays gone until the list shows the new import. */
  const [resubmitted, setResubmitted] = useState<ReadonlySet<string>>(new Set())

  const shown = sources.data?.imports.filter((i) => i.status !== 'superseded' && i.status !== 'removed') ?? []
  const completed = shown.filter((i) => i.status === 'completed')
  // The check covers the current version only, so a past version shows its imports without drift.
  const fingerprint = `${sources.data?.currentSnapshot?.contentHash ?? ''}:${completed.map((i) => i.id).join(',')}`
  const check = useSourcesCheck(project.id, fingerprint, !readOnly && completed.length > 0)
  const drift = new Map((check.data?.items ?? []).map((d) => [`${d.importId}:${d.key}`, d.drift]))

  const writer = can('startAgents')
  const canChange = (sources.data?.canChange ?? false) && !readOnly
  const controls = writer && canChange
  const used = completed.reduce((n, i) => n + i.itemCount, 0)

  const onReimport = (item: SourceImport) => {
    setActionError(null)
    reimport.mutate(item.id, {
      onSuccess: () => {
        setResubmitted((ids) => new Set(ids).add(item.id))
        toast.success(`${item.status === 'failed' ? 'Retry' : 'Re-import'} queued.`)
      },
      onError: (error) => {
        // A duplicate refused because a repeat is already running (another tab or person): the refreshed list shows it.
        if (isApiError(error, 'CONFLICT') || isApiError(error, 'INVALID_STATE')) {
          setResubmitted((ids) => new Set(ids).add(item.id))
          return
        }
        setActionError(errorMessage(error, 'sourceImport'))
      },
    })
  }

  const confirmRemove = () => {
    if (!toRemove) {
      return
    }
    setActionError(null)
    remove.mutate(toRemove.id, {
      onSuccess: () => {
        setToRemove(null)
        toast.success('Import removed.')
      },
      onError: (error) => {
        setToRemove(null)
        setActionError(errorMessage(error, 'sourceImport'))
      },
    })
  }

  let body
  if (sources.isPending) {
    body = <LoadingState label="Loading the source items…" />
  } else if (sources.isError) {
    body = <ErrorState error={sources.error} title="The source items couldn't load" onRetry={() => void sources.refetch()} />
  } else {
    const { currentSnapshot, draft } = sources.data
    body = (
      <>
        {draft?.behindSnapshot && (
          <Alert tone="warning" title="Sources changed since this draft: run another round">
            <p>
              Draft v{draft.version} was built from{' '}
              {draft.builtFromSnapshotVersion === null ? 'no snapshot' : `snapshot v${String(draft.builtFromSnapshotVersion)}`}. Nothing
              changes by itself: another round uses the current snapshot.
            </p>
          </Alert>
        )}
        {actionError && (
          <Alert tone="error" role="alert" title="That didn't work">
            <p>{actionError}</p>
          </Alert>
        )}
        {currentSnapshot && (
          <p className="text-sm text-muted-foreground">
            Agents read snapshot v{currentSnapshot.version}, taken <RelativeTime iso={currentSnapshot.createdAt} />.{' '}
            <Link
              to="/projects/$projectId/artifacts/$type"
              params={{ projectId: project.id, type: 'source_items' }}
              search={{ version: currentSnapshot.version }}
              className="text-primary underline"
            >
              Read the snapshot
            </Link>
          </p>
        )}
        {shown.length === 0 ? (
          <p className="text-sm text-muted-foreground">No tracker items were imported for this version.</p>
        ) : (
          <ul aria-label="Imports" className="divide-y divide-border/60 rounded-lg border border-border/60">
            {shown.map((i) => (
              <ImportRow
                key={i.id}
                item={i}
                drift={drift}
                controls={controls}
                retrying={reimport.isPending && reimport.variables === i.id}
                repeated={resubmitted.has(i.id) || hasRepeatPending(i, shown)}
                onReimport={() => {
                  onReimport(i)
                }}
                onRemove={() => {
                  setToRemove(i)
                }}
              />
            ))}
          </ul>
        )}
        {check.isError && (
          <p className="text-sm text-muted-foreground">Tevrin couldn&apos;t check the items against the tracker just now. The snapshot is unaffected.</p>
        )}
        {!readOnly && !canChange && (
          <p className="text-sm text-muted-foreground">
            Source items can&apos;t change in this phase. New work belongs in a new version, or after a rollback.
          </p>
        )}
        {canChange && !writer && <p className="text-sm text-muted-foreground">Only Contributors and Owners can change the source items.</p>}
      </>
    )
  }

  return (
    <StageSection
      id="source-items"
      title="Source items"
      actions={
        controls ? (
          <Button
            variant="secondary"
            onClick={() => {
              setAddError(null)
              setAdding(true)
            }}
          >
            Add items
          </Button>
        ) : undefined
      }
    >
      {body}
      {adding && (
        <TrackerBrowseDialog
          capacity={Math.max(0, MAX_SOURCE_ITEMS - used)}
          pending={add.isPending}
          error={addError}
          onClose={() => {
            setAdding(false)
          }}
          onAdd={(pick) => {
            setAddError(null)
            add.mutate(toSourceImportRequest(pick), {
              onSuccess: () => {
                setAdding(false)
                toast.success('Import queued. The items appear here when it finishes.')
              },
              onError: (error) => {
                setAddError(errorMessage(error, 'sourceImport'))
              },
            })
          }}
        />
      )}
      <ConfirmDialog
        open={toRemove !== null}
        title="Remove this import?"
        confirmLabel="Remove import"
        pendingLabel="Removing…"
        pending={remove.isPending}
        destructive
        onConfirm={confirmRemove}
        onCancel={() => {
          setToRemove(null)
        }}
      >
        <p>
          {toRemove?.status === 'completed'
            ? `Tevrin writes a new snapshot without these ${itemsCopy(toRemove.itemCount)}. Earlier locked snapshots are kept.`
            : 'This import never reached a snapshot. Earlier locked snapshots are kept.'}
        </p>
      </ConfirmDialog>
    </StageSection>
  )
}
