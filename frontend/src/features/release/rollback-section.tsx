import { useState } from 'react'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { rollbackAwaitingMergeMessage } from '../../lib/errors'
import type { GitHostTerms } from '../../lib/git-host'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { phaseLabels } from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import { useRollbacks, type Rollback } from './release-api'
import { checkpointLabel, useReleaseVersionNames } from '../versions/version-label'
import { rollbackEffects } from './rollback-format'
import { RollbackDialog } from './rollback-dialog'

function RollbackHistory({ projectId }: { projectId: string }) {
  const rollbacks = useRollbacks(projectId)
  const nameOf = useReleaseVersionNames(projectId)
  if (rollbacks.isPending) {
    return <LoadingState label="Loading rollbacks…" />
  }
  if (rollbacks.isError) {
    return <ErrorState error={rollbacks.error} title="The rollbacks couldn't load" onRetry={() => void rollbacks.refetch()} />
  }
  if (rollbacks.data.length === 0) {
    return <p className="text-sm text-muted-foreground">No rollbacks yet.</p>
  }
  return (
    <ol aria-label="Rollbacks" className="flex flex-col gap-3 text-sm">
      {rollbacks.data.map((r) => (
        <li key={r.id} className="rounded-lg border border-border/60 p-3">
          <p className="font-medium">
            {`${checkpointLabel(nameOf(r.releaseVersionId), r.fromCheckpoint)} → checkpoint ${String(r.toCheckpoint)}`}, restored{' '}
            {phaseLabels[r.restoredPhase]}
          </p>
          <p className="text-muted-foreground">
            {r.createdBy.name} · <RelativeTime iso={r.createdAt} />
          </p>
          <p className="mt-1 whitespace-pre-wrap break-words">{r.reason}</p>
          <p className="mt-1 text-muted-foreground">{rollbackEffects(r).join(' · ')}</p>
        </li>
      ))}
    </ol>
  )
}

/**
 * Settings → Danger zone → Roll back (FR-44, FR-45): opens the rollback dialog, then summarises the recorded rollback.
 * The history lists every rollback, newest first.
 */
export function RollbackSection({
  projectId,
  developmentVersion,
  releasedVersion,
  currentCheckpoint,
  awaitingMerge = false,
  host,
}: {
  projectId: string
  /** The version in development; `null` when the project is released with nothing in development. */
  developmentVersion: string | null
  releasedVersion: string | null
  currentCheckpoint: number
  /** FR-65, §4.8: the release is awaiting its merge (ROLLBACK_BLOCKED release_partially_applied). */
  awaitingMerge?: boolean
  /** FR-140: the project's Git host, named in the awaiting-merge reason (default GitHub). */
  host?: GitHostTerms
}) {
  const [open, setOpen] = useState(false)
  const [done, setDone] = useState<Rollback | null>(null)
  // FR-63, D-V2-3: a released version is closed; the way forward is a new version.
  const closed = developmentVersion === null || awaitingMerge
  const closedReason = awaitingMerge
    ? rollbackAwaitingMergeMessage(host)
    : releasedVersion
      ? `Version ${releasedVersion} is released and closed, so it can't be rolled back. Start a new version to make changes.`
      : 'There is no version in development to roll back.'

  return (
    <section aria-labelledby="rollback-title" className="flex flex-col gap-3 rounded-lg border border-destructive p-4">
      <div>
        <h3 id="rollback-title" className="font-semibold">
          Roll back
        </h3>
        <p className="mt-1 text-sm text-muted-foreground">
          Restores the phase of an earlier checkpoint of the version in development. Later artifacts are invalidated and pending gates and queued
          jobs are cancelled; nothing is deleted and the Git branch isn&apos;t rewritten. One rollback per minute, and not
          while a step or agent run is in progress.
        </p>
      </div>
      <div role="status">
        {done && (
          <Alert
            tone="success"
            title={`Rolled back from ${checkpointLabel(developmentVersion, done.fromCheckpoint)} to checkpoint ${String(done.toCheckpoint)}`}
          >
            <p>The project is back in {phaseLabels[done.restoredPhase]}.</p>
            <p className="mt-1">{rollbackEffects(done).join(' · ')}.</p>
          </Alert>
        )}
      </div>
      <div className="flex flex-col gap-1">
        <div>
          <Button
            variant="danger"
            aria-disabled={closed ? true : undefined}
            aria-describedby={closed ? 'rollback-closed-reason' : undefined}
            title={closed ? closedReason : undefined}
            className={closed ? 'cursor-not-allowed opacity-60' : undefined}
            onClick={() => {
              if (!closed) {
                setOpen(true)
              }
            }}
          >
            Roll back…
          </Button>
        </div>
        {closed && (
          <p id="rollback-closed-reason" className="text-sm text-muted-foreground">
            {closedReason}
          </p>
        )}
      </div>
      {open && (
        <RollbackDialog
          projectId={projectId}
          version={developmentVersion}
          currentCheckpoint={currentCheckpoint}
          onClose={() => {
            setOpen(false)
          }}
          onRolledBack={(r) => {
            setOpen(false)
            setDone(r)
          }}
        />
      )}
      <section aria-labelledby="rollback-history-title" className="flex flex-col gap-2">
        <h4 id="rollback-history-title" className="font-semibold">
          Rollback history
        </h4>
        <RollbackHistory projectId={projectId} />
      </section>
    </section>
  )
}
