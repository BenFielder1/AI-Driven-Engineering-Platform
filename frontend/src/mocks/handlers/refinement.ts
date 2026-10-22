import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { writerMutation, seedArtifact } from './artifacts'
import { nextId } from './auth'
import { addEvent } from './lifecycle'
import { problem } from './problem'
import { access, currentVersionId, record, type StoredProject, type StoredRefinementRound } from './projects'

type Round = Schemas['RefinementRoundDto']
type Question = NonNullable<StoredRefinementRound['questions']>[number]
type Answer = Schemas['RefinementAnswerDto']

/** RefinementRules limits. */
const maxAnswers = 20
const maxAnswerLength = 4000

let roundClock = Date.parse('2026-09-03T09:00:00Z')

const isPending = (r: Round) => r.status === 'queued' || r.status === 'running'

/** A new project request draft from a round: earlier drafts are superseded (§4.3). */
function writeDraft(project: StoredProject, round: StoredRefinementRound, content: string) {
  for (const draft of project.artifacts.filter((a) => a.type === 'project_request' && a.status === 'draft')) {
    draft.status = 'superseded'
  }
  const draft = seedArtifact(project, { type: 'project_request', content })
  draft.author = { user: null, agentRunId: round.runId }
  round.draftArtifactId = draft.id
  round.draftVersion = draft.version
  addEvent(project, {
    type: 'ARTIFACT_CREATED',
    category: 'artifact',
    summary: `project_request v${String(draft.version)} created`,
    payload: { artifactId: draft.id, type: 'project_request', version: draft.version },
  })
}

/**
 * Seeds a refinement round (tests). A succeeded round with `draft` writes a new project request draft; `questions`
 * are what its run asked. Rounds are stored oldest first.
 */
export function seedRefinementRound(
  project: StoredProject,
  init: {
    status?: Round['status']
    answers?: Answer[]
    questions?: Question[]
    draft?: string
    error?: string
    trigger?: Round['trigger']
  } = {},
): StoredRefinementRound {
  roundClock += 3_600_000
  const status = init.status ?? 'succeeded'
  const jobId = nextId()
  const finished = status === 'succeeded' || status === 'failed' || status === 'cancelled'
  const round: StoredRefinementRound = {
    runId: jobId,
    jobId,
    trigger: init.trigger ?? 'user_requested',
    status,
    error: init.error ?? null,
    answers: init.answers ?? [],
    feedback: null,
    draftArtifactId: null,
    draftVersion: null,
    createdAt: new Date(roundClock).toISOString(),
    completedAt: finished ? new Date(roundClock + 30_000).toISOString() : null,
    questions: status === 'succeeded' ? (init.questions ?? []) : null,
  }
  if (status === 'succeeded' && init.draft !== undefined) {
    writeDraft(project, round, init.draft)
  }
  project.refinementRounds.push(round)
  return round
}

function latestRound(project: StoredProject): StoredRefinementRound | undefined {
  return project.refinementRounds.at(-1)
}

/** The worker finishing the latest round (tests): new draft and questions; then emit the live `run`/`artifact` event. */
export function finishRefinementRound(project: StoredProject, result: { draft: string; questions?: Question[] }) {
  const round = latestRound(project)
  if (!round) {
    throw new Error('No refinement round to finish')
  }
  round.status = 'succeeded'
  round.completedAt = new Date().toISOString()
  round.questions = result.questions ?? []
  writeDraft(project, round, result.draft)
  addEvent(project, { type: 'RUN_COMPLETED', category: 'run', summary: 'Idea refinement completed' })
  return round
}

/** The worker failing the latest round (tests), with the run's `CODE: message` error. */
export function failRefinementRound(project: StoredProject, error: string) {
  const round = latestRound(project)
  if (!round) {
    throw new Error('No refinement round to fail')
  }
  round.status = 'failed'
  round.error = error
  round.completedAt = new Date().toISOString()
  addEvent(project, { type: 'RUN_FAILED', category: 'run', summary: 'Idea refinement failed' })
  return round
}

// eslint-disable-next-line @typescript-eslint/no-unused-vars
const toDto = ({ questions, ...dto }: StoredRefinementRound): Round => dto

/**
 * FR-55: every round, newest first, numbered oldest = 1. A round's questions carry the answer given to them by the
 * latest round started up to (and including) the next succeeded round.
 */
function roundHistory(rounds: StoredRefinementRound[]): Schemas['RefinementRoundHistoryDto'][] {
  return rounds
    .map((r, i) => {
      const later = rounds.slice(i + 1)
      const nextSucceeded = later.findIndex((x) => x.status === 'succeeded')
      const answering = nextSucceeded === -1 ? later.at(-1) : later[nextSucceeded]
      const given = new Map((answering?.answers ?? []).flatMap((a) => (a.questionId ? [[a.questionId, a.answer] as const] : [])))
      return {
        round: i + 1,
        runId: r.runId,
        jobId: r.jobId,
        trigger: r.trigger,
        status: r.status,
        error: r.error,
        startedAt: r.createdAt,
        completedAt: r.completedAt,
        questions: (r.questions ?? []).map((q) => ({ ...q, answer: given.get(q.id) ?? null })),
        answers: r.answers,
        feedback: r.feedback,
        draftArtifactId: r.draftArtifactId,
        draftArtifactVersion: r.draftVersion,
      }
    })
    .reverse()
}

