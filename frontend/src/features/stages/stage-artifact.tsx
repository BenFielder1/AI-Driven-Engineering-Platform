import { Link } from '@tanstack/react-router'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { toast } from 'sonner'

import type { Schemas } from '../../api/client'
import { EmptyState } from '../../components/shared/empty-state'
import { Alert } from '../../components/ui/alert'
import { Badge } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { isApiError } from '../../lib/errors'
import { EditLatest } from '../artifacts/artifact-detail-page'
import { ArtifactDecisionDialog, type ArtifactDecision } from '../artifacts/artifact-decision-dialog'
import { corruptedCopy, isCurrentVersion } from '../artifacts/artifact-integrity'
import { ArtifactVersionDetails } from '../artifacts/artifact-version-details'
import { useArtifactHistory, useArtifactVersion, type Artifact, type ArtifactSummary, type ArtifactType } from '../artifacts/artifacts-api'
import { MarkdownView } from '../artifacts/markdown-view'
import { artifactStatusLabels, artifactTypeLabels } from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { currentLockedVersion, stageProgress } from './stage-model'
import { StageSection } from './stage-section'

type Phase = Schemas['Phase']

/** How often a current stage with no artifact version yet re-checks its history while a draft is expected. */
export const STAGE_EMPTY_POLL_MS = 10_000

function versionOption(a: ArtifactSummary, newest: number | null, current: number | null) {
  const marks = [a.version === newest ? 'latest' : null, a.version === current ? 'current' : null].filter(Boolean)
  return `v${String(a.version)} · ${a.status ? artifactStatusLabels[a.status] : 'Unknown'}${marks.length > 0 ? ` (${marks.join(', ')})` : ''}`
}

/**
 * FR-55: a stage's artifact in full on its stage page: the version picker (latest and current marked), the shown
 * version's facts (approver, producing run, release version), its rendered Markdown and the stage panel for it
 * (`children`). While the stage is current, an Owner approves or rejects a draft in place; a completed stage (or a
 * past release version, FR-62) shows the locked version without any approve or edit action.
 */
