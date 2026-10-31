import { Link } from '../../lib/navigation'

import { Alert } from '../../components/ui/alert'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { useProjectAccess } from '../projects/project-context'
import { RelativeTime } from '../projects/relative-time'
import type { Release } from '../release/release-api'
import { StageSection } from '../stages/stage-section'
import { DeployPreviewButton, RedeployButton, redeployTargetOf } from './deploy-buttons'
import { componentLabels, deploymentProviderLabels } from './deployment-format'
import { DeploymentFacts, DeploymentLink, DeploymentStatusPill } from './deployment-parts'
import { usePreviewEnvironment, type LiveTarget } from './deployments-api'
import { ResetPreviewDatabase } from './preview-database-reset'
import { liveOnNames, liveTargetsInOrder } from './rollout-format'
import { RolloutStateBadge, RolloutSteps, rolloutFailure } from './rollout-parts'

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
          Set up deployment in Settings → Deployment
        </Link>
      ) : (
        'An Owner can set up deployment in Settings → Deployment.'
      )}
    </p>
  )
}

/**
 * FR-147 on the Review page: the persistent preview environment's current rollout, one row per step (the preview
 * migration run, then backend, then frontend) with state, host, URL, error, build-log tail and Redeploy for
 * Contributors and Owners; the notes for skipped hosts (GitHub Pages); and, when the preview database diverged
 * (FR-148), the reason with "Reset preview database" for Owners. Polls while the rollout runs. A v2 single-Vercel
 * project shows one frontend row.
 */
export function PreviewDeploymentSection() {
  const { project, can } = useProjectAccess()
  const enabled = project.deploymentsEnabled
  const preview = usePreviewEnvironment(project.id, enabled)
  const writer = can('deployPreview')
  const owner = can('manageDeployment')
  const env = preview.data
  const current = env?.current ?? null
  const stepNotes = new Set(current?.steps.map((s) => s.note).filter((n): n is string => n !== null) ?? [])
  const notes = env?.notes.filter((n) => !stepNotes.has(n)) ?? []
  const failure = current ? rolloutFailure(current) : null

  let body
  if (!enabled) {
    body = <NotConfigured owner={owner} projectId={project.id} what="preview deployment" />
  } else if (preview.isPending) {
    body = <LoadingState label="Loading the preview deployment…" />
  } else if (preview.isError) {
    body = <ErrorState error={preview.error} title="The preview deployment couldn't load" onRetry={() => void preview.refetch()} />
  } else {
    body = (
      <div className="flex flex-col gap-3">
        {env?.databaseDiverged && (
          <Alert tone="warning" title="The preview database needs a reset">
            <p data-testid="preview-diverged">
              {env.divergedMessage ?? 'The preview database has migrations this branch no longer has.'} Previews are blocked
              until it&apos;s reset.
              {!owner && ' A project Owner can reset it.'}
            </p>
            {owner && (
              <div className="mt-2">
                <ResetPreviewDatabase projectId={project.id} variant="primary" />
              </div>
            )}
          </Alert>
        )}
        {notes.length > 0 && (
          <ul aria-label="Preview notes" className="flex flex-col gap-1 text-sm">
            {notes.map((n) => (
              <li key={n} data-testid="preview-note">
                {n}
              </li>
            ))}
          </ul>
        )}
        {!current ? (
          <p className="text-sm text-muted-foreground">
            No preview yet. AIDE deploys one when execution opens or updates the pull request.
          </p>
        ) : (
          <>
            <p className="text-sm text-muted-foreground">
              Requested <RelativeTime iso={current.requestedAt} />
            </p>
            {failure && (current.state === 'failed' || current.state === 'blocked') && (
              <Alert tone={current.state === 'blocked' ? 'warning' : 'error'} title={current.state === 'blocked' ? 'The preview is blocked' : 'The preview failed'}>
                <p data-testid="rollout-failure">{failure}</p>
              </Alert>
            )}
            {current.state === 'canceled' && (
              <p className="text-sm text-muted-foreground">A newer preview replaced this one.</p>
            )}
            <RolloutSteps projectId={project.id} rollout={current} mayRedeploy={writer} />
          </>
        )}
      </div>
    )
  }

  return (
    <StageSection
      id="review-preview-deployment"
      title="Preview deployment"
      actions={
        enabled && (
          <span className="flex flex-wrap items-center gap-2">
            {current && <RolloutStateBadge state={current.state} />}
            {writer && preview.isSuccess && current?.state !== 'running' && <DeployPreviewButton projectId={project.id} />}
          </span>
        )
      }
    >
      {body}
    </StageSection>
  )
}

