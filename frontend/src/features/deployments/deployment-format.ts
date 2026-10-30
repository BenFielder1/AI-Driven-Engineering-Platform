import type { Schemas } from '../../api/client'
import type { StatusTone } from '../../components/shared/status-badge'
import { platformCopy } from '../../lib/errors'
import { maskedKey } from '../connections/connection-format'
import type { PlatformResourceType } from '../connections/platform-connections-api'
import type {
  Deployment,
  DeploymentComponent,
  DeploymentStatus,
  DeploymentTarget,
  ReadinessState,
  TargetBinding,
  TargetCard,
  TargetStatus,
} from './deployments-api'

export const deploymentStatusLabels: Record<DeploymentStatus, string> = {
  queued: 'Queued',
  building: 'Building',
  ready: 'Ready',
  error: 'Error',
  canceled: 'Canceled',
}

export const deploymentStatusTones: Record<DeploymentStatus, StatusTone> = {
  queued: 'info',
  building: 'info',
  ready: 'success',
  error: 'destructive',
  canceled: 'neutral',
}

export const deploymentTargetLabels: Record<DeploymentTarget, string> = {
  preview: 'Preview',
  production: 'Production',
}

export const deploymentTriggerLabels: Record<Schemas['DeploymentTrigger'], string> = {
  auto: 'AIDE',
  manual: 'Manual',
  promote: 'Promote',
  redeploy: 'Redeploy',
}

export const deploymentProviderLabels: Record<Schemas['DeploymentProvider'], string> = {
  vercel: 'Vercel',
  netlify: 'Netlify',
  cloudflare: 'Cloudflare',
  render: 'Render',
  railway: 'Railway',
  github_pages: 'GitHub Pages',
  fake: 'Fake (testing)',
}

/** The first 7 characters of a commit SHA. */
export const shortSha = (sha: string) => sha.slice(0, 7)

/** "Ana (Redeploy)", "AIDE" for automatic deployments, "Deleted user (Manual)" when the user is gone. */
export function triggeredByLabel(d: Pick<Deployment, 'trigger' | 'triggeredBy'>): string {
  if (d.trigger === 'auto') {
    return 'AIDE'
  }
  const who = d.triggeredBy?.name ?? 'Deleted user'
  return `${who} (${deploymentTriggerLabels[d.trigger]})`
}

/** A failed deployment's reason: the recorded code's copy (naming its platform), plus the platform's message when there is one. */
export function deploymentFailure(d: Pick<Deployment, 'errorCode' | 'errorMessage' | 'provider'>): string | null {
  const copy = platformCopy(d.errorCode, d.provider)
  const message = d.errorMessage?.trim()
  if (copy && message) {
    return `${copy} ${message}`
  }
  return copy ?? message ?? null
}

/** Only `https://` links from the API are rendered as links (defence in depth). */
export const safeUrl = (url: string | null | undefined): string | null =>
  typeof url === 'string' && url.startsWith('https://') ? url : null

/** FR-144: "Frontend", "Backend". */
export const componentLabels: Record<DeploymentComponent, string> = {
  frontend: 'Frontend',
  backend: 'Backend',
}

/** FR-144 target states, as text (never colour only). */
export const targetStatusLabels: Record<TargetStatus, string> = {
  not_configured: 'Not set up',
  configuring: 'Setting up',
  ready: 'Ready',
  attention: 'Needs attention',
  error: 'Error',
}

export const targetStatusTones: Record<TargetStatus, StatusTone> = {
  not_configured: 'neutral',
  configuring: 'info',
  ready: 'success',
  attention: 'warning',
  error: 'destructive',
}

/** FR-144 readiness item states: the word is always shown next to the message. */
export const readinessStateLabels: Record<ReadinessState, string> = {
  ok: 'OK',
  warning: 'Warning',
  error: 'Problem',
  na: 'Not applicable',
}

export const readinessStateTones: Record<ReadinessState, StatusTone> = {
  ok: 'success',
  warning: 'warning',
  error: 'destructive',
  na: 'neutral',
}

