import { createContext, useContext } from 'react'

import { effectiveProjectRole, type ProjectRole } from '../../lib/permissions'
import { useCurrentUser, type User } from '../auth/auth-api'
import type { Project } from './projects-api'

/** The loaded project, provided by the /projects/:id layout once GetProject succeeds. */
export const ProjectContext = createContext<Project | null>(null)

export interface ProjectAccess {
  project: Project
  user: User | null
  /** `myRole`, or Owner-equivalent for an Admin who isn't a member (SPEC §2). */
  role: ProjectRole | null
}

export function useProjectAccess(): ProjectAccess {
  const project = useContext(ProjectContext)
  const user = useCurrentUser()
  if (!project) {
    throw new Error('useProjectAccess must be used inside the project layout')
  }
  return { project, user, role: effectiveProjectRole(user, project.myRole) }
}
