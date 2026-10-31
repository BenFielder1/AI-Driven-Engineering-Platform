import { StatusBadge } from '../../components/shared/status-badge'
import { Alert } from '../../components/ui/alert'
import { withJobDetail } from '../../lib/errors'
import { Link } from '../../lib/navigation'
import { RedeployButton } from './deploy-buttons'
import { deploymentFailure, deploymentProviderLabels, safeUrl, shortSha } from './deployment-format'
import { BuildLogTail, DeploymentLink, DeploymentStatusPill } from './deployment-parts'
import { DeploymentProviderLogo } from './deployment-provider-logo'
import type { Rollout, RolloutState, RolloutStep } from './deployments-api'
import {
  orderedSteps,
  rolloutStateLabels,
  rolloutStateTones,
  rolloutStepLabels,
  rolloutStepStateLabels,
  rolloutStepStateTones,
} from './rollout-format'

/** FR-147, FR-149: a rollout's overall state as a pill; running pulses and is announced politely. */
export function RolloutStateBadge({ state }: { state: RolloutState }) {
  const running = state === 'running'
  return (
    <StatusBadge tone={rolloutStateTones[state]} pulse={running}>
      <span data-testid="rollout-state" role={running ? 'status' : undefined}>
        {rolloutStateLabels[state]}
        {running && <span className="sr-only"> (in progress)</span>}
      </span>
    </StatusBadge>
  )
}

/** A failed step's reason: the shared copy for its code (naming the host) with the recorded message. */
function stepFailure(step: RolloutStep): string | null {
  if (step.state !== 'failed' && !step.errorCode && !step.errorMessage) {
    return null
  }
  if (step.step !== 'migrations' && step.provider) {
    return deploymentFailure({ errorCode: step.errorCode, errorMessage: step.errorMessage, provider: step.provider })
  }
  return withJobDetail(step.errorCode, null, step.errorMessage) ?? step.errorMessage
}

/** A rollout's own error (e.g. blocked by `PREVIEW_DATABASE_DIVERGED`), as copy with its recorded message. */
export function rolloutFailure(rollout: Rollout): string | null {
  if (!rollout.errorCode && !rollout.errorMessage) {
    return null
  }
  return withJobDetail(rollout.errorCode, null, rollout.errorMessage) ?? rollout.errorMessage
}

function StepRow({
  projectId,
  rollout,
  step,
  index,
  mayRedeploy,
}: {
  projectId: string | undefined
  rollout: Rollout
  step: RolloutStep
  index: number
  mayRedeploy: boolean
}) {
  const label = rolloutStepLabels[step.step]
  const failure = stepFailure(step)
  const inspector = safeUrl(step.inspectorUrl)
  const hostName = step.provider ? deploymentProviderLabels[step.provider] : null
  // Production (FR-91, FR-149): Redeploy is the way back from a failed or canceled deployment, not for a live one.
  const redeployable =
    step.canRedeploy && (rollout.environment === 'preview' || step.status === 'error' || step.status === 'canceled')
  return (
    <li data-testid={`rollout-step-${step.step}`} className="flex min-w-0 flex-col gap-2 rounded-lg border border-border/60 p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <span className="font-medium">
            <span className="sr-only">Step {index + 1}: </span>
            {label}
          </span>
          {step.provider && hostName && (
            <span className="inline-flex items-center gap-1 text-sm text-muted-foreground" data-testid="rollout-step-host">
              <DeploymentProviderLogo provider={step.provider} />
              {hostName}
            </span>
          )}
        </div>
        <span className="flex flex-wrap items-center gap-2">
          {step.status ? (
            <DeploymentStatusPill status={step.status} />
          ) : (
            <StatusBadge tone={rolloutStepStateTones[step.state]} pulse={step.state === 'running'}>
              <span data-testid="rollout-step-state">{rolloutStepStateLabels[step.state]}</span>
            </StatusBadge>
          )}
          {projectId && mayRedeploy && redeployable && step.deploymentId && (
            <RedeployButton
              projectId={projectId}
              deployment={{ id: step.deploymentId, gitSha: rollout.gitSha, label: `${rollout.environment} ${step.step}` }}
            />
          )}
        </span>
      </div>
      {step.state === 'skipped' && (
        <p className="text-sm text-muted-foreground" data-testid="rollout-step-note">
          Skipped{step.note ? `: ${step.note}` : '.'}
        </p>
      )}
      {step.state !== 'skipped' && step.note && (
        <p className="text-sm text-muted-foreground" data-testid="rollout-step-note">
          {step.note}
        </p>
      )}
      {step.step !== 'migrations' && step.state !== 'skipped' && (
        <p className="flex flex-wrap gap-x-2 text-sm">
          <span className="text-muted-foreground">URL</span>
          <DeploymentLink url={step.url} />
          {inspector && hostName && (
            <a href={inspector} target="_blank" rel="noreferrer noopener" className="text-primary underline underline-offset-2">
              Build details on {hostName}
              <span className="sr-only"> (opens in a new tab)</span>
            </a>
          )}
        </p>
      )}
      {projectId && step.migrationRunId && (
        <p className="text-sm">
          <Link
            to="/projects/$projectId/migrations"
            params={{ projectId }}
            className="font-medium text-primary underline underline-offset-2"
          >
            View the migration run
          </Link>
        </p>
      )}
      {failure && (
        <Alert tone="error" title={`${label} failed`}>
          <p data-testid="deployment-failure">{failure}</p>
        </Alert>
      )}
      <BuildLogTail log={step.buildLogTail} defaultOpen={step.state === 'failed'} />
    </li>
  )
}

/**
 * FR-147, FR-149: a rollout's steps in dependency order (migrations → backend → frontend): each with its host, state,
 * URL, error, the migration run link and the build-log tail; Redeploy for `mayRedeploy` on a finished preview step or a
 * failed or canceled production step.
 * Without `projectId` (a review report's snapshot) it is read-only.
 */
export function RolloutSteps({
  projectId,
  rollout,
  mayRedeploy = false,
}: {
  projectId?: string
  rollout: Rollout
  mayRedeploy?: boolean
}) {
  const steps = orderedSteps(rollout.steps)
  return (
    <div className="flex flex-col gap-2">
      <p className="text-sm text-muted-foreground">
        Commit{' '}
        <code className="font-mono text-xs" title={rollout.gitSha}>
          {shortSha(rollout.gitSha)}
        </code>{' '}
        on {rollout.gitRef}
      </p>
      {steps.length === 0 ? (
        <p className="text-sm text-muted-foreground">No steps yet.</p>
      ) : (
        <ol aria-label={`${rollout.environment === 'production' ? 'Production' : 'Preview'} rollout steps`} className="flex flex-col gap-2">
          {steps.map((step, i) => (
            <StepRow key={step.step} projectId={projectId} rollout={rollout} step={step} index={i} mayRedeploy={mayRedeploy} />
          ))}
        </ol>
      )}
    </div>
  )
}
