import type { Schemas } from '../../api/client'

/** A host's setup form values (FR-144, FR-152), from the Settings forms or the wizard's Deployment step. */
export interface HostSetupValues {
  connectionId: string
  mode: string
  name: string
  region: string
  plan: string
  resourceId: string
  connectionString: string
}

const trimmed = (s: string) => (s.trim() ? s.trim() : undefined)

function createFields(v: Pick<HostSetupValues, 'region' | 'plan'>) {
  const region = trimmed(v.region)
  const plan = trimmed(v.plan)
  return { ...(region ? { region } : {}), ...(plan ? { plan } : {}) }
}

/**
 * `PUT …/targets/:component`. GitHub Pages (no connection): the provider only. Others: always the chosen connection;
 * `keep` is a rebind (no create or link).
 */
export function targetRequestBody(
  provider: Schemas['DeploymentProvider'],
  needsConnection: boolean,
  v: HostSetupValues,
): Schemas['PutDeploymentTargetRequest'] {
  if (!needsConnection) {
    return { provider }
  }
  return {
    provider,
    connectionId: v.connectionId,
    ...(v.mode === 'create'
      ? { create: { name: v.name.trim(), ...createFields(v) } }
      : v.mode === 'link'
        ? { link: { resourceId: v.resourceId.trim() } }
        : {}),
  }
}

/** `PUT …/databases/:environment`: External sends the connection string; the others a connection and create or link. */
export function databaseRequestBody(
  provider: Schemas['ManagedDatabaseProvider'],
  needsConnection: boolean,
  v: HostSetupValues,
): Schemas['PutManagedDatabaseRequest'] {
  if (provider === 'external') {
    return { provider, external: { connectionString: v.connectionString.trim() } }
  }
  const name = trimmed(v.name)
  return {
    provider,
    ...(needsConnection ? { connectionId: v.connectionId } : {}),
    ...(v.mode === 'create'
      ? { create: { ...(name ? { name } : {}), ...createFields(v) } }
      : v.mode === 'link'
        ? { link: { resourceId: v.resourceId.trim() } }
        : {}),
  }
}

/** A group's setup form values: a host's, plus an existing resource per part ('' = Create in this project). */
export interface GroupSetupValues extends Omit<HostSetupValues, 'connectionString'> {
  linkFrontend: string
  linkBackend: string
  linkDatabase: string
}

export type GroupLinkField = 'linkFrontend' | 'linkBackend' | 'linkDatabase'

export const groupLinkFields: Readonly<Record<Schemas['DeploymentPart'], GroupLinkField>> = {
  frontend: 'linkFrontend',
  backend: 'linkBackend',
  database: 'linkDatabase',
}

/** What a group's window or card knows of the group (from `windows[]` or `DeploymentGroupDto`). */
export interface GroupShape {
  provider: Schemas['DeploymentProvider']
  capability: Schemas['SharedProjectCapability']
  parts: readonly Schemas['DeploymentPart'][]
}

/** The longest group name the API takes: part suffixes and `-preview` must still fit (FR-192). */
export const GROUP_NAME_MAX_LENGTH = 88

const GROUP_NAME = /^(?!.*---)[a-z0-9._-]+$/

/** The rules of a group's form, matching the API's `PUT …/groups/:provider` (FR-190). `later` ends the "pick" messages. */
export function groupChecks(group: GroupShape, v: GroupSetupValues, platform: string, later = ''): [keyof GroupSetupValues, string][] {
  const issues: [keyof GroupSetupValues, string][] = []
  if (!v.connectionId) {
    issues.push(['connectionId', `Pick one of your ${platform} connections${later}.`])
  }
  if (v.mode === 'create') {
    const name = v.name.trim()
    if (!name) {
      issues.push(['name', `Enter a name${later}.`])
    } else if (name.length > GROUP_NAME_MAX_LENGTH) {
      issues.push(['name', `Names are at most ${String(GROUP_NAME_MAX_LENGTH)} characters.`])
    } else if (!GROUP_NAME.test(name)) {
      issues.push(['name', "Use lowercase letters, digits, '.', '_' or '-', without '---'."])
    }
  }
  if (v.mode === 'link') {
    if (group.capability === 'project' && !v.resourceId.trim()) {
      issues.push(['resourceId', `Pick the project to link${later}.`])
    }
    if (group.capability === 'shared_setup') {
      for (const part of group.parts) {
        if (!v[groupLinkFields[part]].trim()) {
          issues.push([groupLinkFields[part], `Pick the project for the ${part}${later}.`])
        }
      }
    }
  }
  return issues
}

/**
 * `PUT …/groups/:provider`: a connection, and create, link or (neither) a rebind. Per part, what's left out is created in
 * the project. A Railway service picked under another project (the project changed afterwards) is left out too.
 */
export function groupRequestBody(group: GroupShape, v: GroupSetupValues): Schemas['PutDeploymentGroupRequest'] {
  if (v.mode === 'create') {
    return { connectionId: v.connectionId, create: { name: v.name.trim(), ...createFields(v) } }
  }
  if (v.mode !== 'link') {
    return { connectionId: v.connectionId }
  }
  const resourceId = group.capability === 'project' ? v.resourceId.trim() : ''
  const parts = Object.fromEntries(
    group.parts
      .map((part) => [part, v[groupLinkFields[part]].trim()] as const)
      .filter(([, id]) => id !== '' && (group.provider !== 'railway' || id.startsWith(`${resourceId}:`))),
  )
  return {
    connectionId: v.connectionId,
    link: { ...(resourceId ? { resourceId } : {}), ...(Object.keys(parts).length > 0 ? { parts } : {}) },
  }
}