export function StageArtifact({
  stage,
  type,
  version,
  onVersionChange,
  notStarted,
  expectedLatest = null,
  awaitingDraft = false,
  children,
}: {
  stage: Phase
  type: ArtifactType
  /** The version picked in the URL (`?version=N`); the newest when absent. */
  version?: number
  onVersionChange?: (version: number | undefined) => void
  /** The empty state: what produces this artifact and what has to happen first. */
  notStarted: string
  /** A newer version the page already knows of (e.g. from GET /refinement): the history is refetched to show it. */
  expectedLatest?: number | null
  /**
   * A draft is on its way: the producing run is queued or running, or (Review) the latest cycle asked for an
   * optimisation plan. Only then does an empty history poll; otherwise nothing is coming and the page waits for events.
   */
  awaitingDraft?: boolean
  /** The stage panel for the shown version (outline, step table…). */
  children?: (artifact: Artifact) => ReactNode
}) {
  const { project, can, readOnly, releaseVersionId } = useProjectAccess()
  const label = artifactTypeLabels[type]
  const progress = stageProgress(stage, project, readOnly)
  // A safety net under the live updates (FR-46): while the current stage has no version yet and a draft is expected,
  // poll its history so the producing run's draft appears even if its live event was missed.
  const history = useArtifactHistory(
    project.id,
    type,
    releaseVersionId,
    progress === 'current' && awaitingDraft ? STAGE_EMPTY_POLL_MS : undefined,
  )
  const versions = history.data ?? []
  const newest = versions[0]?.version ?? null
  const current = currentLockedVersion(versions)
  const [localVersion, setLocalVersion] = useState<number | undefined>(undefined)
  const picked = onVersionChange ? version : localVersion
  const selected = picked !== undefined && versions.some((a) => a.version === picked) ? picked : newest
  const shown = useArtifactVersion(project.id, type, selected)
  const [decision, setDecision] = useState<ArtifactDecision | null>(null)
  const [editing, setEditing] = useState(false)
  const headingRef = useRef<HTMLSpanElement>(null)
  const stale = history.isSuccess && expectedLatest !== null && (newest ?? 0) < expectedLatest
  const refetchHistory = history.refetch
  useEffect(() => {
    if (stale) {
      void refetchHistory()
    }
  }, [stale, refetchHistory])
  const pick = (next: number | undefined) => {
    if (onVersionChange) {
      onVersionChange(next)
    } else {
      setLocalVersion(next)
    }
  }

  const open = (
    <Link
      to="/projects/$projectId/artifacts/$type"
      params={{ projectId: project.id, type }}
      search={selected === null ? {} : { version: selected }}
      className="text-sm text-primary underline"
    >
      Open in Artifacts{' '}<span className="sr-only">({label.toLowerCase()} history and compare)</span>
    </Link>
  )

  let body: ReactNode
  if (history.isPending) {
    body = <LoadingState label={`Loading the ${label.toLowerCase()}…`} />
  } else if (history.isError && !isApiError(history.error, 'NOT_FOUND')) {
    body = <ErrorState error={history.error} title={`The ${label.toLowerCase()} couldn't load`} onRetry={() => void history.refetch()} />
  } else if (editing) {
    body = (
      <EditLatest
        projectId={project.id}
        type={type}
        openDraftVersion={versions.find((a) => a.status === 'draft')?.version ?? null}
        onCancel={() => {
          setEditing(false)
        }}
        onSaved={(created) => {
          setEditing(false)
          toast.success(`Saved as draft v${String(created.version)}. An Owner can now approve or reject it.`)
          pick(created.version)
          headingRef.current?.focus()
        }}
      />
    )
  } else if (versions.length === 0) {
    body = (
      <EmptyState
        variant="not_started"
        bare
        headingLevel={3}
        title={`No ${label.toLowerCase()} yet`}
        description={notStarted}
      />
    )
  } else {
    const artifact = shown.data
    const canEdit = progress === 'current' && can('createDraft')
    const canDecide =
      progress === 'current' && can('approveArtifacts') && artifact?.status === 'draft' && artifact.integrity === 'ok'
    body = (
      <>
        <div className="flex flex-wrap items-end justify-between gap-3">
          <SelectField
            label="Version"
            value={selected === null ? '' : String(selected)}
            onChange={(e) => {
              const next = Number(e.target.value)
              pick(next === newest ? undefined : next)
            }}
          >
            {versions.map((a) => (
              <option key={a.id} value={a.version}>
                {versionOption(a, newest, current)}
              </option>
            ))}
          </SelectField>
          {(canDecide || canEdit) && (
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
              {canDecide && (
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
        {progress === 'current' && !can('approveArtifacts') && artifact?.status === 'draft' && !readOnly && (
          <p className="text-sm text-muted-foreground">This draft is waiting for a project Owner to approve or reject it.</p>
        )}
        {progress === 'done' && !readOnly && (
          <p className="text-sm text-muted-foreground">
            {phaseDoneCopy(label)}
          </p>
        )}
        {shown.isPending ? (
          <LoadingState label="Loading the version…" />
        ) : shown.isError ? (
          <ErrorState error={shown.error} title="This version couldn't load" onRetry={() => void shown.refetch()} />
        ) : (
          <>
            <p className="flex flex-wrap items-center gap-2 text-sm">
              <span className="font-mono font-medium tabular-nums">v{shown.data.version}</span>
              {shown.data.version === newest && <Badge tone="info">Latest</Badge>}
              {shown.data.version === current && <Badge tone="success">Current</Badge>}
            </p>
            {shown.data.integrity === 'corrupted' && (
              <Alert tone="error" role="alert" title={corruptedCopy(shown.data, isCurrentVersion(shown.data.version, versions)).title}>
                <p>{corruptedCopy(shown.data, isCurrentVersion(shown.data.version, versions)).body}</p>
              </Alert>
            )}
            <ArtifactVersionDetails projectId={project.id} artifact={shown.data} />
            {children?.(shown.data)}
            <MarkdownView content={shown.data.content} label={`${label} v${String(shown.data.version)}`} headingIds />
          </>
        )}
      </>
    )
  }

  return (
    <StageSection id="stage-artifact" title={<span ref={headingRef} tabIndex={-1}>{label}</span>} actions={open}>
      {body}
      {decision && shown.data && (
        <ArtifactDecisionDialog
          projectId={project.id}
          artifact={shown.data}
          action={decision}
          fallbackFocusRef={headingRef}
          onClose={() => {
            setDecision(null)
          }}
          onDecided={(action, decided) => {
            setDecision(null)
            toast.success(
              action === 'approve' ? `v${String(decided.version)} approved and locked.` : `v${String(decided.version)} rejected.`,
            )
          }}
        />
      )}
    </StageSection>
  )
}

const phaseDoneCopy = (label: string) =>
  `This stage is complete, so the ${label.toLowerCase()} can't be approved or edited here. Roll back the project to change it.`
