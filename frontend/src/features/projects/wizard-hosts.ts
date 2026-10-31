import type { Schemas } from '../../api/client'
import { databaseProviderLabel } from '../deployments/database-format'
import { deploymentProviderLabels } from '../deployments/deployment-format'
import type { HostSetupValues } from '../deployments/host-setup-body'
import { HOST_SKIP, hostField, type HostFieldName, type HostSlot } from './wizard-host-fields'

/** The stack fields a host comes from, and the host fields themselves (structural, so `schemas.ts` can use this). */
export type HostFormValues = Record<HostFieldName, string> & {
  stackKind: string
  stackFrontendHost: string
  stackBackendHost: string
  stackDatabaseHost: string
}

export type WizardHost =
  | { kind: 'target'; slot: HostSlot; component: Schemas['DeploymentComponent']; provider: Schemas['DeploymentProvider']; title: string; needsConnection: boolean }
  | { kind: 'database'; slot: HostSlot; environment: Schemas['DeploymentEnvironment']; provider: Schemas['ManagedDatabaseProvider']; title: string; needsConnection: boolean }

const targetProviders: Readonly<Record<string, Schemas['DeploymentProvider']>> = {
  'deploy-vercel': 'vercel',
  'deploy-netlify': 'netlify',
  'deploy-cloudflare': 'cloudflare',
  'deploy-github-pages': 'github_pages',
  'deploy-render': 'render',
  'deploy-railway': 'railway',
}

const databaseProviders: Readonly<Record<string, Schemas['ManagedDatabaseProvider']>> = {
  supabase: 'supabase',
  'render-postgres': 'render',
  'railway-postgres': 'railway',
  external: 'external',
}

/** §7: the hosts the Stack step chose: a target per deployed component, and a database per environment. */
export function wizardHosts(v: Pick<HostFormValues, 'stackKind' | 'stackFrontendHost' | 'stackBackendHost' | 'stackDatabaseHost'>): WizardHost[] {
  if (v.stackKind !== 'compose') {
    return []
  }
  const hosts: WizardHost[] = []
  const target = (slot: HostSlot, component: Schemas['DeploymentComponent'], label: string, option: string) => {
    const provider = targetProviders[option]
    if (provider) {
      hosts.push({ kind: 'target', slot, component, provider, title: `${label} on ${deploymentProviderLabels[provider]}`, needsConnection: provider !== 'github_pages' })
    }
  }
  target('Frontend', 'frontend', 'Frontend', v.stackFrontendHost)
  target('Backend', 'backend', 'Backend', v.stackBackendHost)
  const database = databaseProviders[v.stackDatabaseHost]
  if (database) {
    const db = (slot: HostSlot, environment: Schemas['DeploymentEnvironment'], label: string) => {
      hosts.push({ kind: 'database', slot, environment, provider: database, title: `${label} database on ${databaseProviderLabel(database)}`, needsConnection: database !== 'external' })
    }
    db('DbProduction', 'production', 'Production')
    // Railway's preview goes in the production service's project, once that exists: Settings → Deployment sets it up then.
    if (database !== 'railway') {
      db('DbPreview', 'preview', 'Preview')
    }
  }
  return hosts
}

export const isHostChosen = (v: Record<HostFieldName, string>, slot: HostSlot) => v[hostField(slot, 'Mode')] !== HOST_SKIP

export const hostValues = (v: Record<HostFieldName, string>, slot: HostSlot): HostSetupValues => ({
  connectionId: v[hostField(slot, 'ConnectionId')],
  mode: v[hostField(slot, 'Mode')],
  name: v[hostField(slot, 'Name')],
  region: v[hostField(slot, 'Region')],
  plan: v[hostField(slot, 'Plan')],
  resourceId: v[hostField(slot, 'ResourceId')],
  connectionString: v[hostField(slot, 'ConnectionString')],
})