/** FR-149: "Released 0.1.0 · live on Netlify and Render", with each component's production URL. */
function LiveOn({ version, live }: { version: string; live: readonly LiveTarget[] }) {
  return (
    <div data-testid="production-live" className="flex flex-col gap-1">
      <p className="text-base font-medium">
        Released {version} · live on {liveOnNames(live)}
      </p>
      <ul aria-label="Where the release is live" className="flex flex-col gap-1 text-sm">
        {liveTargetsInOrder(live).map((t) => (
          <li key={t.component} className="flex flex-wrap gap-x-2">
            <span className="text-muted-foreground">
              {componentLabels[t.component]} ({t.providerName || deploymentProviderLabels[t.provider]})
            </span>
            <DeploymentLink url={t.url} />
          </li>
        ))}
      </ul>
    </div>
  )
}

/**
 * FR-91, FR-149 on the Release page: the release's production rollout as a timeline (migration run → backend →
 * frontend) with states and URLs, "Released 0.1.0 · live on Netlify and Render" once live, the migration run link and
 * the "Migrations applied, code not merged" warning. A failed deployment doesn't undo the release: it shows the error
 * and, for Owners, Redeploy. A release without a rollout (v2) shows its single production deployment.
 */
export function ProductionDeploymentSection({ release }: { release: Release | null | undefined }) {
  const { project, can } = useProjectAccess()
  const enabled = project.deploymentsEnabled
  const owner = can('manageDeployment')
  const d = release?.productionDeployment ?? null
  const rollout = release?.rollout ?? null
  const live = release?.liveOn ?? []
  const tagVersion = release?.tag ? release.tag.replace(/^v/, '') : null
  const version = d?.releaseVersion ?? tagVersion ?? project.developmentVersion ?? project.releasedVersion ?? ''
  const failedStep = rollout?.steps.some((s) => s.state === 'failed') ?? false

  const migrationNotes = release && (
    <>
      {release.migrationsAppliedNotMerged && (
        <Alert tone="warning" title="Migrations applied, code not merged">
          <p data-testid="migrations-applied-not-merged">
            The production database migrations were applied, but the release wasn&apos;t merged, so production still runs the
            previous code against the new schema. Finish the merge to bring them back in step.
          </p>
        </Alert>
      )}
      {release.migrationRunId && !rollout?.steps.some((s) => s.migrationRunId === release.migrationRunId) && (
        <p className="text-sm">
          <Link
            to="/projects/$projectId/migrations"
            params={{ projectId: project.id }}
            className="font-medium text-primary underline underline-offset-2"
          >
            View the production migration run
          </Link>
        </p>
      )}
    </>
  )

  let body
  if (!enabled && !d && !rollout) {
    body = <NotConfigured owner={owner} projectId={project.id} what="production deployment" />
  } else if (!release) {
    body = <p className="text-sm text-muted-foreground">AIDE deploys production after the release is merged.</p>
  } else if (rollout) {
    body = (
      <div className="flex flex-col gap-3">
        {live.length > 0 && <LiveOn version={version} live={live} />}
        {migrationNotes}
        {(rollout.state === 'failed' || failedStep) && (
          <p className="text-sm">
            The release itself is done: the production rollout failed, and Owners were notified.
            {owner ? ' Fix the cause, then redeploy the failed step.' : ' An Owner can redeploy.'}
          </p>
        )}
        <RolloutSteps projectId={project.id} rollout={rollout} mayRedeploy={owner} />
      </div>
    )
  } else if (!d) {
    body = (
      <div className="flex flex-col gap-3">
        {migrationNotes}
        <p className="text-sm text-muted-foreground">
          {release.status === 'succeeded'
            ? 'The production deployment of the merge commit will show here shortly.'
            : 'AIDE deploys production once the release is merged.'}
        </p>
      </div>
    )
  } else {
    body = (
      <div className="flex flex-col gap-3">
        {d.status === 'ready' &&
          (live.length > 0 ? (
            <LiveOn version={version} live={live} />
          ) : (
            <p className="text-base font-medium" data-testid="production-live">
              Released {version} · live on {deploymentProviderLabels[d.provider]}
              {d.url && (
                <>
                  {' '}
                  at <DeploymentLink url={d.url} />
                </>
              )}
            </p>
          ))}
        {migrationNotes}
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
        rollout ? (
          <RolloutStateBadge state={rollout.state} />
        ) : (
          d && (
            <span className="flex flex-wrap items-center gap-2">
              <DeploymentStatusPill status={d.status} />
              {owner && (d.status === 'error' || d.status === 'canceled') && (
                <RedeployButton projectId={project.id} deployment={redeployTargetOf(d)} variant="primary" />
              )}
            </span>
          )
        )
      }
    >
      {body}
    </StageSection>
  )
}
