/**
 * §7, FR-144, FR-152: the wizard's flat per-host fields (flat, like the stack fields, so the error summary and field ids
 * stay one level): `host<Slot><Prop>`, one set per target and database. No dependencies: `schemas.ts` builds on it.
 */
export const hostSlots = ['Frontend', 'Backend', 'DbProduction', 'DbPreview'] as const
export type HostSlot = (typeof hostSlots)[number]

const hostProps = ['Mode', 'ConnectionId', 'Name', 'Region', 'Plan', 'ResourceId', 'ConnectionString'] as const
export type HostProp = (typeof hostProps)[number]

export type HostFieldName = `host${HostSlot}${HostProp}`

export const hostField = <S extends HostSlot, P extends HostProp>(slot: S, prop: P): `host${S}${P}` => `host${slot}${prop}`

/** The mode of a host left for Settings → Deployment (the default: the Deployment step stays optional, FR-88). */
export const HOST_SKIP = 'skip'

export const hostFieldNames: readonly HostFieldName[] = hostSlots.flatMap((slot) => hostProps.map((prop) => hostField(slot, prop)))

/** The External connection strings: secrets, never kept in the wizard's draft. */
export const hostSecretFields = [hostField('DbProduction', 'ConnectionString'), hostField('DbPreview', 'ConnectionString')] as const

export const hostFieldDefaults = Object.fromEntries(
  hostFieldNames.map((name) => [name, name.endsWith('Mode') ? HOST_SKIP : '']),
) as Record<HostFieldName, string>

const kebab = (text: string) => text.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase()

/** Error-summary targets (`project-host-db-production-mode`). */
export const hostFieldIds = Object.fromEntries(
  hostFieldNames.map((name) => [name, `project-${kebab(name)}`]),
) as Record<HostFieldName, string>
