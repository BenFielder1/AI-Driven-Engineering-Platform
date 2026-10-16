import { zodResolver } from '@hookform/resolvers/zod'
import { Link } from '@tanstack/react-router'
import { useState, type ReactNode } from 'react'
import { useForm } from 'react-hook-form'

import type { Schemas } from '../../api/client'
import { Alert, ErrorSummary, type SummaryError } from '../../components/ui/alert'
import { Badge } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextAreaField } from '../../components/ui/textarea-field'
import { applyFieldErrors, errorMessage } from '../../lib/errors'
import { useDocumentTitle } from '../../lib/use-document-title'
import { useLatestArtifact } from '../artifacts/artifacts-api'
import { MarkdownView } from '../artifacts/markdown-view'
import { artifactStatusLabels, phaseLabels } from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { RelativeTime } from '../projects/relative-time'
import { RunStatusBadge } from '../runs/run-badges'
import { StageArtifact } from '../stages/stage-artifact'
import { StageHeader } from '../stages/stage-header'
import { StageRuns } from '../stages/stage-runs'
import { StageSection } from '../stages/stage-section'
import { describeRunError } from '../runs/run-format'
import {
  isRoundPending,
  useRefinement,
  useStartRefinement,
  type RefinementAnswerRequest,
  type RefinementQuestion,
  type RefinementRound,
} from './refinement-api'
import { answersSchema, type AnswersValues } from './refinement-schemas'

type Trigger = NonNullable<RefinementRound['trigger']>

const triggerLabels: Record<Trigger, string> = {
  user_requested: 'Started from this page',
  phase_entered: 'Started when the phase began',
  gate_rejected: 'Started after a gate was rejected',
  artifact_rejected: 'Started after the draft was rejected',
  artifact_corrupted: 'Started after the draft failed its integrity check',
}

function Card({ id, title, children, actions }: { id: string; title: string; children: ReactNode; actions?: ReactNode }) {
  return (
    <section aria-labelledby={id} className="flex flex-col gap-3 rounded-xl border border-border/60 bg-card/70 shadow-sm backdrop-blur-md p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 id={id} className="text-lg">
          {title}
        </h3>
        {actions}
      </div>
      {children}
    </section>
  )
}

/** Question → answer pairs, read-only (answers sent with a round, FR-27). */
function AnswerList({ answers }: { answers: { question: string; answer: string }[] }) {
  return (
    <dl className="flex flex-col gap-2">
      {answers.map((a) => (
        <div key={`${a.question}:${a.answer}`} className="rounded-lg border border-border/60 p-2">
          <dt className="text-sm font-medium break-words">{a.question}</dt>
          <dd className="mt-1 text-sm whitespace-pre-wrap break-words">{a.answer}</dd>
        </div>
      ))}
    </dl>
  )
}

