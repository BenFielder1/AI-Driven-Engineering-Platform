import { useQuery } from '@tanstack/react-query'
import { useRouterState } from '@tanstack/react-router'

import { canViewAdmin } from '../../lib/permissions'
import type { User } from '../auth/auth-api'
import { projectQueryOptions, projectStateQueryOptions } from '../projects/projects-api'
import { projectIdFromPath, type NavViewer } from './nav-config'
import { stageStatuses, type StageStatus } from './stage-states'

export interface ShellNav {
  pathname: string
  viewer: NavViewer
  /** Set inside /projects/:id (project mode, FR-54). */
  projectId: string | undefined
  /** The project's name once loaded (shared cache with the project layout). */
  projectName: string | undefined
  /** FR-62: the project's released version (`null`: not released) once loaded. */
  releasedVersion: string | null | undefined
  /** FR-62: the version in development (`null`: none) once loaded. */
  developmentVersion: string | null | undefined
  /** FR-74: the current version's preset chip (name and version; null: no preset). */
  presetName: string | null | undefined
  presetVersion: string | null | undefined
  /** Lifecycle stage states once /state has loaded. */
  stages: StageStatus[] | undefined
}

/** What the shell needs to render nav-config for the current route and viewer. */
export function useShellNav(user: User): ShellNav {
  const pathname = useRouterState({ select: (s) => s.location.pathname })
  const projectId = projectIdFromPath(pathname)
  const project = useQuery({ ...projectQueryOptions(projectId ?? ''), enabled: projectId !== undefined })
  // Only once the project loaded: a 403/404 project has no state to show (the layout explains why).
  const state = useQuery({ ...projectStateQueryOptions(projectId ?? ''), enabled: projectId !== undefined && project.isSuccess })
  return {
    pathname,
    viewer: { isAdmin: canViewAdmin(user) },
    projectId,
    projectName: projectId ? project.data?.name : undefined,
    releasedVersion: projectId ? project.data?.releasedVersion : undefined,
    developmentVersion: projectId ? project.data?.developmentVersion : undefined,
    presetName: projectId ? project.data?.presetName : undefined,
    presetVersion: projectId ? project.data?.presetVersion : undefined,
    stages: projectId && state.data ? stageStatuses(state.data) : undefined,
  }
}
