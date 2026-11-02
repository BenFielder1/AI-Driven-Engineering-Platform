import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { invalidateProject } from '../lifecycle/lifecycle-api'
import { projectKeys, unwrap } from '../projects/projects-api'
import { renderStackProfile, type StackProfileDocument } from './stack-profile-format'

export type Analysis = Schemas['AnalysisDto']
export type StackAnalysisRun = Schemas['StackAnalysisRunDto']
export type StackProfileView = Schemas['StackProfileViewDto']

/** The Stack Analyst is queued or running: its draft (or failure) is still to come. */
export const isAnalysisRunPending = (run: Pick<StackAnalysisRun, 'status'> | null | undefined) =>
  run?.status === 'queued' || run?.status === 'running'

/**
 * Fallback refresh while the Stack Analyst or the Repository Analyst works: the live `run`/`artifact` events normally
 * end the pending state first (FR-46); this only matters when the live connection is down.
 */
const PENDING_REFETCH_MS = 15_000

const summaryPending = (a: Analysis | undefined) => a?.summary.status === 'queued' || a?.summary.status === 'running'

/** FR-182: the Analysis page of the project's current release version (members). */
export function useAnalysis(id: string, enabled = true) {
  return useQuery({
    queryKey: projectKeys.analysis(id),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/projects/{id}/analysis', { params: { path: { id } }, signal })),
    refetchInterval: (query) =>
      isAnalysisRunPending(query.state.data?.run) || summaryPending(query.state.data) ? PENDING_REFETCH_MS : false,
    enabled,
  })
}

/**
 * FR-180 Retry (writers): queues the Stack Analyst again. 409 WRONG_PHASE, BUDGET_EXCEEDED, INVALID_STATE (already
 * queued or running). Either way the page is refreshed: the run arrives with the live events.
 */
export function useRunStackAnalysis(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/projects/{id}/analysis/run', { params: { path: { id } } })),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: projectKeys.analysis(id) }),
        queryClient.invalidateQueries({ queryKey: projectKeys.runs(id) }),
        queryClient.invalidateQueries({ queryKey: projectKeys.state(id) }),
      ]),
  })
}

/**
 * FR-183: saves an edited profile as a new draft (writers) through the generic artifact route: the narrative and the
 * fenced block (`renderStackProfile`), from `baseVersion` (409 CONFLICT when a newer version was saved meanwhile).
 * 400 VALIDATION carries field errors keyed like `layers.backend.option`.
 */
export function useSaveStackProfile(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({
      narrative,
      document,
      baseVersion,
    }: {
      narrative: string
      document: StackProfileDocument
      baseVersion: number | null
    }) =>
      unwrap(
        await api.POST('/api/projects/{id}/artifacts/{type}', {
          params: { path: { id, type: 'stack_profile' } },
          body: { content: renderStackProfile(narrative, document), baseVersion },
        }),
      ),
    onSettled: () => invalidateProject(queryClient, id),
  })
}

/**
 * FR-181, FR-183: approve (Owners; `generalist` is "Continue with No stack (general)") or reject (Owners, with the
 * comment the Stack Analyst re-runs with) the profile draft. 409 STACK_PROFILE_INCOMPLETE (details: the layers),
 * 400 STACK_INCOMPATIBLE (violations), 409 WRONG_PHASE / ARTIFACT_NOT_DRAFT. Approval pins the stack, so the whole
 * project is refreshed either way.
 */
export function useDecideStackProfile(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({
      action,
      artifactId,
      comment,
      generalist = false,
    }: {
      action: 'approve' | 'reject'
      artifactId: string
      comment: string | null
      generalist?: boolean
    }) =>
      unwrap(
        action === 'approve'
          ? await api.POST('/api/projects/{id}/artifacts/{artifactId}/approve', {
              params: { path: { id, artifactId } },
              body: generalist ? { comment, generalist: true } : { comment },
            })
          : await api.POST('/api/projects/{id}/artifacts/{artifactId}/reject', {
              params: { path: { id, artifactId } },
              body: { comment },
            }),
      ),
    onSettled: () => invalidateProject(queryClient, id),
  })
}
