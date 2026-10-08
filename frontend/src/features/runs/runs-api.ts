import { keepPreviousData, useQuery } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { projectKeys, unwrap } from '../projects/projects-api'

export type AgentRun = Schemas['AgentRunDto']
export type AgentRunDetail = Schemas['AgentRunDetailDto']
export type AgentKind = Schemas['AgentKind']
export type AgentRunStatus = Schemas['AgentRunStatus']

/** Rows per page on /projects/:id/runs. */
export const RUNS_PAGE_SIZE = 20

/** Query keys, all under `projectKeys.runs(id)` so one live `run` event refreshes every runs view (FR-46). */
export const runKeys = {
  all: (id: string) => projectKeys.runs(id),
  list: (id: string, agent: AgentKind | null, page: number) =>
    [...projectKeys.runs(id), 'list', agent ?? 'all', page] as const,
  detail: (id: string, runId: string) => [...projectKeys.runs(id), 'detail', runId] as const,
}

/** FR-23: the project's agent runs, newest first, paged, optionally for one agent. */
export function useRuns(id: string, agent: AgentKind | null, page: number) {
  return useQuery({
    queryKey: runKeys.list(id, agent, page),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/runs', {
          params: { path: { id }, query: { page, limit: RUNS_PAGE_SIZE, ...(agent ? { agent } : {}) } },
          signal,
        }),
      ),
    placeholderData: keepPreviousData,
  })
}

/** FR-23: one run with its output (`null` until it succeeds; cut at 200,000 characters). */
export function useRun(id: string, runId: string | null) {
  return useQuery({
    queryKey: runKeys.detail(id, runId ?? ''),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/runs/{runId}', { params: { path: { id, runId: runId ?? '' } }, signal }),
      ),
    enabled: runId !== null,
  })
}
