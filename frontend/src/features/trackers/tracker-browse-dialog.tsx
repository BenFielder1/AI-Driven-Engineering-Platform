import { useEffect, useId, useState, type ReactNode } from 'react'

import { EmptyState } from '../../components/shared/empty-state'
import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextAreaField } from '../../components/ui/textarea-field'
import { TextField } from '../../components/ui/text-field'
import { isApiError, trackerBrowseMessage } from '../../lib/errors'
import { Link } from '../../lib/navigation'
import { GitHostLink } from '../git-hosts/git-host-link'
import { ExternalItemLink } from './external-item-link'
import { TrackerReasonNotice } from './tracker-readiness'
import type { PickedImport, TrackerProvider } from './tracker-picks'
import {
  useTrackerContainers,
  useTrackers,
  useTrackerSearch,
  type SourceItemStatusCategory,
  type TrackerCriteria,
  type TrackerFilter,
  type TrackerItem,
  type TrackerStatus,
} from './trackers-api'

const containerLabels: Readonly<Record<TrackerProvider, string>> = {
  jira: 'Project',
  linear: 'Team or project',
  github: 'Repository',
  gitlab: 'Project',
}

/** FR-169: only Jira and GitHub take a native query. */
const nativeQueryLabels: Partial<Record<TrackerProvider, string>> = {
  jira: 'JQL',
  github: 'GitHub search qualifiers',
}

const parentHints: Readonly<Record<TrackerProvider, string>> = {
  jira: 'An epic key, like PROJ-12.',
  linear: 'A parent issue key or project name.',
  github: 'A parent issue, like acme/app#12.',
  gitlab: 'A milestone title.',
}

const statusOptions: readonly { value: SourceItemStatusCategory; label: string }[] = [
  { value: 'todo', label: 'To do' },
  { value: 'in_progress', label: 'In progress' },
  { value: 'done', label: 'Done' },
]

const typeLabels: Readonly<Record<TrackerItem['type'], string>> = {
  epic: 'Epic',
  story: 'Story',
  task: 'Task',
  bug: 'Bug',
  subtask: 'Sub-task',
  other: 'Item',
}

function Checkbox({ label, hint, ...props }: { label: ReactNode; hint?: string } & React.InputHTMLAttributes<HTMLInputElement>) {
  return (
    <label className="flex min-h-6 items-start gap-2 text-sm">
      <input type="checkbox" className="mt-0.5 size-4 shrink-0 accent-primary" {...props} />
      <span className="min-w-0">
        {label}
        {hint && <span className="block text-muted-foreground">{hint}</span>}
      </span>
    </label>
  )
}

function ConnectionsLink({ onOpenConnections }: { onOpenConnections?: () => void }) {
  return (
    <Link to="/connections" onClick={onOpenConnections} className="text-primary underline underline-offset-2">
      Go to Connections
    </Link>
  )
}

/** A failed browse call, with its way out: the App settings link, or Connections. */
function BrowseError({ error, tracker, onOpenConnections }: { error: unknown; tracker: string; onOpenConnections?: () => void }) {
  return (
    <Alert tone="error" role="alert" title="That didn't work">
      <p className="break-words">{trackerBrowseMessage(error, tracker)}</p>
      {isApiError(error, 'TRACKER_PERMISSION_MISSING') && error.installUrl && (
        <p className="mt-2">
          <GitHostLink site="GitHub" href={error.installUrl}>
            Open the GitHub App settings
          </GitHostLink>
        </p>
      )}
      {(isApiError(error, 'TRACKER_NOT_CONNECTED') || isApiError(error, 'CONNECTION_SCOPE_INSUFFICIENT')) && (
        <p className="mt-2">
          <ConnectionsLink onOpenConnections={onOpenConnections} />
        </p>
      )}
    </Alert>
  )
}

