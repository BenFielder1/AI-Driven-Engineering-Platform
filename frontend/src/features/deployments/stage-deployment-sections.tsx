import { Link } from '../../lib/navigation'

import { ErrorState, LoadingState } from '../../components/ui/states'
import { useProjectAccess } from '../projects/project-context'
import type { Release } from '../release/release-api'
import { StageSection } from '../stages/stage-section'
import { DeployPreviewButton, RedeployButton } from './deploy-buttons'
import { DeploymentFacts, DeploymentLink, DeploymentStatusPill } from './deployment-parts'
import { useDeployments, type Deployment } from './deployments-api'

function NotConfigured({ owner, projectId, what }: { owner: boolean; projectId: string; what: string }) {
  return (
    <p className="text-sm text-muted-foreground" data-testid="deployment-not-configured">
      Deployment isn&apos;t set up, so there is no {what}.{' '}
      {owner ? (
        <Link
          to="/projects/$projectId/settings/$section"
          params={{ projectId, section: 'deployment' }}
          className="font-medium text-primary underline underline-offset-2"
        >
          Set up Vercel in Settings → Deployment
        </Link>
      ) : (
        'An Owner can set up Vercel in Settings → Deployment.'
      )}
    </p>
  )
}

/**
 * FR-90 on the Review page: the newest preview deployment of the version branch (URL, a live state pill, the build-log
 * tail) with Redeploy for Contributors and Owners. Review waits for it to be ready or failed before scoring.
 */
export function PreviewDeploymentSection() {
  const { project, can } = useProjectAccess()
  const enabled = project.deploymentsEnabled
  const previews = useDeployments(
    project.id,
    { target: 'preview', releaseVersionId: project.releaseVersionId ?? undefined },
    enabled,
  )
  const writer = can('deployPreview')
  const latest: Deployment | undefined = previews.data?.[0]

  let body
  if (!enabled) {
    body = <NotConfigured owner={can('manageDeployment')} projectId={project.id} what="preview deployment" />
  } else if (previews.isPending) {
    body = <LoadingState label="Loading the preview deployment…" />
  } else if (previews.isError) {
    body = <ErrorState error={previews.error} title="The preview deployment couldn't load" onRetry={() => void previews.refetch()} />
  } else if (!latest) {
    body = (
      <p className="text-sm text-muted-foreground">
        No preview yet. AIDE deploys one when execution opens or updates the pull request.
      </p>
    )
  } else {
    body = <DeploymentFacts deployment={latest} />
  }

  return (
    <StageSection
      id="review-preview-deployment"
      title="Preview deployment"
      actions={
        enabled && (
          <span className="flex flex-wrap items-center gap-2">
            {latest && <DeploymentStatusPill status={latest.status} />}
            {writer && latest && latest.status !== 'queued' && latest.status !== 'building' && (
              <RedeployButton projectId={project.id} deployment={latest} />
            )}
            {writer && previews.isSuccess && !latest && <DeployPreviewButton projectId={project.id} />}
          </span>
        )
      }
    >
      {body}
    </StageSection>
  )
}

/**
 * FR-91 on the Release page: the release's production deployment (state, URL; "Released 1.2.0 · live on Vercel"
 * once ready). A failed one doesn't undo the release: it shows the error and, for Owners, Redeploy.
 */
export function ProductionDeploymentSection({ release }: { release: Release | null | undefined }) {
  const { project, can } = useProjectAccess()
  const enabled = project.deploymentsEnabled
  const owner = can('manageDeployment')
  const d = release?.productionDeployment ?? null
  const version = d?.releaseVersion ?? project.developmentVersion ?? project.releasedVersion ?? ''

  let body
  if (!enabled && !d) {
    body = <NotConfigured owner={owner} projectId={project.id} what="production deployment" />
  } else if (!release) {
    body = <p className="text-sm text-muted-foreground">AIDE deploys production after the release is merged.</p>
  } else if (!d) {
    body = (
      <p className="text-sm text-muted-foreground">
        {release.status === 'succeeded'
          ? 'The production deployment of the merge commit will show here shortly.'
          : 'AIDE deploys production once the release is merged.'}
      </p>
    )
  } else {
    body = (
      <div className="flex flex-col gap-3">
        {d.status === 'ready' && (
          <p className="text-base font-medium" data-testid="production-live">
            Released {version} · live on Vercel
            {d.url && (
              <>
                {' '}
                at <DeploymentLink url={d.url} />
              </>
            )}
          </p>
        )}
        {d.status === 'error' && (
          <p className="text-sm">
            The release itself is done: the production deployment failed, and Owners were notified.
            {owner ? ' Fix the cause, then redeploy.' : ' An Owner can redeploy.'}
          </p>
        )}
        <DeploymentFacts deployment={d} />
      </div>
    )
  }

  return (
    <StageSection
      id="release-production-deployment"
      title="Production deployment"
      actions={
        d && (
          <span className="flex flex-wrap items-center gap-2">
            <DeploymentStatusPill status={d.status} />
            {owner && (d.status === 'error' || d.status === 'canceled') && (
              <RedeployButton projectId={project.id} deployment={d} variant="primary" />
            )}
          </span>
        )
      }
    >
      {body}
    </StageSection>
  )
}
