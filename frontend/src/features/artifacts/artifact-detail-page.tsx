import { Link } from '../../lib/navigation'
import { useRef, useState, type SyntheticEvent, type ReactNode } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { SelectField } from '../../components/ui/select-field'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { isApiError } from '../../lib/errors'
import { useDocumentTitle } from '../../lib/use-document-title'
import { artifactStatusLabels, artifactTypeLabels } from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { RelativeTime } from '../projects/relative-time'
import { useReleaseVersionNames } from '../versions/version-label'
import { authorName, isAgentAuthored } from './artifact-author'
import { ArtifactStatusBadge, IntegrityBadge } from './artifact-badges'
import { ArtifactDecisionDialog, type ArtifactDecision } from './artifact-decision-dialog'
import { ArtifactEditor } from './artifact-editor'
import { ArtifactVersionDetails } from './artifact-version-details'
import { corruptedCopy, isCurrentVersion } from './artifact-integrity'
import {
  useAllArtifactHistory,
  useArtifactDiff,
  useArtifactHistory,
  useArtifactVersion,
  useLatestArtifact,
  type Artifact,
  type ArtifactSummary,
  type ArtifactType,
} from './artifacts-api'
import { DiffView } from './diff-view'
import { MarkdownView } from './markdown-view'
import { isPlanType } from './plan-block'
import { PlanSteps } from './plan-steps'
import { RestoreVersionAction } from './restore-version'

const versionOption = (a: ArtifactSummary) =>
  `v${String(a.version)} · ${a.status ? artifactStatusLabels[a.status] : 'Unknown'}${a.integrity === 'corrupted' ? ' · integrity check failed' : ''}`

/** FR-59: the compare options name each version's release version, since they span release versions. */
const compareOption = (a: ArtifactSummary) => `${versionOption(a)}${a.releaseVersion ? ` · ${a.releaseVersion}` : ''}`

