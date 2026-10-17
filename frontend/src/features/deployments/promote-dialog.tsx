import { useRef } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { errorMessage } from '../../lib/errors'
import { formatDateTime } from '../projects/labels'
import { DeploymentLink } from './deployment-parts'
import { shortSha } from './deployment-format'
import { usePromoteDeployment, type Deployment } from './deployments-api'

/**
 * SPEC §7.2 "Promote deployment" dialog (FR-91, Owner): serve an earlier ready production deployment at the production
 * URL again. It is a production rollback only: Git, tags and releases are left alone.
 */
export function PromoteDeploymentDialog({
  projectId,
  deployment,
  current,
  onClose,
}: {
  projectId: string
  deployment: Deployment
  /** The production deployment live now, if known. */
  current: Deployment | undefined
  onClose: () => void
}) {
  const promote = usePromoteDeployment(projectId)
  const cancelRef = useRef<HTMLButtonElement>(null)
  const d = deployment
  const confirm = () => {
    promote.mutate(d.id, {
      onSuccess: () => {
        toast.success(`Promoting ${d.releaseVersion} (${shortSha(d.gitSha)}) to production.`)
        onClose()
      },
    })
  }
  return (
    <ModalDialog
      open
      role="alertdialog"
      title={`Promote ${d.releaseVersion} to production?`}
      initialFocusRef={cancelRef}
      busy={promote.isPending}
      onClose={onClose}
      className="sm:max-w-lg"
      description={
        <div className="flex flex-col gap-2">
          <p>
            Vercel serves this deployment at the production URL again. Git isn&apos;t changed: the default branch, tags and
            releases stay as they are, so the next release deploys the latest code again.
          </p>
          <dl className="grid gap-1 text-sm">
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
        {promote.isError && (
          <Alert tone="error" role="alert" title="We couldn't promote the deployment">
            <p>{errorMessage(promote.error, 'promoteDeployment')}</p>
          </Alert>
        )}
        <div className="flex flex-wrap justify-end gap-2">
          <Button ref={cancelRef} variant="secondary" onClick={onClose} disabled={promote.isPending}>
            Cancel
          </Button>
          <Button onClick={confirm} pending={promote.isPending} pendingLabel="Promoting…">
            Promote to production
          </Button>
        </div>
      </div>
    </ModalDialog>
  )
}
