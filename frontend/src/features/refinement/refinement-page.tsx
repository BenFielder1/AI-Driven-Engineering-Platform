import { zodResolver } from '@hookform/resolvers/zod'
import { Link } from '@tanstack/react-router'
import { useState, type ReactNode } from 'react'
import { useForm } from 'react-hook-form'

import { Alert, ErrorSummary, type SummaryError } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextAreaField } from '../../components/ui/textarea-field'
import { applyFieldErrors, errorMessage } from '../../lib/errors'
import { useDocumentTitle } from '../../lib/use-document-title'
import { useLatestArtifact } from '../artifacts/artifacts-api'
import { MarkdownView } from '../artifacts/markdown-view'
import { phaseLabels } from '../projects/labels'
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

/** FR-55, FR-70: the Repository Analyst's codebase summary of an iteration, when the version has one. */
function CodebaseSummary({ projectId, releaseVersionId }: { projectId: string; releaseVersionId: string | undefined }) {
  const summary = useLatestArtifact(projectId, 'codebase_summary', true, releaseVersionId)
  if (!summary.data) {
    return null
  }
  return (
    <StageSection id="codebase-summary" title={`Repository Analyst summary (v${String(summary.data.version)})`}>
      <p className="text-sm text-muted-foreground">
        What the Repository Analyst found in the existing code when this version started. The other agents read it as context.
      </p>
      <details>
        <summary className="cursor-pointer text-sm font-medium">Show the codebase summary</summary>
        <div className="mt-2">
          <MarkdownView content={summary.data.content} label={`Codebase summary v${String(summary.data.version)}`} />
        </div>
      </details>
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
        <CodebaseSummary projectId={project.id} releaseVersionId={releaseVersionId} />
        <StageRuns agent="idea_refinement" />
      </div>
    </section>
  )
}