function ResultRow({
  item,
  tracker,
  checked,
  disabled,
  onToggle,
}: {
  item: TrackerItem
  tracker: string
  checked: boolean
  disabled: boolean
  onToggle: () => void
}) {
  return (
    <li className="flex flex-col gap-1 p-2 sm:flex-row sm:items-start sm:justify-between sm:gap-3" data-testid="tracker-result">
      <Checkbox
        checked={checked}
        disabled={disabled}
        onChange={onToggle}
        label={
          <>
            <span className="font-mono text-xs break-all">{item.key}</span> <span className="break-words">{item.title}</span>
          </>
        }
        hint={[typeLabels[item.type], item.status, ...item.labels].join(' · ')}
      />
      <span className="shrink-0 pl-6 sm:pl-0">
        <ExternalItemLink url={item.url} itemKey={item.key} tracker={tracker} />
      </span>
    </li>
  )
}

/** The form for one ready tracker. Remounted (keyed) per tracker and connection: a selection belongs to one of each. */
function BrowseForm({
  tracker,
  connectionId,
  capacity,
  pending,
  error,
  addLabel,
  onCancel,
  onAdd,
  onOpenConnections,
}: {
  tracker: TrackerStatus
  connectionId: string | null
  capacity: number
  pending: boolean
  error: string | null
  addLabel: (count: number) => string
  onCancel: () => void
  onAdd: (pick: PickedImport) => void
  onOpenConnections?: () => void
}) {
  const provider = tracker.provider
  const base = useId()
  const nativeLabel = nativeQueryLabels[provider]

  const [containerInput, setContainerInput] = useState('')
  const [containerSearch, setContainerSearch] = useState('')
  const [containerId, setContainerId] = useState('')
  const [containerError, setContainerError] = useState<string | null>(null)
  const [mode, setMode] = useState<'filters' | 'query'>('filters')
  const [text, setText] = useState('')
  const [statuses, setStatuses] = useState<readonly SourceItemStatusCategory[]>([])
  const [labels, setLabels] = useState('')
  const [parent, setParent] = useState('')
  const [query, setQuery] = useState('')
  const [criteria, setCriteria] = useState<TrackerCriteria | null>(null)
  const [selected, setSelected] = useState<ReadonlyMap<string, TrackerItem>>(new Map())
  /** "Select all matching" found everything within the cap: the import then repeats the search (FR-173 Re-import). */
  const [allMatching, setAllMatching] = useState(false)
  const [truncated, setTruncated] = useState(false)
  const [walking, setWalking] = useState(false)
  const [includeChildren, setIncludeChildren] = useState(true)
  const [includeComments, setIncludeComments] = useState(false)
  const [includeAssignees, setIncludeAssignees] = useState(false)

  useEffect(() => {
    const timer = setTimeout(() => {
      setContainerSearch(containerInput.trim())
    }, 300)
    return () => {
      clearTimeout(timer)
    }
  }, [containerInput])

  const containers = useTrackerContainers(provider, connectionId, containerSearch)
  const search = useTrackerSearch(criteria)
  const containerOptions = containers.data?.pages.flatMap((p) => p.items) ?? []
  const results = search.data?.pages.flatMap((p) => p.items) ?? []
  const queryInvalid = isApiError(search.error, 'TRACKER_QUERY_INVALID')

  const buildCriteria = (nextContainer: string, nextMode: 'filters' | 'query'): TrackerCriteria => {
    if (nextMode === 'query') {
      return { provider, connectionId, containerId: nextContainer === '' ? null : nextContainer, filter: null, query: query.trim() }
    }
    const labelList = labels
      .split(',')
      .map((l) => l.trim())
      .filter(Boolean)
    const filter: TrackerFilter = {
      ...(text.trim() ? { text: text.trim() } : {}),
      ...(statuses.length > 0 ? { statusCategories: [...statuses] } : {}),
      ...(labelList.length > 0 ? { labels: labelList } : {}),
      ...(parent.trim() ? { parent: parent.trim() } : {}),
    }
    return {
      provider,
      connectionId,
      containerId: nextContainer === '' ? null : nextContainer,
      filter: Object.keys(filter).length > 0 ? filter : null,
      query: null,
    }
  }

  const runSearch = () => {
    const needsContainer = mode === 'filters' || provider === 'github'
    if (mode === 'query' && query.trim() === '') {
      setContainerError(null)
      setCriteria(null)
      return
    }
    if (needsContainer && containerId === '') {
      setContainerError(`Choose a ${containerLabels[provider].toLowerCase()} first.`)
      return
    }
    setContainerError(null)
    setAllMatching(false)
    setTruncated(false)
    setCriteria(buildCriteria(containerId, mode))
  }

  const pickContainer = (next: string) => {
    setContainerId(next)
    setContainerError(null)
    setSelected(new Map())
    setAllMatching(false)
    setTruncated(false)
    // Filters run at once with the new container. A native query waits for Search, as it may not be written yet.
    setCriteria(next !== '' && mode === 'filters' ? buildCriteria(next, 'filters') : null)
  }

  const toggle = (item: TrackerItem) => {
    const next = new Map(selected)
    if (!next.delete(item.key)) {
      next.set(item.key, item)
    }
    setSelected(next)
    setAllMatching(false)
    setTruncated(false)
  }

  /** FR-169: walks the provider's pages up to the cap, so what's ticked is exactly what's imported. */
  const selectAllMatching = async () => {
    setWalking(true)
    try {
      let pages = search.data?.pages ?? []
      let more = search.hasNextPage
      while (more && pages.reduce((n, p) => n + p.items.length, 0) < capacity) {
        const page = await search.fetchNextPage()
        if (page.isError) {
          return
        }
        pages = page.data?.pages ?? pages
        more = page.hasNextPage
      }
      const all = pages.flatMap((p) => p.items)
      const cut = more || all.length > capacity
      setSelected(new Map(all.slice(0, capacity).map((item) => [item.key, item])))
      setAllMatching(!cut)
      setTruncated(cut)
    } finally {
      setWalking(false)
    }
  }

  const count = selected.size
  const atCapacity = count >= capacity

  const submit = () => {
    const items = [...selected.values()].map(({ key, title, url }) => ({ key, title, url }))
    const container = containerId === '' ? null : containerId
    let selection: PickedImport['selection'] = { keys: items.map((i) => i.key), containerId: container }
    if (allMatching && criteria) {
      selection = criteria.query
        ? { query: criteria.query, containerId: criteria.containerId }
        : { filter: criteria.filter, containerId: criteria.containerId }
    }
    onAdd({
      provider,
      connectionId,
      selection,
      options: { includeChildren, includeComments, includeAssignees },
      items,
    })
  }

  const containerLabel = containerLabels[provider]
  return (
    <div className="flex flex-col gap-4">
      <fieldset className="flex flex-col gap-2">
        <legend className="mb-1 text-sm font-semibold">{containerLabel}</legend>
        <TextField
          label={`Find a ${containerLabel.toLowerCase()}`}
          value={containerInput}
          autoComplete="off"
          spellCheck={false}
          onChange={(e) => {
            setContainerInput(e.target.value)
          }}
        />
        {containers.isError ? (
          <>
            <BrowseError error={containers.error} tracker={tracker.name} onOpenConnections={onOpenConnections} />
            <div>
              <Button variant="secondary" onClick={() => void containers.refetch()}>
                Try again
              </Button>
            </div>
          </>
        ) : (
          <SelectField
            label={`Choose a ${containerLabel.toLowerCase()}`}
            value={containerId}
            error={containerError ?? undefined}
            disabled={containers.isPending}
            onChange={(e) => {
              pickContainer(e.target.value)
            }}
          >
            <option value="">{containers.isPending ? 'Loading…' : containerOptions.length === 0 ? 'None found' : 'Choose…'}</option>
            {containerOptions.map((c) => (
              <option key={c.id} value={c.id}>
                {c.detail ? `${c.name} (${c.detail})` : c.name}
              </option>
            ))}
          </SelectField>
        )}
        {containers.hasNextPage && (
          <div>
            <Button variant="ghost" pending={containers.isFetchingNextPage} pendingLabel="Loading…" onClick={() => void containers.fetchNextPage()}>
              {`Load more ${provider === 'github' ? 'repositories' : 'projects'}`}
            </Button>
          </div>
        )}
      </fieldset>

      <form
        noValidate
        aria-label="Search the tracker"
        className="flex flex-col gap-3"
        onSubmit={(e) => {
          // The dialog can sit inside the wizard's form in the React tree: its submit must not bubble there.
          e.preventDefault()
          e.stopPropagation()
          runSearch()
        }}
      >
        {nativeLabel && (
          <fieldset className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
            <legend className="mb-1 font-semibold">Search with</legend>
            <label className="flex min-h-6 items-center gap-2">
              <input
                type="radio"
                name={`${base}-mode`}
                className="size-4 accent-primary"
                checked={mode === 'filters'}
                onChange={() => {
                  setMode('filters')
                }}
              />
              Filters
            </label>
            <label className="flex min-h-6 items-center gap-2">
              <input
                type="radio"
                name={`${base}-mode`}
                className="size-4 accent-primary"
                checked={mode === 'query'}
                onChange={() => {
                  setMode('query')
                }}
              />
              {nativeLabel}
            </label>
          </fieldset>
        )}
        {mode === 'query' && nativeLabel ? (
          <TextAreaField
            label={nativeLabel}
            hint={provider === 'jira' ? 'For example: project = PROJ AND statusCategory != Done' : 'For example: label:bug is:open'}
            rows={2}
            maxLength={2000}
            spellCheck={false}
            value={query}
            error={queryInvalid ? trackerBrowseMessage(search.error, tracker.name) : undefined}
            onChange={(e) => {
              setQuery(e.target.value)
            }}
          />
        ) : (
          <div className="grid gap-3 sm:grid-cols-2">
            <TextField
              label="Title contains"
              value={text}
              maxLength={300}
              autoComplete="off"
              onChange={(e) => {
                setText(e.target.value)
              }}
            />
            <TextField
              label="Labels"
              hint="Separate several with commas."
              value={labels}
              autoComplete="off"
              onChange={(e) => {
                setLabels(e.target.value)
              }}
            />
            <TextField
              label="Parent"
              hint={parentHints[provider]}
              value={parent}
              autoComplete="off"
              spellCheck={false}
              onChange={(e) => {
                setParent(e.target.value)
              }}
            />
            <fieldset className="flex flex-col gap-1">
              <legend className="text-sm font-medium">Status</legend>
              <p className="text-sm text-muted-foreground">Done items stay hidden unless you tick Done.</p>
              {statusOptions.map((s) => (
                <Checkbox
                  key={s.value}
                  label={s.label}
                  checked={statuses.includes(s.value)}
                  onChange={(e) => {
                    setStatuses(e.target.checked ? [...statuses, s.value] : statuses.filter((v) => v !== s.value))
                  }}
                />
              ))}
            </fieldset>
          </div>
        )}
        <div>
          <Button type="submit" variant="secondary">
            Search
          </Button>
        </div>
      </form>

      <section aria-label="Results" className="flex flex-col gap-2">
        {search.isError && !queryInvalid && <BrowseError error={search.error} tracker={tracker.name} onOpenConnections={onOpenConnections} />}
        {criteria === null ? (
          <p className="text-sm text-muted-foreground">
            {mode === 'query' ? 'Write a query, then choose Search.' : `Choose a ${containerLabel.toLowerCase()} to see its items.`}
          </p>
        ) : search.isPending ? (
          <LoadingState label="Searching…" />
        ) : search.isError ? null : results.length === 0 ? (
          <p className="text-sm text-muted-foreground">No items match. Done items stay hidden unless the search asks for them.</p>
        ) : (
          <>
            <div className="flex flex-wrap items-center justify-between gap-2">
              <p className="text-sm text-muted-foreground">{results.length === 1 ? '1 item shown' : `${String(results.length)} items shown`}</p>
              <Button
                variant="ghost"
                pending={walking}
                pendingLabel="Selecting…"
                disabled={capacity === 0}
                onClick={() => void selectAllMatching()}
              >
                Select all matching
              </Button>
            </div>
            <ul aria-label="Matching items" className="max-h-80 divide-y divide-border/60 overflow-y-auto rounded-lg border border-border/60">
              {results.map((item) => (
                <ResultRow
                  key={item.key}
                  item={item}
                  tracker={tracker.name}
                  checked={selected.has(item.key)}
                  disabled={!selected.has(item.key) && atCapacity}
                  onToggle={() => {
                    toggle(item)
                  }}
                />
              ))}
            </ul>
            {search.hasNextPage && (
              <div>
                <Button variant="ghost" pending={search.isFetchingNextPage} pendingLabel="Loading…" onClick={() => void search.fetchNextPage()}>
                  Load more
                </Button>
              </div>
            )}
          </>
        )}
      </section>

      <fieldset className="flex flex-col gap-1">
        <legend className="mb-1 text-sm font-semibold">Options</legend>
        <Checkbox
          label="Include children"
          hint="Sub-tasks and child issues of the items you pick."
          checked={includeChildren}
          onChange={(e) => {
            setIncludeChildren(e.target.checked)
          }}
        />
        <Checkbox
          label="Include comments"
          hint="The newest 20 per item."
          checked={includeComments}
          onChange={(e) => {
            setIncludeComments(e.target.checked)
          }}
        />
        <Checkbox
          label="Include assignees"
          hint="Names only, never emails."
          checked={includeAssignees}
          onChange={(e) => {
            setIncludeAssignees(e.target.checked)
          }}
        />
        <p className="mt-1 text-sm text-muted-foreground">Attachments are never imported: only their names are listed.</p>
      </fieldset>

      {error && (
        <Alert tone="error" role="alert" title="We couldn't add these items">
          <p className="break-words">{error}</p>
        </Alert>
      )}

      <div className="flex flex-wrap items-center justify-between gap-2">
        <p role="status" className="text-sm" data-testid="tracker-selection-status">
          {count === 0
            ? capacity === 0
              ? 'This version already has the most items it can import (200 across its imports). Remove some first.'
              : 'Tick the items to import.'
            : truncated
              ? `${String(count)} selected: the first ${String(count)} matching items. That's the most that can be imported (200 across a version's imports).`
              : allMatching
                ? `${String(count)} selected: all matching items.`
                : atCapacity
                  ? `${String(count)} selected. That's the most that can be imported (200 across a version's imports).`
                  : `${String(count)} selected.`}
        </p>
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" disabled={pending} onClick={onCancel}>
            Cancel
          </Button>
          <Button disabled={count === 0} pending={pending} pendingLabel="Adding…" onClick={submit}>
            {addLabel(count)}
          </Button>
        </div>
      </div>
    </div>
  )
}

