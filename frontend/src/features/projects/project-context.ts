import { createContext, useContext } from 'react'

import { canInProject, effectiveProjectRole, type ProjectCapability, type ProjectRole } from '../../lib/permissions'
import { useCurrentUser, type User } from '../auth/auth-api'
import type { ReleaseView } from '../versions/use-selected-release-version'
import type { Project } from './projects-api'

/** The loaded project, provided by the /projects/:id layout once GetProject succeeds. */
export const ProjectContext = createContext<Project | null>(null)

export const currentView: ReleaseView = { version: undefined, readOnly: false, releaseVersionId: undefined, resolving: false, versions: [] }

/** The release version on screen (FR-62), provided by the /projects/:id layout. */
export const ReleaseViewContext = createContext<ReleaseView>(currentView)

export interface ProjectAccess {
  project: Project
  user: User | null
  /** `myRole`, or Owner-equivalent for an Admin who isn't a member (SPEC §2). */
  role: ProjectRole | null
  /** A past release version is on screen (FR-62): every mutation is hidden. */
  readOnly: boolean
  /** Pass to history queries (artifacts, runs, steps, compliance, releases, activity); `undefined` = the current version. */
  releaseVersionId: string | undefined
  /** The release version on screen. */
  view: ReleaseView
  /** The SPEC §2 capability check, false for everything but `view` while read-only (FR-62). */
  can: (capability: ProjectCapability) => boolean
}

export function useProjectAccess(): ProjectAccess {
  const project = useContext(ProjectContext)
  const view = useContext(ReleaseViewContext)
  const user = useCurrentUser()
  if (!project) {
    throw new Error('useProjectAccess must be used inside the project layout')
  }
  const role = effectiveProjectRole(user, project.myRole)
  return {
    project,
    user,
    role,
    readOnly: view.readOnly,
    releaseVersionId: view.releaseVersionId,
    view,
    can: (capability) => (capability === 'view' || !view.readOnly) && canInProject(user, role, capability),
  }
}