function HistoryTable({
  projectId,
  type,
  history,
  selected,
}: {
  projectId: string
  type: ArtifactType
  history: ArtifactSummary[]
  selected: number
}) {
  return (
    <div className="overflow-x-auto rounded-xl border border-border/60 bg-card/70 shadow-sm backdrop-blur-md">
      <table className="w-full border-collapse text-sm">
        <caption className="sr-only">Version history, newest first</caption>
        <thead>
          <tr className="border-b border-border/60 text-left text-xs text-muted-foreground">
            <th scope="col" className="px-3 py-2">
              Version
            </th>
            <th scope="col" className="px-3 py-2">
              Status
            </th>
            <th scope="col" className="px-3 py-2">
              Release version
            </th>
            <th scope="col" className="px-3 py-2">
              Author
            </th>
            <th scope="col" className="py-2">
              Created
            </th>
          </tr>
        </thead>
        <tbody>
          {history.map((a) => (
            <tr key={a.id} className="border-b border-border last:border-b-0">
              <th scope="row" className="px-3 py-2 text-left font-medium">
                <Link
                  to="/projects/$projectId/artifacts/$type"
                  params={{ projectId, type }}
                  search={{ version: a.version }}
                  aria-current={a.version === selected ? 'true' : undefined}
                  className="text-primary underline"
                >
                  v{a.version}
                </Link>
                {a.version === selected && <span className="ml-1 text-xs font-normal text-muted-foreground">(shown)</span>}
                {a.restoredFromVersion !== null && (
                  <span className="block text-xs font-normal text-muted-foreground">restored from v{a.restoredFromVersion}</span>
                )}
              </th>
              <td className="px-3 py-2">
                <span className="flex flex-wrap gap-1">
                  <ArtifactStatusBadge status={a.status} />
                  {a.integrity === 'corrupted' && <IntegrityBadge integrity={a.integrity} />}
                </span>
              </td>
              <td className="px-3 py-2 font-mono tabular-nums">{a.releaseVersion || '—'}</td>
              <td className="px-3 py-2">{authorName(a.author)}</td>
              <td className="py-2">
                <RelativeTime iso={a.createdAt} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/** FR-21, FR-59: pick any two versions, across release versions, and see the side-by-side diff. */
function ComparePanel({ projectId, type, scoped }: { projectId: string; type: ArtifactType; scoped: ArtifactSummary[] }) {
  const all = useAllArtifactHistory(projectId, type)
  // Until (or unless) the cross-version history loads, the release version's own history is offered.
  const history = all.data && all.data.length >= scoped.length ? all.data : scoped
  const newest = history[0]?.version ?? 1
  const [from, setFrom] = useState(String(history[1]?.version ?? newest))
  const [to, setTo] = useState(String(newest))
  const [pair, setPair] = useState<{ from: number; to: number } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const diff = useArtifactDiff(projectId, type, pair)

  if (history.length < 2) {
    return <p className="text-sm text-muted-foreground">There is only one version so far. Comparing needs at least two.</p>
  }

  const onSubmit = (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (from === to) {
      setError('Pick two different versions.')
      return
    }
    setError(null)
    setPair({ from: Number(from), to: Number(to) })
  }

  const options = history.map((a) => (
    <option key={a.id} value={a.version}>
      {compareOption(a)}
    </option>
  ))

  return (
    <div className="flex flex-col gap-3">
      <form noValidate aria-label="Compare versions" onSubmit={onSubmit} className="flex flex-wrap items-end gap-3">
        <SelectField
          label="From version"
          value={from}
          onChange={(e) => {
            setFrom(e.target.value)
          }}
          error={error ?? undefined}
        >
          {options}
        </SelectField>
        <SelectField
          label="To version"
          value={to}
          onChange={(e) => {
            setTo(e.target.value)
          }}
        >
          {options}
        </SelectField>
        <Button type="submit" variant="secondary">
          Compare
        </Button>
      </form>
      {pair && diff.isPending && <LoadingState label="Loading the diff…" />}
      {/* Too large (400 VALIDATION) won't change on retry. */}
      {pair && diff.isError && (
        <ErrorState
          error={diff.error}
          context="artifactDiff"
          title="The diff couldn't load"
          onRetry={isApiError(diff.error, 'VALIDATION') ? undefined : () => void diff.refetch()}
        />
      )}
      {pair && diff.data && (
        <div aria-live="polite" aria-busy={diff.isFetching || undefined}>
          <DiffView diff={diff.data} />
        </div>
      )}
    </div>
  )
}

function CorruptedAlert({ artifact, current }: { artifact: Artifact; current: boolean }) {
  const copy = corruptedCopy(artifact, current)
  return (
    <Alert tone="error" role="alert" title={copy.title}>
      <p>{copy.body}</p>
    </Alert>
  )
}

function Section({ id, title, children }: { id: string; title: string; children: ReactNode }) {
  return (
    <section aria-labelledby={id} className="flex flex-col gap-3">
      <h3 id={id} className="text-lg">
        {title}
      </h3>
      {children}
    </section>
  )
}

/** Opens the editor once the latest version (FR-18: the edit starts from it) has loaded. */
export function EditLatest({
  projectId,
  type,
  openDraftVersion,
  onCancel,
  onSaved,
}: {
  projectId: string
  type: ArtifactType
  openDraftVersion: number | null
  onCancel: () => void
  onSaved: (artifact: Artifact) => void
}) {
  const latest = useLatestArtifact(projectId, type)
  if (latest.isPending) {
    return <LoadingState label="Loading the latest version…" />
  }
  if (latest.isError && !isApiError(latest.error, 'NOT_FOUND')) {
    return <ErrorState error={latest.error} title="The latest version couldn't load" onRetry={() => void latest.refetch()} />
  }
  return (
    <ArtifactEditor
      projectId={projectId}
      type={type}
      base={latest.data ?? null}
      openDraftVersion={openDraftVersion}
      onCancel={onCancel}
      onSaved={onSaved}
    />
  )
}

/**
 * /projects/:id/artifacts/:type (FR-16–FR-21, SPEC §7): rendered Markdown of the picked version, version picker,
 * history, side-by-side diff, edit as a new draft (writers) and approve/reject (Owners).
 */
export function ArtifactDetailPage({
  type,
  version,
  onVersionChange,
}: {
  type: ArtifactType
  /** The version picked in the URL; the newest version when absent. */
  version: number | undefined
  onVersionChange: (version: number | undefined) => void
}) {
  const { project, can, releaseVersionId, view } = useProjectAccess()
  const versionName = useReleaseVersionNames(project.id)
  const releaseName = view.version?.version ?? versionName(project.releaseVersionId)
  const label = artifactTypeLabels[type]
  useDocumentTitle(`${label} · ${project.name}`)
  // FR-62: a past release version is read-only (`can` is false for mutations there).
  const canEdit = can('createDraft')
  const canDecide = can('approveArtifacts')

  const history = useArtifactHistory(project.id, type, releaseVersionId)
  const versions = history.data ?? []
  const newest = versions[0]?.version ?? null
  const selected = version !== undefined && versions.some((a) => a.version === version) ? version : newest
  const shown = useArtifactVersion(project.id, type, selected)

  const [editing, setEditing] = useState(false)
  const [decision, setDecision] = useState<ArtifactDecision | null>(null)
  /** §7: after rejecting an agent's draft, show "regenerating" until a newer version arrives (live event or refetch). */
  const [regeneratingAfter, setRegeneratingAfter] = useState<number | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  const header = (
    <div className="mb-4 flex flex-col gap-1">
      <Link to="/projects/$projectId/artifacts" params={{ projectId: project.id }} className="text-sm text-primary underline">
        All artifacts
      </Link>
      <h2 ref={headingRef} tabIndex={-1} className="text-xl">
        {label}
      </h2>
      {releaseName && <p className="text-sm text-muted-foreground">Release version {releaseName}</p>}
    </div>
  )


  if (history.isPending) {
    return (
      <>
        {header}
        <LoadingState label={`Loading the ${label.toLowerCase()}…`} />
      </>
    )
  }
  const noVersions = versions.length === 0 || isApiError(history.error, 'NOT_FOUND')
  if (history.isError && !noVersions) {
    return (
      <>
        {header}
        <ErrorState error={history.error} onRetry={() => void history.refetch()} title={`The ${label.toLowerCase()} couldn't load`} />
      </>
    )
  }

  const openDraft = versions.find((a) => a.status === 'draft')?.version ?? null
  const onSaved = (created: Artifact) => {
    setEditing(false)
    toast.success(`Saved as draft v${String(created.version)}. An Owner can now approve or reject it.`)
    onVersionChange(created.version)
    headingRef.current?.focus()
  }

  if (noVersions) {
    return (
      <>
        {header}
        {editing ? (
          <EditLatest
            projectId={project.id}
            type={type}
            openDraftVersion={null}
            onCancel={() => {
              setEditing(false)
            }}
            onSaved={onSaved}
          />
        ) : (
          <EmptyState title="No versions yet">
            <p>The producing agent creates the first version{canEdit ? ', or you can write it yourself' : ''}.</p>
            {canEdit && (
              <Button
                variant="secondary"
                className="mt-3"
                onClick={() => {
                  setEditing(true)
                }}
              >
                Write the first draft
              </Button>
            )}
          </EmptyState>
        )}
      </>
    )
  }

  const regenerating = regeneratingAfter !== null && newest !== null && newest <= regeneratingAfter
  const artifact = shown.data
  const decidable = artifact?.status === 'draft' && artifact.integrity === 'ok'

  const onDecided = (action: ArtifactDecision, decided: Artifact) => {
    setDecision(null)
    if (action === 'approve') {
      toast.success(`v${String(decided.version)} approved and locked.`)
    } else {
      toast.success(`v${String(decided.version)} rejected.`)
      if (isAgentAuthored(decided.author)) {
        setRegeneratingAfter(Math.max(newest ?? decided.version, decided.version))
      }
    }
  }

  return (
    <>
      {header}
      <div className="flex flex-col gap-6">
        {regenerating && (
          <p role="status" className="rounded-lg border border-border/60 bg-muted/60 p-3 text-sm" aria-busy="true">
            <span aria-hidden="true" className="mr-2 inline-block size-3 animate-pulse rounded-full bg-primary align-middle" />
            The agent is regenerating the {label.toLowerCase()} with your feedback. The new draft appears here when it&apos;s
            ready.
          </p>
        )}

        <div className="flex flex-wrap items-end justify-between gap-3">
          <SelectField
            label="Version"
            value={selected === null ? '' : String(selected)}
            onChange={(e) => {
              onVersionChange(Number(e.target.value) === newest ? undefined : Number(e.target.value))
            }}
          >
            {versions.map((a) => (
              <option key={a.id} value={a.version}>
                {versionOption(a)}
                {a.version === newest ? ' (latest)' : ''}
              </option>
            ))}
          </SelectField>
          {!editing && (
            <div className="flex flex-wrap gap-2">
              {canEdit && (
                <Button
                  variant="secondary"
                  onClick={() => {
                    setEditing(true)
                  }}
                >
                  Edit as new draft
                </Button>
              )}
              {canDecide && decidable && (
                <>
                  <Button
                    onClick={() => {
                      setDecision('approve')
                    }}
                  >
                    Approve
                  </Button>
                  <Button
                    variant="danger"
                    onClick={() => {
                      setDecision('reject')
                    }}
                  >
                    Reject
                  </Button>
                </>
              )}
            </div>
          )}
        </div>

        {editing ? (
          <EditLatest
            projectId={project.id}
            type={type}
            openDraftVersion={openDraft}
            onCancel={() => {
              setEditing(false)
            }}
            onSaved={onSaved}
          />
        ) : shown.isPending ? (
          <LoadingState label="Loading the version…" />
        ) : shown.isError ? (
          <ErrorState error={shown.error} title="This version couldn't load" onRetry={() => void shown.refetch()} />
        ) : (
          <>
            {shown.data.integrity === 'corrupted' && (
              <CorruptedAlert artifact={shown.data} current={isCurrentVersion(shown.data.version, versions)} />
            )}
            {newest !== null && (
              <RestoreVersionAction
                key={shown.data.version}
                type={type}
                shown={shown.data}
                latest={newest}
                onRestored={(created) => {
                  onVersionChange(created)
                  headingRef.current?.focus()
                }}
              />
            )}
            <ArtifactVersionDetails projectId={project.id} artifact={shown.data} />
            {isPlanType(type) && (
              <Section id="plan-steps" title="Steps">
                <PlanSteps projectId={project.id} artifact={shown.data} />
              </Section>
            )}
            <MarkdownView content={shown.data.content} label={`${label} v${String(shown.data.version)}`} />
          </>
        )}

        <Section id="artifact-history" title="History">
          <HistoryTable projectId={project.id} type={type} history={versions} selected={selected ?? 0} />
        </Section>

        <Section id="artifact-compare" title="Compare versions">
          <ComparePanel projectId={project.id} type={type} scoped={versions} />
        </Section>
      </div>

      {decision && artifact && (
        <ArtifactDecisionDialog
          projectId={project.id}
          artifact={artifact}
          action={decision}
          fallbackFocusRef={headingRef}
          onClose={() => {
            setDecision(null)
          }}
          onDecided={onDecided}
        />
      )}
    </>
  )
}
