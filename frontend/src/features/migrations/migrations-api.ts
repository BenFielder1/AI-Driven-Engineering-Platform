import { useQuery } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { projectKeys, unwrap } from '../projects/projects-api'

export type MigrationRun = Schemas['MigrationRunDto']
export type MigrationRunStatus = Schemas['MigrationRunStatus']
export type AdvisorFinding = Schemas['AdvisorFindingDto']

/** Under the project detail key, so a project live event (MIGRATION_RUN_*) refreshes them. */
export const migrationKeys = {
  all: (id: string) => [...projectKeys.detail(id), 'migrations'] as const,
  list: (id: string, releaseVersionId: string | undefined) => [...migrationKeys.all(id), 'list', releaseVersionId ?? 'all'] as const,
}

/** While a run is running, refetch every 10 s as a fallback to the live events. */
export const migrationPolling = { runningMs: 10_000 }

/** FR-153: the project's Supabase BaaS migration runs, newest first, optionally of one release version (any member). */
export function useMigrationRuns(projectId: string, releaseVersionId?: string) {
  return useQuery({
    queryKey: migrationKeys.list(projectId, releaseVersionId),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/migrations', {
          params: { path: { id: projectId }, query: releaseVersionId ? { releaseVersionId } : {} },
          signal,
        }),
      ),
    refetchInterval: (query) => (query.state.data?.some((r) => r.status === 'running') ? migrationPolling.runningMs : false),
  })
}
