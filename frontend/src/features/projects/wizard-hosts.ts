import type { Schemas } from '../../api/client'
import { databaseProviderLabel, databaseProviderLabels } from '../deployments/database-format'
import { deploymentProviderLabels } from '../deployments/deployment-format'
import { deploymentPartsTitle } from '../deployments/deployment-group-format'
import { groupChecks, type GroupSetupValues, type HostSetupValues } from '../deployments/host-setup-body'
import { HOST_SKIP, hostField, hostGroupLinkFieldOf, type HostFieldName, type HostSlot } from './wizard-host-fields'

/** The stack field the windows come from, and the host fields themselves (structural, so `schemas.ts` can use this). */
export type HostFormValues = Record<HostFieldName, string> & {
  stackKind: string
  hostWindows: string
}

type Part = Schemas['DeploymentPart']
type Capability = Schemas['SharedProjectCapability']

export type WizardHost =
  | { kind: 'group'; slot: 'Group'; provider: Schemas['DeploymentProvider']; capability: Exclude<Capability, 'none'>; parts: Part[]; title: string; needsConnection: true }
  | { kind: 'target'; slot: HostSlot; component: Schemas['DeploymentComponent']; provider: Schemas['DeploymentProvider']; title: string; needsConnection: boolean }
  | {
      kind: 'database'
      slot: HostSlot
      environment: Schemas['DeploymentEnvironment']
      provider: Schemas['ManagedDatabaseProvider']
      title: string
      needsConnection: boolean
      /** One window for a lone Railway database, which has no window per environment: its preview waits for Settings. */
      previewInSettings: boolean
    }

type Window = Schemas['StackDeploymentWindowDto']

/** The `windows[]` kept in the form, or none when they aren't known yet. */
function windowsOf(json: string): Window[] {
  if (!json) {
    return []
  }
  try {
    const parsed: unknown = JSON.parse(json)
    return Array.isArray(parsed) ? (parsed as Window[]) : []
  } catch {
    return []
  }
}

const isDeploymentProvider = (id: string): id is Schemas['DeploymentProvider'] => id in deploymentProviderLabels
const isDatabaseProvider = (id: string): id is Schemas['ManagedDatabaseProvider'] => id in databaseProviderLabels

function hostOfWindow(w: Window): WizardHost | null {
  const [part] = w.parts
  if (w.capability !== 'none' && w.parts.length > 1 && isDeploymentProvider(w.platform)) {
    return {
      kind: 'group',
      slot: 'Group',
      provider: w.platform,
      capability: w.capability,
      parts: w.parts,
      title: deploymentPartsTitle(w.parts, deploymentProviderLabels[w.platform]),
      needsConnection: true,
    }
  }
  if ((part === 'frontend' || part === 'backend') && isDeploymentProvider(w.platform)) {
    return {
      kind: 'target',
      slot: part === 'frontend' ? 'Frontend' : 'Backend',
      component: part,
      provider: w.platform,
      title: deploymentPartsTitle([part], deploymentProviderLabels[w.platform]),
      needsConnection: w.platform !== 'github_pages',
    }
  }
  if (part === 'database' && isDatabaseProvider(w.platform)) {
    const environment = w.environment ?? 'production'
    const label = w.environment === null ? 'Database' : environment === 'production' ? 'Production database' : 'Preview database'
    return {
      kind: 'database',
      slot: environment === 'production' ? 'DbProduction' : 'DbPreview',
      environment,
      provider: w.platform,
      title: `${label} on ${databaseProviderLabel(w.platform)}`,
      needsConnection: w.platform !== 'external',
      previewInSettings: w.environment === null,
    }
  }
  return null
}

/** FR-188: the Deployment step's windows, exactly as `POST /api/stack/options` listed them: groups first, then each part. */
export function wizardHosts(v: Pick<HostFormValues, 'stackKind' | 'hostWindows'>): WizardHost[] {
  return v.stackKind === 'compose' ? windowsOf(v.hostWindows).flatMap((w) => hostOfWindow(w) ?? []) : []
}

/** The parts a window covers. */
export const partsOfHost = (host: WizardHost): Part[] => (host.kind === 'group' ? host.parts : host.kind === 'target' ? [host.component] : ['database'])

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

/** The group's values, for `groupRequestBody` and `groupChecks`. */
export const hostGroupValues = (v: Record<HostFieldName, string>): GroupSetupValues => ({
  ...hostValues(v, 'Group'),
  linkFrontend: v[hostGroupLinkFieldOf.frontend],
  linkBackend: v[hostGroupLinkFieldOf.backend],
  linkDatabase: v[hostGroupLinkFieldOf.database],
})

/** The stack choice each host slot follows: a new host leaves nothing of the old one's setup behind. */
export const hostSlotsOfChoice: Readonly<Record<string, readonly HostSlot[]>> = {
  frontendHost: ['Frontend', 'Group'],
  backendHost: ['Backend', 'Group'],
  databaseHost: ['DbProduction', 'DbPreview', 'Group'],
}

/** FR-152: a `postgres://` or `postgresql://` URI, or an ADO.NET string (`Key=value;…`) for SQL Server. */
const CONNECTION_STRING = /^(postgres(ql)?:\/\/\S+|[A-Za-z ]+=[^;]*(;[A-Za-z ]+=[^;]*)*;?)$/

const NOT_SEPARATE = 'The preview and production databases must be separate.'

const groupFieldOf: Readonly<Record<keyof GroupSetupValues, HostFieldName>> = {
  connectionId: 'hostGroupConnectionId',
  mode: 'hostGroupMode',
  name: 'hostGroupName',
  region: 'hostGroupRegion',
  plan: 'hostGroupPlan',
  resourceId: 'hostGroupResourceId',
  linkFrontend: hostGroupLinkFieldOf.frontend,
  linkBackend: hostGroupLinkFieldOf.backend,
  linkDatabase: hostGroupLinkFieldOf.database,
}

/**
 * The Deployment step's rules for the chosen hosts, matching the API (FR-144, FR-152, FR-190). The API still has the last
 * word: what only it can refuse is reported after the project is created, pointing to Settings → Deployment.
 */
export function hostChecks(v: HostFormValues): [HostFieldName, string][] {
  const issues: [HostFieldName, string][] = []
  const hosts = wizardHosts(v)
  for (const host of hosts) {
    if (!isHostChosen(v, host.slot)) {
      continue
    }
    if (host.kind === 'group') {
      for (const [field, message] of groupChecks(host, hostGroupValues(v), deploymentProviderLabels[host.provider], ', or set this up later')) {
        issues.push([groupFieldOf[field], message])
      }
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
