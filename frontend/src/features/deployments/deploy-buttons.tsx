import { toast } from 'sonner'

import { Button } from '../../components/ui/button'
import { errorMessage } from '../../lib/errors'
import { shortSha } from './deployment-format'
import { useCreatePreviewDeployment, useRedeployDeployment, type Deployment } from './deployments-api'

/**
 * FR-90, FR-91: deploy the same commit again as a new row. The caller decides who sees it (preview: writers;
 * production: Owners); the API enforces it anyway. The outcome is a toast (SPEC §7.1 async results).
 */
export function RedeployButton({
  projectId,
  deployment,
  variant = 'secondary',
}: {
  projectId: string
  deployment: Deployment
  variant?: 'primary' | 'secondary'
}) {
  const redeploy = useRedeployDeployment(projectId)
  const d = deployment
  return (
    <Button
      variant={variant}
      pending={redeploy.isPending}
      pendingLabel="Redeploying…"
      aria-label={`Redeploy ${d.target} ${d.releaseVersion} (${shortSha(d.gitSha)})`}
      onClick={() => {
        redeploy.mutate(d.id, {
          onSuccess: () => {
            toast.success(`Redeploying ${shortSha(d.gitSha)}. The new deployment is queued.`)
          },
          onError: (error) => {
            toast.error(errorMessage(error, 'deploy'))
          },
        })
      }}
    >
      Redeploy
    </Button>
  )
}

/** FR-90: deploy the version branch head as a preview now (writers). */
export function DeployPreviewButton({ projectId, variant = 'secondary' }: { projectId: string; variant?: 'primary' | 'secondary' }) {
  const create = useCreatePreviewDeployment(projectId)
  return (
    <Button
      variant={variant}
      pending={create.isPending}
      pendingLabel="Deploying…"
      onClick={() => {
        create.mutate(undefined, {
          onSuccess: (d) => {
            toast.success(`Preview of ${shortSha(d.gitSha)} queued.`)
          },
          onError: (error) => {
            toast.error(errorMessage(error, 'deploy'))
          },
        })
      }}
    >
      Deploy preview
    </Button>
  )
}
