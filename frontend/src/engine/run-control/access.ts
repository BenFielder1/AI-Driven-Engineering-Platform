import { isWriter, type ProjectRole } from '../../lib/permissions'

/**
 * Who is looking at the Pipeline tab, from the host (it owns the project and the session). `role` is the effective
 * project role (an Admin who isn't a member acts as Owner, SPEC §2). Without it every action is hidden. The API checks
 * every action anyway; these only hide controls.
 */
export interface PipelineAccess {
  role: ProjectRole | null
  userId: string | null
}

export const noAccess: PipelineAccess = { role: null, userId: null }

/** SPEC §2: start an engine-controlled run and submit input (Contributor and up, FR-362). */
export const canStartRuns = (access: PipelineAccess) => isWriter(access.role)

/** SPEC §2: add agent feedback (Contributor and up, FR-365). */
export const canAddFeedback = (access: PipelineAccess) => isWriter(access.role)

/** SPEC §2: delete agent feedback (Owner, or its author as a Contributor, FR-365). */
export function canDeleteFeedback(access: PipelineAccess, createdBy: string | null): boolean {
  if (access.role === 'owner') {
    return true
  }
  return isWriter(access.role) && createdBy !== null && createdBy === access.userId
}

/** SPEC §2: create, change and delete schedules and webhooks (Owner, FR-366). */
export const canManageTriggers = (access: PipelineAccess) => access.role === 'owner'
