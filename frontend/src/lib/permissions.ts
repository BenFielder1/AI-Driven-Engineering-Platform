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

/** SPEC §2: per-project roles (the contract's `ProjectRole`, without its `null`). */
export type ProjectRole = NonNullable<Schemas['ProjectRole']>

export type ProjectCapability =
  | 'view'
  | 'advancePhase'
  | 'startAgents'
  | 'startExecution'
  | 'createDraft'
  | 'triggerReview'
  | 'decideGates'
  | 'approveArtifacts'
  | 'requestRelease'
  | 'rollback'
  | 'startVersion'
  | 'changePreset'
  | 'changeSettings'
  | 'deployPreview'
  | 'manageDeployment'
  | 'manageMembers'
  | 'deleteProject'

const viewer: readonly ProjectCapability[] = ['view']
const contributor: readonly ProjectCapability[] = [
  ...viewer,
  'advancePhase',
  'startAgents',
  'startExecution',
  'createDraft',
  'triggerReview',
  // FR-90 (SPEC §2 v2): Contributors trigger (and redeploy) preview deployments.
  'deployPreview',
]
const owner: readonly ProjectCapability[] = [
  ...contributor,
  'decideGates',
  'approveArtifacts',
  'requestRelease',
  'rollback',
  // FR-60 (SPEC §2 v2): only Owners start a new release version.
  'startVersion',
  // FR-74 (SPEC §2 v2): only Owners choose the preset; Contributors and Viewers see it.
  'changePreset',
  'changeSettings',
  // FR-88, FR-91 (SPEC §2 v2): bind a Vercel connection, edit env vars, promote and redeploy production.
  'manageDeployment',
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

/**
 * The role the UI acts with in a project: the caller's membership role (`ProjectDto.myRole`), or Owner-equivalent
 * for an Admin who isn't a member (SPEC §2 Admin row). `null` means no access.
 */
export function effectiveProjectRole(
  user: User | null | undefined,
  myRole: ProjectRole | null | undefined,
): ProjectRole | null {
  if (myRole) {
    return myRole
  }
  return isAdmin(user) ? 'owner' : null
}
