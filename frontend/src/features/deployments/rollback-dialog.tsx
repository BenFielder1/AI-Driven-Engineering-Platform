import { useRef } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { errorMessage } from '../../lib/errors'
import { formatDateTime } from '../projects/labels'
import { DeploymentLink } from './deployment-parts'
import { componentLabels, deploymentProviderLabels, rollbackWording, shortSha } from './deployment-format'
import { useRollbackDeployment, type Deployment } from './deployments-api'

/**
 * SPEC §7 rollback dialog (FR-91, FR-150, Owner): make an earlier ready production deployment current again with the
 * platform's own action, worded as the platform words it. Git, tags and releases are left alone. Rolling back a
 * backend never rolls back its database: the dialog warns when the deployment is older than the latest migration.
 */
export function RollbackDeploymentDialog({
  projectId,
  deployment,
  current,
  cloudflarePages = false,
  onClose,
}: {
  projectId: string
  deployment: Deployment
  /** The production deployment of the same component that is current now, if known. */
  current: Deployment | undefined
  /** The component's Cloudflare target links a Pages project ("Roll back"), not a Worker ("Deploy this version"). */
  cloudflarePages?: boolean
  onClose: () => void
}) {
  const d = deployment
  const rollback = useRollbackDeployment(projectId)
  const wording = rollbackWording(d.platform, cloudflarePages)
  const cancelRef = useRef<HTMLButtonElement>(null)
  const what = `${d.releaseVersion} (${shortSha(d.gitSha)})`
  const confirm = () => {
    rollback.mutate(d.id, {
      onSuccess: () => {
        toast.success(wording.started(what))
        onClose()
      },
    })
  }
  const databaseWarning = d.component === 'backend' && d.olderThanLatestMigration === true
  return (
    <ModalDialog
      open
      role="alertdialog"
      title={wording.title(d.releaseVersion)}
      initialFocusRef={cancelRef}
      busy={rollback.isPending}
      onClose={onClose}
      className="sm:max-w-lg"
      description={
        <div className="flex flex-col gap-2">
          <p>
            {wording.explanation} Git isn&apos;t changed: the default branch, tags and releases stay as they are, so the next
            release deploys the latest code again.
          </p>
          <dl className="grid gap-1 text-sm">
            <div className="flex flex-wrap gap-2">
              <dt className="text-muted-foreground">Component</dt>
              <dd>
                {componentLabels[d.component]} on {deploymentProviderLabels[d.platform]}
              </dd>
            </div>
            <div className="flex flex-wrap gap-2">
              <dt className="text-muted-foreground">Deployment</dt>
              <dd>
                <span className="font-mono">{d.releaseVersion}</span> ·{' '}
                <code className="font-mono text-xs">{shortSha(d.gitSha)}</code> · {formatDateTime(d.createdAt)}
              </dd>
            </div>
            <div className="flex flex-wrap gap-2">
              <dt className="text-muted-foreground">URL</dt>
              <dd className="min-w-0">
                <DeploymentLink url={d.url} />
              </dd>
            </div>
            {current && current.id !== d.id && (
              <div className="flex flex-wrap gap-2">
                <dt className="text-muted-foreground">Live now</dt>
                <dd>
                  <span className="font-mono">{current.releaseVersion}</span> ·{' '}
                  <code className="font-mono text-xs">{shortSha(current.gitSha)}</code>
                </dd>
              </div>
            )}
          </dl>
        </div>
      }
    >
      <div className="flex flex-col gap-3">
        {databaseWarning && (
          <Alert tone="warning" title="The database isn't rolled back">
            <p data-testid="rollback-migration-warning">
              Rolling back the backend never rolls back its database. This deployment is older than the latest applied
              migration, so it runs against a newer schema than it was built for. Check that it still works with it.
            </p>
          </Alert>
        )}
        {rollback.isError && (
          <Alert tone="error" role="alert" title="We couldn't roll back the deployment">
            <p>{errorMessage(rollback.error, 'rollbackDeployment')}</p>
          </Alert>
        )}
        <div className="flex flex-wrap justify-end gap-2">
          <Button ref={cancelRef} variant="secondary" onClick={onClose} disabled={rollback.isPending}>
            Cancel
          </Button>
          <Button onClick={confirm} pending={rollback.isPending} pendingLabel={wording.pending}>
            {wording.confirm}
          </Button>
        </div>
      </div>
    </ModalDialog>
  )
}
