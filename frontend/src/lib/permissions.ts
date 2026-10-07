import type { Schemas } from '../api/client'

type User = Pick<Schemas['UserDto'], 'systemRole'>

/** SPEC §2: system roles. */
export function isAdmin(user: User | null | undefined): boolean {
  return user?.systemRole === 'admin'
}

/** Admin pages (events, runs, dead letters, users) are for admins only. */
export const canViewAdmin = isAdmin

/** FR-3: only admins change system roles. */
export const canChangeSystemRoles = isAdmin

/** SPEC §2: per-project roles. (Not in the contract yet; projects arrive in a later phase.) */
export type ProjectRole = 'owner' | 'contributor' | 'viewer'

export type ProjectCapability =
  | 'view'
  | 'startAgents'
  | 'startExecution'
  | 'createDraft'
  | 'triggerReview'
  | 'decideGates'
  | 'approveArtifacts'
  | 'requestRelease'
  | 'rollback'
  | 'changeSettings'
  | 'manageMembers'
  | 'deleteProject'

const viewer: readonly ProjectCapability[] = ['view']
const contributor: readonly ProjectCapability[] = [
  ...viewer,
  'startAgents',
  'startExecution',
  'createDraft',
  'triggerReview',
]
const owner: readonly ProjectCapability[] = [
  ...contributor,
  'decideGates',
  'approveArtifacts',
  'requestRelease',
  'rollback',
  'changeSettings',
  'manageMembers',
  'deleteProject',
]

/** The SPEC §2 capability matrix. */
export const projectCapabilities: Readonly<Record<ProjectRole, readonly ProjectCapability[]>> = {
  owner,
  contributor,
  viewer,
}

/**
 * Whether the user may do `capability` in a project where they hold `role` (null when not a member).
 * Admins can do everything on every project. The API enforces this anyway; this only hides controls.
 */
export function canInProject(
  user: User | null | undefined,
  role: ProjectRole | null | undefined,
  capability: ProjectCapability,
): boolean {
  if (isAdmin(user)) {
    return true
  }
  return role ? projectCapabilities[role].includes(capability) : false
}

/** Writer = Contributor or Owner (SPEC §6). */
export function isWriter(role: ProjectRole | null | undefined): boolean {
  return role === 'owner' || role === 'contributor'
}
