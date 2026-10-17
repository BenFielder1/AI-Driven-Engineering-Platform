import type { Schemas } from '../../api/client'
import type { StatusTone } from '../../components/shared/status-badge'
import { copyForCode } from '../../lib/errors'
import { maskedKey } from '../connections/connection-format'
import type { Deployment, DeploymentSettings, DeploymentStatus, DeploymentTarget } from './deployments-api'

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

/** A failed deployment's reason: the recorded code's copy, plus Vercel's message when there is one. */
export function deploymentFailure(d: Pick<Deployment, 'errorCode' | 'errorMessage'>): string | null {
  const copy = copyForCode(d.errorCode)
  const message = d.errorMessage?.trim()
  if (copy && message) {
    return `${copy} ${message}`
  }
  return copy ?? message ?? null
}

/** Only `https://` links from the API are rendered as links (defence in depth). */
export const safeUrl = (url: string | null | undefined): string | null =>
  typeof url === 'string' && url.startsWith('https://') ? url : null

/** FR-88: "Vercel · connected by Ana (••••a1b2) · team acme". */
export function deploymentBindingSummary(s: Pick<DeploymentSettings, 'provider' | 'binding'>): string {
  const provider = s.provider ? deploymentProviderLabels[s.provider] : 'Vercel'
  const b = s.binding
  if (!b) {
    return `${provider} · no connection bound`
  }
  return `${provider} · connected by ${b.boundBy.name} (${maskedKey(b.last4)})${b.teamSlug ? ` · team ${b.teamSlug}` : ''}`
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
