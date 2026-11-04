import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { toApiError } from '../../lib/errors'
import { connectionKeys } from '../connections/connections-api'
import { projectKeys, unwrap } from '../projects/projects-api'
import type { TrackerProvider } from './tracker-picks'

export type TrackerStatus = Schemas['TrackerStatusDto']
export type TrackerConnectionSummary = Schemas['TrackerConnectionSummaryDto']
export type TrackerItem = Schemas['TrackerItemSummaryDto']
export type TrackerFilter = Schemas['TrackerFilterDto']
export type ProjectSources = Schemas['ProjectSourcesDto']
export type SourceImport = Schemas['SourceImportDto']
export type SourceItemDrift = Schemas['SourceItemDrift']
export type SourceItemStatusCategory = Schemas['SourceItemStatusCategory']

/** What a search reads. `query` is the native query (JQL or GitHub qualifiers); otherwise `filter`. */
export interface TrackerCriteria {
  provider: TrackerProvider
  connectionId: string | null
  containerId: string | null
  filter: TrackerFilter | null
  query: string | null
}

/** Under `connectionKeys.all`, so adding or deleting a connection refreshes tracker readiness. */
const trackerKeys = {
  list: () => [...connectionKeys.all, 'trackers'] as const,
  containers: (provider: string, connectionId: string | null, search: string) =>
    [...connectionKeys.all, 'trackers', provider, 'containers', connectionId, search] as const,
  search: (criteria: TrackerCriteria) => [...connectionKeys.all, 'trackers', criteria.provider, 'search', criteria] as const,
}

/** Under `projectKeys.refinement`: the tracker events (category `artifact`) refresh it through the live hub. */
const sourceKeys = {
  all: (id: string) => [...projectKeys.refinement(id), 'sources'] as const,
  detail: (id: string, releaseVersionId: string | undefined) => [...sourceKeys.all(id), releaseVersionId ?? 'current'] as const,
  check: (id: string, fingerprint: string) => [...sourceKeys.all(id), 'check', fingerprint] as const,
}

const SOURCES_POLL_MS = 5_000

/** FR-165: the four trackers, whether each is ready, and why not. */
export function useTrackers(enabled = true) {
  return useQuery({
    queryKey: trackerKeys.list(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/me/trackers', { signal })),
    enabled,
  })
}

/** FR-169: the projects, teams or repositories a connection reaches, a page at a time. */
export function useTrackerContainers(provider: TrackerProvider, connectionId: string | null, search: string) {
  return useInfiniteQuery({
    queryKey: trackerKeys.containers(provider, connectionId, search),
    initialPageParam: null as string | null,
    queryFn: async ({ pageParam, signal }) =>
      unwrap(
        await api.GET('/api/me/trackers/{provider}/containers', {
          params: {
            path: { provider },
            query: {
              ...(connectionId ? { connectionId } : {}),
              ...(search ? { search } : {}),
              ...(pageParam ? { cursor: pageParam } : {}),
            },
          },
          signal,
        }),
      ),
    getNextPageParam: (page) => page.nextCursor,
    retry: false,
  })
}

/** FR-169: paginated results. No retry: an invalid query should show the tracker's message at once. */
export function useTrackerSearch(criteria: TrackerCriteria | null) {
  return useInfiniteQuery({
    queryKey: criteria ? trackerKeys.search(criteria) : [...trackerKeys.list(), 'no-search'],
    initialPageParam: null as string | null,
    queryFn: async ({ pageParam, signal }) => {
      if (!criteria) {
        return { items: [], nextCursor: null }
      }
      return unwrap(
        await api.POST('/api/me/trackers/{provider}/search', {
          params: { path: { provider: criteria.provider } },
          body: {
            connectionId: criteria.connectionId,
            containerId: criteria.containerId,
            filter: criteria.filter,
            query: criteria.query,
            cursor: pageParam,
          },
          signal,
        }),
      )
    },
    getNextPageParam: (page) => page.nextCursor,
    enabled: criteria !== null,
    retry: false,
    refetchOnWindowFocus: false,
  })
}

const isImportPending = (i: Pick<SourceImport, 'status'>) => i.status === 'queued' || i.status === 'running'

/** FR-170, FR-173: a version's imports, snapshot and draft state. Polls under the live events while an import runs. */
export function useProjectSources(id: string, releaseVersionId?: string, enabled = true) {
  return useQuery({
    queryKey: sourceKeys.detail(id, releaseVersionId),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/sources', {
          params: { path: { id }, query: releaseVersionId ? { releaseVersionId } : {} },
          signal,
        }),
      ),
    enabled,
    refetchInterval: (query) => (query.state.data?.imports.some(isImportPending) ? SOURCES_POLL_MS : false),
  })
}

/**
 * FR-173: the drift check on opening the page. `fingerprint` changes with the imports, so a new snapshot is checked
 * again. It never changes a snapshot, and a failure only leaves the items without a drift label.
 */
export function useSourcesCheck(id: string, fingerprint: string, enabled: boolean) {
  return useQuery({
    queryKey: sourceKeys.check(id, fingerprint),
    queryFn: async ({ signal }) => unwrap(await api.POST('/api/projects/{id}/sources/check', { params: { path: { id } }, signal })),
    enabled,
    retry: false,
    refetchOnWindowFocus: false,
    staleTime: 5 * 60_000,
  })
}

function useInvalidateSources(id: string) {
  const queryClient = useQueryClient()
  return () => queryClient.invalidateQueries({ queryKey: sourceKeys.all(id) })
}

/** FR-171: queues an import on the current version. */
export function useAddSource(id: string) {
  const invalidate = useInvalidateSources(id)
  return useMutation({
    mutationFn: async (body: Schemas['SourceImportRequest']) =>
      unwrap(await api.POST('/api/projects/{id}/sources', { params: { path: { id } }, body })),
    onSuccess: invalidate,
  })
}

/** FR-173: repeats an import's query; also the retry of a failed import. */
export function useReimportSource(id: string) {
  const invalidate = useInvalidateSources(id)
  return useMutation({
    mutationFn: async (importId: string) =>
      unwrap(await api.POST('/api/projects/{id}/sources/{importId}/reimport', { params: { path: { id, importId } } })),
    onSettled: invalidate,
  })
}

/** FR-173: removes an import. A completed one leads to a new snapshot; earlier snapshots stay readable. */
export function useRemoveSource(id: string) {
  const invalidate = useInvalidateSources(id)
  return useMutation({
    mutationFn: async (importId: string) => {
      const { error, response } = await api.DELETE('/api/projects/{id}/sources/{importId}', { params: { path: { id, importId } } })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: invalidate,
  })
}