/** FR-144: what each readiness item checks (`id` from `POST …/verify`). */
export const readinessItemLabels: Readonly<Record<string, string>> = {
  connection: 'Connection',
  git_access: 'Repository access',
  required_env: 'Required variables',
  branch_control: 'AIDE branches not auto-deployed',
  preview_environment: 'Preview environment',
  plan_notes: 'Plan limits',
}

/** The `deployment_setup` job's steps (FR-144), in order. */
export const setupSteps = ['queued', 'creating', 'linking', 'syncing_env'] as const

export const setupStepLabels: Record<(typeof setupSteps)[number], string> = {
  queued: 'Waiting to start',
  creating: 'Creating the resource',
  linking: 'Linking the repository',
  syncing_env: 'Syncing environment variables',
}

/** The setup job is still working: the settings query polls (FR-144). */
export const isSetupActive = (t: Pick<TargetCard, 'setup'>) => t.setup?.state === 'queued' || t.setup?.state === 'running'

/** The resource word a platform uses (for "Create a new Netlify site"). */
export const resourceNouns: Record<Schemas['DeploymentProvider'], string> = {
  vercel: 'project',
  netlify: 'site',
  cloudflare: 'Worker',
  render: 'service',
  railway: 'project',
  github_pages: 'site',
  fake: 'project',
}

/** FR-142 picker `type` for linking an existing resource (`GET …/platforms/:id/resources?type=`); `null`: no picker. */
export const resourcePickerTypes: Record<Schemas['DeploymentProvider'], PlatformResourceType | null> = {
  vercel: 'projects',
  netlify: 'sites',
  cloudflare: 'workers',
  render: 'services',
  railway: 'projects',
  github_pages: null,
  fake: null,
}

/**
 * §4.20 capabilities as text, one line each, so a missing capability is said rather than only coloured
 * ("No previews on GitHub Pages").
 */
export function capabilityLines(t: Pick<TargetCard, 'capabilities' | 'provider'>): string[] {
  const name = deploymentProviderLabels[t.provider]
  const c = t.capabilities
  return [
    c.previews ? 'Preview deployments' : `No previews on ${name}`,
    c.rollback ? 'Rollback to an earlier deployment' : `No rollback on ${name}`,
    c.shaPinned ? 'Deploys the exact commit AIDE asks for' : `${name} deploys the branch head, not a pinned commit`,
    c.perEnvironmentVariables ? 'Separate variables for preview and production' : 'The same variables for preview and production',
    c.runtimeSecrets ? 'Runtime secrets' : 'No runtime secrets: variables are built into the app',
  ]
}

/** FR-144: "Acme team (••••ab12) · account acme · connected by Ana". */
export function targetBindingSummary(b: TargetBinding): string {
  return `${b.label} (${maskedKey(b.last4)})${b.accountName ? ` · account ${b.accountName}` : ''} · connected by ${b.boundBy.name}`
}

/** FR-146 origin models, and the stack-packs vocabulary. */
export const originModelLabels: Readonly<Record<string, string>> = {
  'same-origin': 'Same origin',
  proxy: 'Proxy',
  'cross-origin': 'Cross-origin',
}

export const originModelLabel = (model: string | null) => (model ? (originModelLabels[model] ?? model) : 'None (API only)')

/** FR-145 sources. */
export const envSourceLabels: Record<Schemas['DeploymentEnvSource'], string> = {
  user: 'User',
  derived: 'Derived',
  required: 'Required · missing',
}

export const envSourceTones: Record<Schemas['DeploymentEnvSource'], StatusTone> = {
  user: 'neutral',
  derived: 'info',
  required: 'warning',
}

/** The production deployment Vercel serves now: the newest ready production row (a promote is a new row). */
export function liveProduction(deployments: readonly Deployment[]): Deployment | undefined {
  return deployments
    .filter((d) => d.target === 'production' && d.status === 'ready')
    .reduce<Deployment | undefined>((best, d) => (!best || d.createdAt > best.createdAt ? d : best), undefined)
}

/** FR-91: only a ready production deployment that isn't the live one can be promoted. */
export const canPromote = (d: Deployment, live: Deployment | undefined) =>
  d.target === 'production' && d.status === 'ready' && d.id !== live?.id
