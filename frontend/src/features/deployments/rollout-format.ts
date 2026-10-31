import type { StatusTone } from '../../components/shared/status-badge'
import { deploymentProviderLabels } from './deployment-format'
import type { LiveTarget, PreviewResetMethod, RolloutState, RolloutStep, RolloutStepState } from './deployments-api'

/** FR-147, FR-149: a rollout's overall state, as text (never colour only). */
export const rolloutStateLabels: Record<RolloutState, string> = {
  running: 'Rolling out',
  succeeded: 'Succeeded',
  failed: 'Failed',
  blocked: 'Blocked',
  canceled: 'Canceled',
}

export const rolloutStateTones: Record<RolloutState, StatusTone> = {
  running: 'info',
  succeeded: 'success',
  failed: 'destructive',
  blocked: 'warning',
  canceled: 'neutral',
}

export const rolloutStepStateLabels: Record<RolloutStepState, string> = {
  pending: 'Waiting',
  running: 'In progress',
  succeeded: 'Succeeded',
  failed: 'Failed',
  skipped: 'Skipped',
}

export const rolloutStepStateTones: Record<RolloutStepState, StatusTone> = {
  pending: 'neutral',
  running: 'info',
  succeeded: 'success',
  failed: 'destructive',
  skipped: 'neutral',
}

export const rolloutStepLabels: Record<RolloutStep['step'], string> = {
  migrations: 'Database migrations',
  backend: 'Backend',
  frontend: 'Frontend',
}

const stepOrder: Record<RolloutStep['step'], number> = { migrations: 0, backend: 1, frontend: 2 }

/** The steps in dependency order (migrations → backend → frontend), whatever order they arrive in. */
export const orderedSteps = (steps: readonly RolloutStep[]): RolloutStep[] =>
  [...steps].sort((a, b) => stepOrder[a.step] - stepOrder[b.step])

/** "Netlify", "Netlify and Render", "Vercel, Render and Railway": each host once, in order. */
export function joinNames(names: readonly string[]): string {
  const unique = [...new Set(names)]
  if (unique.length <= 1) {
    return unique[0] ?? ''
  }
  return `${unique.slice(0, -1).join(', ')} and ${unique.at(-1) ?? ''}`
}

/** FR-149: where a release is live, frontend first (as the user meets it): "live on Netlify and Render". */
export const liveTargetsInOrder = (live: readonly LiveTarget[]): LiveTarget[] =>
  [...live].sort((a, b) => (a.component === b.component ? 0 : a.component === 'frontend' ? -1 : 1))

/** FR-149: the host names a release is live on ("Netlify and Render"). */
export const liveOnNames = (live: readonly LiveTarget[]) =>
  joinNames(liveTargetsInOrder(live).map((t) => t.providerName || deploymentProviderLabels[t.provider]))

/** FR-148: what happens after the reset was queued, per method. */
export const previewResetCopy: Record<PreviewResetMethod, string> = {
  reset_schema:
    "AIDE is emptying the preview database's schema. The next preview deployment re-applies every migration from the branch.",
  reset_schema_external:
    "AIDE is emptying the preview database's schema over its external connection. The next preview deployment re-applies every migration from the branch.",
  manual:
    "The reset is recorded, but AIDE can't empty a Railway database itself. Empty the preview Postgres in Railway (drop its schema or re-create it), then deploy a preview: its pre-deploy re-applies every migration.",
  none: 'There was nothing to reset.',
}

/** The host's name as review findings use it (the backend's `DeploymentPlatforms.DisplayName`). */
const findingHostName = (provider: NonNullable<RolloutStep['provider']>): string =>
  provider === 'fake' ? 'Fake' : deploymentProviderLabels[provider]

/** Mirrors the backend's `RolloutRules.IsPreDeployFailure`: a backend step whose platform-run migration failed. */
function isPreDeployFailure(step: RolloutStep): boolean {
  if (step.step !== 'backend') {
    return false
  }
  const message = step.errorMessage ?? ''
  return (
    step.errorCode === 'MIGRATION_FAILED' ||
    /pre_deploy_failed|pre-deploy/i.test(message) ||
    message.includes('.aide-fake/fail-migration')
  )
}

/**
 * FR-90, FR-147: the titles of a preview rollout's failed builds, as the report's critical findings name them: a failed
 * build is "{Host} preview build failed" (v2's "Vercel preview build failed"), a failed backend pre-deploy is "Backend
 * preview pre-deploy migration failed" (RolloutFindings on the backend).
 */
export function previewBuildFailureTitles(steps: readonly RolloutStep[]): string[] {
  return orderedSteps(steps).flatMap((s) => {
    if (s.step === 'migrations' || s.status !== 'error' || !s.provider) {
      return []
    }
    return [isPreDeployFailure(s) ? 'Backend preview pre-deploy migration failed' : `${findingHostName(s.provider)} preview build failed`]
  })
}