/** FR-27, SPEC §7: the latest round's status, its run, and what the agent was given. */
function RoundStatus({ projectId, round }: { projectId: string; round: RefinementRound }) {
  const failure = round.status === 'failed' && round.error ? describeRunError(round.error) : null
  const cancelled =
    round.status === 'cancelled' ? (round.error ? describeRunError(round.error) : { title: null, message: '' }) : null
  return (
    <Card
      id="refinement-round"
      title="Latest round"
      actions={
        round.status !== 'queued' && (
          <Link to="/projects/$projectId/runs" params={{ projectId }} search={{ run: round.runId }} className="text-sm text-primary underline">
            View the agent run
          </Link>
        )
      }
    >
      <dl className="flex flex-wrap gap-x-6 gap-y-2 text-sm">
        <div className="flex items-center gap-2">
          <dt className="text-muted-foreground">Status</dt>
          <dd>
            <RunStatusBadge status={round.status} />
          </dd>
        </div>
        <div className="flex items-center gap-2">
          <dt className="text-muted-foreground">Started</dt>
          <dd>
            <RelativeTime iso={round.createdAt} />
          </dd>
        </div>
        {round.trigger && (
          <div className="flex items-center gap-2">
            <dt className="text-muted-foreground">Trigger</dt>
            <dd>{triggerLabels[round.trigger]}</dd>
          </div>
        )}
        {round.draftVersion !== null && (
          <div className="flex items-center gap-2">
            <dt className="text-muted-foreground">Draft</dt>
            <dd>v{round.draftVersion}</dd>
          </div>
        )}
      </dl>

      {isRoundPending(round) && (
        <p role="status" aria-busy="true" className="rounded-lg border border-border/60 bg-muted/60 p-3 text-sm">
          <span aria-hidden="true" className="mr-2 inline-block size-3 animate-pulse rounded-full bg-primary align-middle" />
          The Idea Refinement agent is {round.status === 'queued' ? 'about to start' : 'working'} on this round. The new draft and
          any clarifying questions appear here when it&apos;s done.
        </p>
      )}
      {failure && (
        <Alert tone="error" role="alert" title={`The round failed. ${failure.title ?? ''}`.trim()}>
          {failure.message && <p className="break-words">{failure.message}</p>}
          <p className="mt-1">Your answers are kept below, so you can run the round again.</p>
        </Alert>
      )}
      {cancelled && (
        <Alert tone="warning" title={`The round was cancelled before it produced a draft. ${cancelled.title ?? ''}`.trim()}>
          {cancelled.message && <p className="break-words">{cancelled.message}</p>}
          <p className="mt-1">Your answers are kept below, so you can run the round again.</p>
        </Alert>
      )}

      {round.feedback && (
        <div className="flex flex-col gap-1">
          <h4 className="text-sm font-semibold">Feedback given to the agent</h4>
          <p className="text-sm whitespace-pre-wrap break-words">{round.feedback}</p>
        </div>
      )}
      {round.answers.length > 0 && (
        <div className="flex flex-col gap-1">
          <h4 className="text-sm font-semibold">Answers sent with this round</h4>
          <AnswerList answers={round.answers} />
        </div>
      )}
    </Card>
  )
}

/** Questions without answer fields (viewers, outside the refinement phase, or while a round is running). */
function QuestionList({ questions }: { questions: RefinementQuestion[] }) {
  return (
    <ol className="flex flex-col gap-3">
      {questions.map((q) => (
        <li key={q.id} className="rounded-lg border border-border/60 p-3">
          <p className="font-medium break-words">{q.question}</p>
          {q.reason && <p className="mt-1 text-sm text-muted-foreground break-words">{q.reason}</p>}
          {q.answer !== null && (
            <p className="mt-2 text-sm whitespace-pre-wrap break-words">
              <span className="font-medium">Your answer: </span>
              {q.answer}
            </p>
          )}
        </li>
      ))}
    </ol>
  )
}

/**
 * FR-27: an answer field per clarifying question (answer some or all; blank = unanswered) and "Run another round".
 * With no questions it's just the button. Remounted (keyed) when a new round brings new questions.
 */
function RoundForm({
  questions,
  hasDraft,
  pending,
  onRun,
}: {
  questions: RefinementQuestion[]
  hasDraft: boolean
  pending: boolean
  onRun: (answers: RefinementAnswerRequest[]) => void
}) {
  const fieldId = (i: number) => `refinement-answer-${String(i)}`
  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitted },
  } = useForm<AnswersValues>({
    resolver: zodResolver(answersSchema),
    defaultValues: { answers: questions.map((q) => q.answer ?? '') },
  })

  const onSubmit = handleSubmit(({ answers }) => {
    onRun(
      questions.flatMap((q, i) => {
        const answer = answers[i]?.trim() ?? ''
        return answer ? [{ questionId: q.id, answer }] : []
      }),
    )
  })

  const summary: SummaryError[] = [
    ...(isSubmitted
      ? questions.flatMap((_, i) => {
          const message = errors.answers?.[i]?.message
          return message ? [{ fieldId: fieldId(i), message }] : []
        })
      : []),
  ]
  const label = hasDraft ? 'Run another round' : 'Start refinement'

  return (
    <form noValidate aria-label="Refinement round" onSubmit={(e) => void onSubmit(e)} className="flex flex-col gap-4">
      <ErrorSummary title="We couldn't start the round" errors={summary} />
      {questions.length > 0 ? (
        <>
          <p className="text-sm text-muted-foreground">
            Answer as many as you like; questions left blank stay unanswered. Your answers go to the agent with the current
            draft, and the next round creates a new draft version.
          </p>
          <ol className="flex flex-col gap-4">
            {questions.map((q, i) => (
              <li key={q.id}>
                <TextAreaField
                  id={fieldId(i)}
                  label={q.question}
                  hint={q.reason ?? undefined}
                  rows={3}
                  error={errors.answers?.[i]?.message}
                  {...register(`answers.${String(i)}` as `answers.${number}`)}
                />
              </li>
            ))}
          </ol>
        </>
      ) : (
        <p className="text-sm text-muted-foreground">
          {hasDraft
            ? 'The agent asked no clarifying questions. You can still run another round to refine the draft.'
            : 'The Idea Refinement agent turns the idea into a first project request draft and may ask clarifying questions.'}
        </p>
      )}
      <div>
        <Button type="submit" pending={pending} pendingLabel="Starting…">
          {label}
        </Button>
      </div>
    </form>
  )
}

