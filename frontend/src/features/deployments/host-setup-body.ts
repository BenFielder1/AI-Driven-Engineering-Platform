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

function createFields(v: HostSetupValues) {
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