/**
 * FR-169: the one browse dialog of the wizard, the new-version dialog and Refinement: tracker and connection, container,
 * filters or a native query, paginated results, ticks or "Select all matching", and the import options. `capacity` is
 * how many items can still be added. `onOpenConnections` runs before the link to Connections opens (the wizard saves
 * its draft there).
 */
export function TrackerBrowseDialog({
  capacity,
  pending = false,
  error = null,
  addLabel = (count) => (count === 1 ? 'Add 1 item' : `Add ${String(count)} items`),
  onClose,
  onAdd,
  onOpenConnections,
}: {
  capacity: number
  pending?: boolean
  error?: string | null
  addLabel?: (count: number) => string
  onClose: () => void
  onAdd: (pick: PickedImport) => void
  onOpenConnections?: () => void
}) {
  const trackers = useTrackers()
  const [providerChoice, setProviderChoice] = useState<TrackerProvider | null>(null)
  const [connectionChoice, setConnectionChoice] = useState<string | null>(null)
  const list = trackers.data ?? []
  const provider = providerChoice ?? list.find((t) => t.ready)?.provider ?? null
  const tracker = list.find((t) => t.provider === provider)
  const connections = tracker?.connections ?? []
  const connectionId =
    connections.find((c) => c.id === connectionChoice)?.id ??
    connections.find((c) => c.status === 'valid')?.id ??
    connections[0]?.id ??
    null

  let body: ReactNode
  if (trackers.isPending) {
    body = <LoadingState label="Loading your trackers…" />
  } else if (trackers.isError) {
    body = <ErrorState error={trackers.error} title="Your trackers couldn't load" onRetry={() => void trackers.refetch()} />
  } else if (!list.some((t) => t.ready)) {
    body = (
      <div className="flex flex-col gap-3">
        <EmptyState
          variant="not_connected"
          bare
          headingLevel={3}
          title="No issue tracker is connected"
          description="Connect Jira, Linear, GitHub or GitLab, then come back to pick items."
          action={<ConnectionsLink onOpenConnections={onOpenConnections} />}
        />
        {list
          .filter((t) => t.reason === 'issues_permission_missing' || t.reason === 'app_not_registered')
          .map((t) => (
            <TrackerReasonNotice key={t.provider} tracker={t} />
          ))}
        <div className="flex justify-end">
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
        </div>
      </div>
    )
  } else {
    body = (
      <div className="flex flex-col gap-4">
        <div className="grid gap-3 sm:grid-cols-2">
          <SelectField
            label="Tracker"
            value={provider ?? ''}
            onChange={(e) => {
              setProviderChoice(e.target.value as TrackerProvider)
              setConnectionChoice(null)
            }}
          >
            {list.map((t) => (
              <option key={t.provider} value={t.provider}>
                {t.ready ? t.name : `${t.name} (not ready)`}
              </option>
            ))}
          </SelectField>
          {tracker?.ready && connections.length > 1 && (
            <SelectField
              label="Connection"
              value={connectionId ?? ''}
              onChange={(e) => {
                setConnectionChoice(e.target.value)
              }}
            >
              {connections.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.accountName ? `${c.label} (${c.accountName})` : c.label}
                  {c.status === 'valid' ? '' : ` · ${c.status.replaceAll('_', ' ')}`}
                </option>
              ))}
            </SelectField>
          )}
        </div>
        {tracker?.ready ? (
          <BrowseForm
            key={`${tracker.provider}:${connectionId ?? ''}`}
            tracker={tracker}
            connectionId={connectionId}
            capacity={capacity}
            pending={pending}
            error={error}
            addLabel={addLabel}
            onCancel={onClose}
            onAdd={onAdd}
            onOpenConnections={onOpenConnections}
          />
        ) : (
          <>
            {tracker && <TrackerReasonNotice tracker={tracker} />}
            <p className="text-sm">
              <ConnectionsLink onOpenConnections={onOpenConnections} />
            </p>
            <div className="flex justify-end">
              <Button variant="secondary" onClick={onClose}>
                Cancel
              </Button>
            </div>
          </>
        )}
      </div>
    )
  }

  return (
    <ModalDialog
      open
      title="Import from tracker"
      description="Tevrin imports a snapshot of the items you pick and never refreshes it silently."
      busy={pending}
      onClose={onClose}
      className="sm:max-w-3xl"
    >
      {body}
    </ModalDialog>
  )
}