/** Mirrors RefinementService.GetAsync. */
function refinementDto(project: StoredProject, releaseVersionId: string): Schemas['RefinementDto'] {
  const drafts = project.artifacts
    .filter((a) => a.type === 'project_request' && a.releaseVersionId === releaseVersionId)
    .sort((a, b) => b.version - a.version)
  // Rounds are only kept for the current release version in this mock.
  const roundsOf = releaseVersionId === currentVersionId(project) ? project.refinementRounds : []
  const latestDraft = drafts[0]
  const rounds = [...roundsOf].reverse()
  const latest = rounds[0]
  const completed = rounds.find((r) => r.status === 'succeeded' && r.questions !== null)
  const later = latest && completed && latest.jobId !== completed.jobId ? latest : undefined
  const given = new Map((later?.answers ?? []).flatMap((a) => (a.questionId ? [[a.questionId, a.answer] as const] : [])))
  return {
    latestDraft: latestDraft
      ? {
          id: latestDraft.id,
          type: latestDraft.type,
          version: latestDraft.version,
          releaseVersionId: latestDraft.releaseVersionId,
          releaseVersion: latestDraft.releaseVersion,
          restoredFromVersion: latestDraft.restoredFromVersion,
          status: latestDraft.status,
          integrity: latestDraft.integrity,
          createdAt: latestDraft.createdAt,
          author: latestDraft.author,
        }
      : null,
    latestRound: latest ? toDto(latest) : null,
    questionsRunId: completed?.runId ?? null,
    questions: (completed?.questions ?? []).map((q) => ({ ...q, answer: given.get(q.id) ?? null })),
    codebaseSummary: codebaseSummaryOf(project, releaseVersionId),
    rounds: roundHistory(roundsOf),
  }
}

/** FR-70: `completed` once the version has a summary, else the seeded analyst state, else `not_needed`. */
function codebaseSummaryOf(project: StoredProject, releaseVersionId: string): Schemas['CodebaseSummaryStatusDto'] {
  const summary = project.artifacts
    .filter((a) => a.type === 'codebase_summary' && a.releaseVersionId === releaseVersionId)
    .sort((a, b) => b.version - a.version)[0]
  const seeded = project.analyst?.[releaseVersionId]
  if (summary) {
    return { status: 'completed', runId: seeded?.runId ?? null, artifactId: summary.id, artifactVersion: summary.version }
  }
  return seeded ?? { status: 'not_needed', runId: null, artifactId: null, artifactVersion: null }
}

const validation = (errors: Record<string, string[]>) =>
  problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors })

export const refinementHandlers = [
  http.get('*/api/projects/:id/refinement', ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const scope = new URL(request.url).searchParams.get('releaseVersionId') ?? currentVersionId(a.project)
    return HttpResponse.json<Schemas['RefinementDto']>(refinementDto(a.project, scope))
  }),

  // FR-27: queue a round (writers): WRONG_PHASE, BUDGET_EXCEEDED, REFINEMENT_IN_PROGRESS, VALIDATION.
  http.post('*/api/projects/:id/refinement/run', async ({ request, params }) => {
    const a = writerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['StartRefinementRequest'] | null
    const p = a.project
    if (p.currentPhase !== 'refinement') {
      return problem(409, 'WRONG_PHASE', `Refinement rounds run only in the refinement phase; the project is in ${p.currentPhase}.`)
    }
    if (p.tokenBudget !== null && p.tokensUsed >= p.tokenBudget) {
      return problem(409, 'BUDGET_EXCEEDED', 'The project has used its token budget.')
    }
    if (p.refinementRounds.some(isPending)) {
      return problem(409, 'REFINEMENT_IN_PROGRESS', 'A refinement round is already queued or running.')
    }
    const inputs = body?.answers ?? []
    if (inputs.length > maxAnswers) {
      return validation({ answers: [`At most ${String(maxAnswers)} answers.`] })
    }
    const completed = [...p.refinementRounds].reverse().find((r) => r.status === 'succeeded' && r.questions !== null)
    const answers: Answer[] = []
    for (const input of inputs) {
      const answer = input.answer.trim()
      if (!answer || answer.length > maxAnswerLength) {
        return validation({ answers: ['Every answer needs 1 to 4000 characters.'] })
      }
      if (input.questionId) {
        const q = completed?.questions?.find((x) => x.id === input.questionId)
        if (!q) {
          return validation({ answers: [`Unknown question ${input.questionId}.`] })
        }
        answers.push({ questionId: q.id, question: q.question, answer })
      } else if (input.question?.trim()) {
        answers.push({ questionId: null, question: input.question.trim(), answer })
      } else {
        return validation({ answers: ['Every answer needs a questionId or a question.'] })
      }
    }
    const round = seedRefinementRound(p, { status: 'queued', answers })
    round.createdAt = new Date().toISOString()
    addEvent(p, { type: 'JOB_QUEUED', category: 'run', summary: 'Idea refinement queued' })
    return HttpResponse.json<Round>(toDto(round), { status: 202 })
  }),
]