/** The stack choice each host slot follows: a new host leaves nothing of the old one's setup behind. */
export const hostSlotsOfChoice: Readonly<Record<string, readonly HostSlot[]>> = {
  frontendHost: ['Frontend'],
  backendHost: ['Backend'],
  databaseHost: ['DbProduction', 'DbPreview'],
}

/** FR-152: a `postgres://` or `postgresql://` URI, or an ADO.NET string (`Key=value;…`) for SQL Server. */
const CONNECTION_STRING = /^(postgres(ql)?:\/\/\S+|[A-Za-z ]+=[^;]*(;[A-Za-z ]+=[^;]*)*;?)$/

const NOT_SEPARATE = 'The preview and production databases must be separate.'

/**
 * The Deployment step's rules for the chosen hosts, matching the API (FR-144, FR-152). The API still has the last word:
 * what only it can refuse is reported after the project is created, pointing to Settings → Deployment.
 */
export function hostChecks(v: HostFormValues): [HostFieldName, string][] {
  const issues: [HostFieldName, string][] = []
  const hosts = wizardHosts(v)
  for (const host of hosts) {
    if (!isHostChosen(v, host.slot)) {
      continue
    }
    const values = hostValues(v, host.slot)
    const issue = (field: Parameters<typeof hostField>[1], message: string) => {
      issues.push([hostField(host.slot, field), message])
    }
    if (host.provider === 'external') {
      const cs = values.connectionString.trim()
      if (!cs) {
        issue('ConnectionString', 'Enter the connection string.')
      } else if (!CONNECTION_STRING.test(cs)) {
        issue('ConnectionString', 'Enter a postgres:// or postgresql:// URI, or an ADO.NET connection string (Server=…;Database=…).')
      }
      continue
    }
    if (!host.needsConnection) {
      continue
    }
    const platform = host.kind === 'target' ? deploymentProviderLabels[host.provider] : databaseProviderLabel(host.provider).replace(' Postgres', '')
    if (!values.connectionId) {
      issue('ConnectionId', `Pick one of your ${platform} connections, or set this up later.`)
    }
    if (values.mode === 'link' && !values.resourceId.trim()) {
      issue('ResourceId', 'Pick what to link, or set this up later.')
    }
    if (values.mode === 'create') {
      const name = values.name.trim()
      if (host.kind === 'target' && !name) {
        issue('Name', 'Enter a name, or set this up later.')
      } else if (name.length > (host.kind === 'target' ? 100 : 60)) {
        issue('Name', `Names are at most ${host.kind === 'target' ? '100' : '60'} characters.`)
      }
      if (host.provider === 'supabase' && !values.region.trim()) {
        issue('Region', 'Enter a region code (e.g. eu-west-2), or americas, emea or apac.')
      }
    }
    // §4.21: Railway Postgres goes in the app's Railway project, so a Railway target must be set up with it.
    if (host.kind === 'database' && host.provider === 'railway' && !hosts.some((h) => h.kind === 'target' && h.provider === 'railway' && isHostChosen(v, h.slot))) {
      issue('Mode', 'Railway Postgres goes in the Railway project of a target: set one up here too, or set this up later.')
    }
  }
  // §4.21: the same resource or connection string for both environments is refused.
  const production = hostValues(v, 'DbProduction')
  const preview = hostValues(v, 'DbPreview')
  if (hosts.some((h) => h.slot === 'DbPreview') && isHostChosen(v, 'DbProduction') && isHostChosen(v, 'DbPreview')) {
    if (production.mode === 'link' && preview.mode === 'link' && production.resourceId.trim() && production.resourceId.trim() === preview.resourceId.trim()) {
      issues.push([hostField('DbPreview', 'ResourceId'), NOT_SEPARATE])
    }
    if (production.connectionString.trim() && production.connectionString.trim() === preview.connectionString.trim()) {
      issues.push([hostField('DbPreview', 'ConnectionString'), NOT_SEPARATE])
    }
  }
  return issues
}
