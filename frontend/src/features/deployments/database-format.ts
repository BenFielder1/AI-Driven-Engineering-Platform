import type { StatusTone } from '../../components/shared/status-badge'
import type { PlatformResourceType } from '../connections/platform-connections-api'
import { formatDateTime } from '../projects/labels'
import { deploymentTargetLabels } from './deployment-format'
import type { DatabaseCard, DeploymentTarget } from './deployments-api'

/** FR-152: a database card's provider (`DeploymentDatabaseDto.provider`), by name. */
export const databaseProviderLabels: Readonly<Record<string, string>> = {
  supabase: 'Supabase',
  render: 'Render Postgres',
  railway: 'Railway Postgres',
  external: 'External database',
  fake: 'Fake database',
}

export const databaseProviderLabel = (provider: string) => databaseProviderLabels[provider] ?? provider

/** The platform's name, for "your Render connections" (Supabase, Render, Railway). */
export const databasePlatformNames: Readonly<Record<string, string>> = {
  supabase: 'Supabase',
  render: 'Render',
  railway: 'Railway',
}

/** The resource a provider provisions or links ("Create a new Supabase project"). */
export const databaseResourceNouns: Readonly<Record<string, string>> = {
  supabase: 'project',
  render: 'database',
  railway: 'Postgres service',
  fake: 'database',
}

/** The connection's picker for Link (FR-142 `GET …/resources`); `null`: the Owner types the id. */
export const databasePickerTypes: Readonly<Record<string, PlatformResourceType | null>> = {
  supabase: 'projects',
  render: null,
  railway: null,
  fake: null,
}

/** The id to type for Link when there's no picker. */
export const databaseLinkIdLabels: Readonly<Record<string, string>> = {
  render: 'Render Postgres id (dpg-…)',
  railway: 'Railway Postgres service id',
  fake: 'Database id',
}

/** §4.21 database states, in words (never colour alone). */
export const databaseStatusLabels: Readonly<Record<string, string>> = {
  not_configured: 'Not set up',
  provisioning: 'Provisioning',
  available: 'Available',
  error: 'Error',
  paused: 'Paused',
  expiring: 'Expiring',
  removed: 'Removed',
}

export const databaseStatusTones: Readonly<Record<string, StatusTone>> = {
  not_configured: 'neutral',
  provisioning: 'info',
  available: 'success',
  error: 'destructive',
  paused: 'warning',
  expiring: 'warning',
  removed: 'neutral',
}

export const databaseStatusLabel = (status: string) => databaseStatusLabels[status] ?? status

/** The `database_provisioning` job's steps (FR-152). */
export const provisioningStepLabels: Readonly<Record<string, string>> = {
  queued: 'Waiting to start',
  creating: 'Creating the database',
  linking: 'Linking the database',
  waiting: 'Waiting for it to become available',
  syncing_env: 'Syncing environment variables',
}

export const isProvisioningActive = (db: Pick<DatabaseCard, 'provisioning'>) =>
  db.provisioning?.state === 'queued' || db.provisioning?.state === 'running'

/** The environment's name, for "Production database". */
export const environmentLabels: Record<DeploymentTarget, string> = deploymentTargetLabels

/** FR-152 warnings, as text. `expiresAt` dates the Render free expiry. */
export function databaseWarningCopy(warning: string, db: Pick<DatabaseCard, 'expiresAt'>): string {
  switch (warning) {
    case 'render_free_expiry':
      return db.expiresAt
        ? `Render's free Postgres expires on ${formatDateTime(db.expiresAt)}, then has a 14-day grace period before Render deletes it. Upgrade its plan in Render to keep it.`
        : "Render's free Postgres expires 30 days after it's created, then has a 14-day grace period before Render deletes it. Upgrade its plan in Render to keep it."
    case 'supabase_free_pausing':
      return 'Supabase pauses free-plan projects after 7 days of inactivity. A paused database can be restored from this card.'
    case 'supabase_free_project_limit':
      return "Supabase's free plan allows 2 active projects per organisation. Link an existing project instead, or upgrade the organisation's plan."
    case 'preview_uses_production_database':
      return 'Previews use the production database, so preview deployments can change production data. Set up a separate preview database to avoid that.'
    default:
      return warning
  }
}