type CodebaseSummaryStatus = Schemas['CodebaseSummaryStatusDto']

/** The completed summary: the locked artifact, rendered (FR-70). */
function SummaryArtifact({ projectId, releaseVersionId }: { projectId: string; releaseVersionId: string | undefined }) {
  const summary = useLatestArtifact(projectId, 'codebase_summary', true, releaseVersionId)
  if (summary.isPending) {
    return <LoadingState label="Loading the codebase summary…" />
  }
  if (summary.isError) {
    return <ErrorState error={summary.error} title="The codebase summary couldn't load" onRetry={() => void summary.refetch()} />
  }
  const artifact = summary.data
  return (
    <>
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <Badge tone={artifact.status === 'locked' ? 'success' : 'neutral'}>
          {/* Auto-locked without an approver (FR-70), so "Locked" rather than "Approved". */}
          {artifact.status === 'locked' ? 'Locked' : artifact.status ? artifactStatusLabels[artifact.status] : 'Unknown'}
        </Badge>
        <span className="text-muted-foreground">
          v{artifact.version} · Generated by the Repository Analyst from the code at this version&apos;s start. The other
          agents read it as context; it needs no approval.
        </span>
      </div>
      <MarkdownView content={artifact.content} label={`Codebase summary v${String(artifact.version)}`} />
    </>
  )
}

/**
 * FR-55, FR-70: the Repository Analyst's codebase summary, driven by GET /refinement `codebaseSummary`: being
 * generated (queued/running), the locked summary (completed), why refinement went on without it (failed/timed out,
 * with the run), or why there is none (not_needed: no existing code).
 */
function CodebaseSummary({ state }: { state: CodebaseSummaryStatus | undefined }) {
  const { project, releaseVersionId } = useProjectAccess()
  if (state === undefined || state.status === 'not_needed') {
    return state === undefined ? null : (
      <StageSection id="codebase-summary" title="Codebase summary">
        <p className="text-sm text-muted-foreground">
          No codebase summary: this version starts without existing code, so there is nothing for the Repository Analyst
          to summarise. It runs when a later version starts, or when a project imports a repository.
        </p>
      </StageSection>
    )
  }
  const runLink = state.runId ? (
    <Link
      to="/projects/$projectId/runs"
      params={{ projectId: project.id }}
      search={{ run: state.runId }}
      className="text-sm text-primary underline"
    >
      View the Repository Analyst run
    </Link>
  ) : undefined
  let body: ReactNode
  switch (state.status) {
    case 'queued':
    case 'running':
      body = (
        <p role="status" className="rounded-lg border border-border/60 bg-muted/60 p-3 text-sm">
          <span aria-hidden="true" className="mr-2 inline-block size-3 animate-pulse rounded-full bg-primary align-middle" />
          <span className="font-medium">Summary being generated…</span> The Repository Analyst{' '}
          {state.status === 'queued' ? 'is about to read' : 'is reading'} the repository at this version&apos;s base
          commit. Refinement can start before it&apos;s done: the refinement run waits up to 10 minutes for the summary.
        </p>
      )
      break
    case 'failed':
    case 'timed_out':
      body = (
        <Alert tone="warning" title={state.status === 'failed' ? 'The Repository Analyst failed' : 'The codebase summary took too long'}>
          <p>
            {state.status === 'failed'
              ? 'It ended without a codebase summary, so refinement continued without it.'
              : 'It wasn’t ready 10 minutes after the version started, so refinement no longer waits for it and continued without it.'}{' '}
            The agents still read the repository through their tools.
          </p>
        </Alert>
      )
      break
    case 'completed':
      body = <SummaryArtifact projectId={project.id} releaseVersionId={releaseVersionId} />
      break
  }
  return (
    <StageSection id="codebase-summary" title="Codebase summary" actions={runLink}>
      {body}
    </StageSection>
  )
}

