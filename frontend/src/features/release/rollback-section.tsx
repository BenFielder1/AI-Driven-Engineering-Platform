import { useState } from 'react'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { phaseLabels } from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import { useRollbacks, type Rollback } from './release-api'
import { rollbackEffects } from './rollback-format'
import { RollbackDialog } from './rollback-dialog'

function RollbackHistory({ projectId }: { projectId: string }) {
  const rollbacks = useRollbacks(projectId)
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
            Version {r.fromVersion} → version {r.toVersion}, restored {phaseLabels[r.restoredPhase]}
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
export function RollbackSection({ projectId, currentVersion }: { projectId: string; currentVersion: number }) {
  const [open, setOpen] = useState(false)
  const [done, setDone] = useState<Rollback | null>(null)

  return (
    <section aria-labelledby="rollback-title" className="flex flex-col gap-3 rounded-lg border border-destructive p-4">
      <div>
        <h3 id="rollback-title" className="font-semibold">
          Roll back
        </h3>
        <p className="mt-1 text-sm text-muted-foreground">
          Restores the phase of an earlier project version. Later artifacts are invalidated and pending gates and queued
          jobs are cancelled; nothing is deleted and the Git branch isn&apos;t rewritten. One rollback per minute, and not
          while a step or agent run is in progress.
        </p>
      </div>
      <div role="status">
        {done && (
          <Alert tone="success" title={`Rolled back from version ${String(done.fromVersion)} to version ${String(done.toVersion)}`}>
            <p>The project is back in {phaseLabels[done.restoredPhase]}.</p>
            <p className="mt-1">{rollbackEffects(done).join(' · ')}.</p>
          </Alert>
        )}
      </div>
      <div>
        <Button
          variant="danger"
          onClick={() => {
            setOpen(true)
          }}
        >
          Roll back…
        </Button>
      </div>
      {open && (
        <RollbackDialog
          projectId={projectId}
          currentVersion={currentVersion}
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
