import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { projectKeys, unwrap } from '../projects/projects-api'

export type Refinement = Schemas['RefinementDto']
export type RefinementRound = Schemas['RefinementRoundDto']
export type RefinementQuestion = Schemas['RefinementQuestionDto']
export type RefinementAnswerRequest = Schemas['RefinementAnswerRequest']

export const refinementKeys = {
  detail: (id: string) => projectKeys.refinement(id),
}

/** A round still waiting for its draft (nothing streams in v1, SPEC §8). */
export const isRoundPending = (round: Pick<RefinementRound, 'status'> | null | undefined) =>
  round?.status === 'queued' || round?.status === 'running'

/**
 * Fallback refresh while a round is queued or running: the live `run`/`artifact` events normally end the pending
 * state first (FR-46); this only matters when the live connection is down.
 */
const PENDING_REFETCH_MS = 15_000

/** FR-27: the refinement page state (members): latest draft summary, latest round, its questions and given answers. */
export function useRefinement(id: string) {
  return useQuery({
    queryKey: refinementKeys.detail(id),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/refinement', { params: { path: { id } }, signal })),
    refetchInterval: (query) => (isRoundPending(query.state.data?.latestRound) ? PENDING_REFETCH_MS : false),
  })
}

/**
 * FR-27: run a refinement round (writers) with the answers given (blank answers are left out). The 202 round is put
 * into the cache straight away so the page shows the pending state; the run, the new draft and its questions arrive
 * with the live events. Errors (REFINEMENT_IN_PROGRESS, CONFLICT…) mean our view is stale, so it's refetched either way.
 */
export function useStartRefinement(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (answers: RefinementAnswerRequest[]) =>
      unwrap(
        await api.POST('/api/projects/{id}/refinement/run', {
          params: { path: { id } },
          body: answers.length > 0 ? { answers } : {},
        }),
      ),
    onSuccess: (round) => {
      queryClient.setQueryData<Refinement>(refinementKeys.detail(id), (current) => {
        if (!current) {
          return current
        }
        const given = new Map(round.answers.flatMap((a) => (a.questionId ? [[a.questionId, a.answer] as const] : [])))
        return {
          ...current,
          latestRound: round,
          questions: current.questions.map((q) => ({ ...q, answer: given.get(q.id) ?? null })),
        }
      })
    },
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: refinementKeys.detail(id) }),
        queryClient.invalidateQueries({ queryKey: projectKeys.state(id) }),
        queryClient.invalidateQueries({ queryKey: projectKeys.runs(id) }),
      ]),
  })
}
