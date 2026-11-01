import { toast } from 'sonner'

import { Button } from '../../components/ui/button'
import { errorMessage, isApiError } from '../../lib/errors'
import { useNavigate } from '../../lib/navigation'
import { useProjectAccess } from '../projects/project-context'
import { shortSha } from './deployment-format'
import { useCreatePreviewDeployment, useRedeployDeployment, type Deployment } from './deployments-api'

/** What Redeploy acts on: a deployment row, or a rollout step's deployment (FR-147, FR-149). */
export interface RedeployTarget {
  id: string
  gitSha: string
  /** Names it in the button's accessible name, e.g. "preview 1.2.0" or "preview backend". */
  label: string
}

/** A deployment row as a Redeploy target: "Redeploy preview 1.2.0 (3f2a9c1)". */
export const redeployTargetOf = (d: Deployment): RedeployTarget => ({ id: d.id, gitSha: d.gitSha, label: `${d.target} ${d.releaseVersion}` })

/**
 * FR-90, FR-91: deploy the same commit again as a new row. The caller decides who sees it (preview: writers;
 * production: Owners); the API enforces it anyway. The outcome is a toast (SPEC §7.1 async results).
 */
export function RedeployButton({
  projectId,
  deployment: d,
  variant = 'secondary',
}: {
  projectId: string
  deployment: RedeployTarget
  variant?: 'primary' | 'secondary'
}) {
  const redeploy = useRedeployDeployment(projectId)
  return (
    <Button
      variant={variant}
      pending={redeploy.isPending}
      pendingLabel="Redeploying…"
      aria-label={`Redeploy ${d.label} (${shortSha(d.gitSha)})`}
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

/**
 * FR-147: deploy the version branch head as a preview rollout now (writers). A diverged preview database (FR-148,
 * `PREVIEW_DATABASE_DIVERGED`) blocks it: the toast says so and, for Owners, links to the reset on the database card.
 */
export function DeployPreviewButton({ projectId, variant = 'secondary' }: { projectId: string; variant?: 'primary' | 'secondary' }) {
  const create = useCreatePreviewDeployment(projectId)
  const { can } = useProjectAccess()
  const navigate = useNavigate()
  const owner = can('manageDeployment')
  return (
    <Button
      variant={variant}
      pending={create.isPending}
      pendingLabel="Deploying…"
      onClick={() => {
        create.mutate(undefined, {
          onSuccess: (rollout) => {
            toast.success(`Preview of ${shortSha(rollout.gitSha)} queued.`)
          },
          onError: (error) => {
            const diverged = isApiError(error, 'PREVIEW_DATABASE_DIVERGED')
            toast.error(errorMessage(error, 'deploy'), {
              ...(diverged && owner
                ? {
                    action: {
                      label: 'Reset preview database',
                      onClick: () => {
                        void navigate({ to: '/projects/$projectId/settings/$section', params: { projectId, section: 'deployment' } })
                      },
                    },
                  }
                : {}),
            })
          },
        })
      }}
    >
      Deploy preview
    </Button>
  )
}
