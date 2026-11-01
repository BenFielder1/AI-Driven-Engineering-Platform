import { useQuery } from '@tanstack/react-query'

import { usePathname } from '../../lib/navigation'
import { canViewAdmin } from '../../lib/permissions'
import type { User } from '../auth/auth-api'
import { useDeploymentSettings } from '../deployments/deployments-api'
import { projectQueryOptions, projectStateQueryOptions } from '../projects/projects-api'
import { hasSupabaseBaasDatabase, projectIdFromPath, type NavViewer } from './nav-config'
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
  /** FR-88/FR-91: the project has a deployment target, so the Deployments item shows. */
  deploymentsEnabled: boolean
  /** FR-153: the project has a Supabase BaaS database, so the Migrations item shows (`hasSupabaseBaasDatabase`). */
  migrationsEnabled: boolean
  /** Lifecycle stage states once /state has loaded. */
  stages: StageStatus[] | undefined
}

/** What the shell needs to render nav-config for the current route and viewer. */
export function useShellNav(user: User): ShellNav {
  const pathname = usePathname()
  const projectId = projectIdFromPath(pathname)
  const project = useQuery({ ...projectQueryOptions(projectId ?? ''), enabled: projectId !== undefined })
  // Only once the project loaded: a 403/404 project has no state to show (the layout explains why).
  const state = useQuery({ ...projectStateQueryOptions(projectId ?? ''), enabled: projectId !== undefined && project.isSuccess })
  // FR-153: only a data-supabase project reads the deployment settings (shared cache with Settings → Deployment).
  const supabase = projectId !== undefined && project.data?.stackPacks.includes('data-supabase') === true
  const settings = useDeploymentSettings(projectId ?? '', supabase)
  return {
    pathname,
    viewer: { isAdmin: canViewAdmin(user) },
    projectId,
    projectName: projectId ? project.data?.name : undefined,
    releasedVersion: projectId ? project.data?.releasedVersion : undefined,
    developmentVersion: projectId ? project.data?.developmentVersion : undefined,
    deploymentsEnabled: projectId ? project.data?.deploymentsEnabled === true : false,
    migrationsEnabled: supabase && hasSupabaseBaasDatabase(project.data?.stackPacks, settings.data?.databases),
    stages: projectId && state.data ? stageStatuses(state.data) : undefined,
  }
}