/**
 * /projects/:id/refinement (FR-27, FR-55): the version's request text, the project request in full (approve or reject
 * a draft in place), the latest round and its clarifying questions with answer fields and "Run another round"
 * (writers, refinement phase only), the Repository Analyst summary for iterations and the Idea Refinement runs. A past
 * release version (FR-62) shows its request and rounds read-only.
 */
export function RefinementPage() {
  const { project, can, readOnly, releaseVersionId, view } = useProjectAccess()
  useDocumentTitle(`Refinement · ${project.name}`)
  const refinement = useRefinement(project.id, releaseVersionId)
  const start = useStartRefinement(project.id)
  /** Shown outside the form: REFINEMENT_IN_PROGRESS and CONFLICT refetch the page, which replaces the form. */
  const [startErrors, setStartErrors] = useState<string[]>([])
  const writer = can('startAgents')
  const inPhase = project.currentPhase === 'refinement' && !readOnly
  const request = view.version?.request ?? project.idea

  const onRun = (answers: RefinementAnswerRequest[]) => {
    setStartErrors([])
    start.mutate(answers, {
      onError: (error) => {
        const unmatched = applyFieldErrors(error, () => undefined, [])
        setStartErrors(unmatched.length > 0 ? unmatched : [errorMessage(error, 'startRefinement')])
      },
    })
  }

  let rounds: ReactNode
  if (refinement.isPending) {
    rounds = <LoadingState label="Loading refinement…" />
  } else if (refinement.isError) {
    rounds = <ErrorState error={refinement.error} title="Refinement couldn't load" onRetry={() => void refinement.refetch()} />
  } else {
    const { latestDraft, latestRound, questions, questionsRunId } = refinement.data
    const pending = isRoundPending(latestRound)
    const canRun = writer && inPhase && !pending
    rounds = (
      <>
        <div aria-live="polite">{latestRound && <RoundStatus projectId={project.id} round={latestRound} />}</div>

        <StageArtifact
          stage="refinement"
          type="project_request"
          expectedLatest={latestDraft?.version ?? null}
          notStarted="The Idea Refinement agent turns the project idea (the request above) into a project request. Start the first round below; the agent may ask clarifying questions."
        />

        <ErrorSummary title="We couldn't start the round" errors={startErrors.map((message) => ({ message }))} />

        {(latestDraft !== null || questions.length > 0 || canRun) && (
          <Card
            id="refinement-questions"
            title={latestDraft !== null || questions.length > 0 ? 'Clarifying questions' : 'First round'}
          >
            {canRun ? (
              <RoundForm
                key={questionsRunId ?? 'none'}
                questions={questions}
                hasDraft={latestDraft !== null}
                pending={start.isPending}
                onRun={onRun}
              />
            ) : questions.length > 0 ? (
              <>
                {pending && <p className="text-sm text-muted-foreground">These answers were sent with the round in progress.</p>}
                <QuestionList questions={questions} />
              </>
            ) : (
              <p className="text-sm text-muted-foreground">{pending ? 'The round in progress may bring new questions.' : 'No questions to answer.'}</p>
            )}
          </Card>
        )}
      </>
    )
  }

  const awaiting = refinement.data?.latestDraft?.status === 'draft'
  return (
    <section aria-labelledby="refinement-title">
      <StageHeader stage="refinement" awaitingApproval={awaiting} />
      <div className="flex flex-col gap-5">
        {!inPhase && !readOnly && (
          <Alert tone="info">
            The project is in {phaseLabels[project.currentPhase]}. Refinement rounds run only in the Refinement phase, so this
            page is read-only.
          </Alert>
        )}
        {inPhase && !writer && (
          <Alert tone="info">You can follow refinement here. Only Contributors and Owners can answer questions and run rounds.</Alert>
        )}
        <StageSection id="refinement-request" title="Request">
          <blockquote className="whitespace-pre-wrap break-words">{request}</blockquote>
        </StageSection>
        {rounds}
        <CodebaseSummary state={refinement.data?.codebaseSummary} />
        <StageRuns agent="idea_refinement" />
      </div>
    </section>
  )
}
