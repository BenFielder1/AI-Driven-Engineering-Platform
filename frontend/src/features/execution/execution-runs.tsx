import { Link } from '@tanstack/react-router'
import { ChevronDown, ChevronRight } from 'lucide-react'
import { useId, useState, type ReactNode } from 'react'

import { StatusBadge, type StatusTone } from '../../components/shared/status-badge'
import { Badge } from '../../components/ui/badge'
import { ErrorState, LoadingState } from '../../components/ui/states'
import {
  artifactTypeLabels,
  formatDateTime,
  formatDuration,
  formatNumber,
  planRunStatusLabels,
  stepAttemptOutcomeLabels,
  stepStatusLabels,
} from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { Commit, ViolationList } from './execution-steps'
import {
  useStepAttempts,
  type ExecutionRun,
  type ExecutionStepSummary,
  type PlanRunStatus,
  type StepAttempt,
  type ToolCall,
} from './execution-api'
import { stepTones } from './step-tones'

const runTones: Record<PlanRunStatus, StatusTone> = {
  pending: 'neutral',
  running: 'info',
  paused: 'warning',
  completed: 'success',
  blocked: 'destructive',
  superseded: 'neutral',
}

const outcomeTones = { accepted: 'success', rejected: 'danger', error: 'danger' } as const

const plural = (n: number, one: string, many = `${one}s`) => `${String(n)} ${n === 1 ? one : many}`

/** "Implementation plan v1" / "Optimisation plan v2". */
const runTitle = (run: Pick<ExecutionRun, 'planType' | 'planVersion'>) =>
  `${artifactTypeLabels[run.planType]} v${String(run.planVersion)}`

/** FR-57: the development run, or the review-fix run of a cycle. */
const runKind = (run: Pick<ExecutionRun, 'cycle' | 'planType'>) =>
  run.planType === 'implementation_plan'
    ? 'Development run'
    : run.cycle === null
      ? 'Review-fix run'
      : `Review-fix run, cycle ${String(run.cycle)}`

/** A tool call's arguments as compact JSON, cut for display (the full value is in the title). */
function argumentsText(value: unknown): string {
  if (value === undefined) {
    return ''
  }
  let text: string
  try {
    text = JSON.stringify(value)
  } catch {
    text = '(arguments could not be shown)'
  }
  return text.length > 160 ? `${text.slice(0, 159)}…` : text
}

function ToolCalls({ calls, attempt }: { calls: ToolCall[]; attempt: number }) {
  if (calls.length === 0) {
    return <p className="text-xs text-muted-foreground">No repository tool calls.</p>
  }
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[28rem] border-collapse text-xs">
        <caption className="sr-only">Repository tool calls of attempt {attempt}</caption>
        <thead>
          <tr className="border-b border-border/60 text-left text-muted-foreground">
            <th scope="col" className="py-1 pr-2 font-medium">Tool</th>
            <th scope="col" className="py-1 pr-2 font-medium">Arguments</th>
            <th scope="col" className="py-1 pr-2 text-right font-medium">Bytes</th>
            <th scope="col" className="py-1 font-medium">Result</th>
          </tr>
        </thead>
        <tbody>
          {calls.map((c, i) => {
            const args = argumentsText(c.arguments)
            return (
              <tr key={`${c.tool}-${String(i)}`} className="border-b border-border/40 align-top last:border-b-0">
                <th scope="row" className="py-1 pr-2 text-left font-normal">
                  <code>{c.tool}</code>
                </th>
                <td className="py-1 pr-2 break-all">
                  <code title={args}>{args}</code>
                </td>
                <td className="py-1 pr-2 text-right tabular-nums">{formatNumber(c.bytes)}</td>
                <td className="py-1">{c.truncated ? 'Cut to the tool limit' : 'Complete'}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

function AttemptItem({ projectId, attempt }: { projectId: string; attempt: StepAttempt }) {
  const { run } = attempt
  return (
    <li aria-label={`Attempt ${String(attempt.attempt)}`} className="flex flex-col gap-2 rounded-lg border border-border/60 p-3 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-semibold">Attempt {attempt.attempt}</span>
        <Badge tone={outcomeTones[attempt.outcome]}>{stepAttemptOutcomeLabels[attempt.outcome]}</Badge>
        <time dateTime={attempt.createdAt} className="text-xs text-muted-foreground">
          {formatDateTime(attempt.createdAt)}
        </time>
      </div>
      {run ? (
        <dl className="flex flex-wrap gap-x-5 gap-y-1 text-xs">
          <div className="flex gap-1">
            <dt className="text-muted-foreground">Model</dt>
            <dd>
              <code>{run.model}</code>
            </dd>
          </div>
          <div className="flex gap-1">
            <dt className="text-muted-foreground">Tokens in / out</dt>
            <dd className="tabular-nums">
              {formatNumber(run.inputTokens)} / {formatNumber(run.outputTokens)}
            </dd>
          </div>
          <div className="flex gap-1">
            <dt className="text-muted-foreground">Duration</dt>
            <dd className="tabular-nums">{formatDuration(run.durationMs)}</dd>
          </div>
          <div className="flex gap-1">
            <dt className="text-muted-foreground">Agent run</dt>
            <dd>
              <Link to="/projects/$projectId/runs" params={{ projectId }} search={{ run: run.id }} className="text-primary underline">
                Details{' '}<span className="sr-only">of the agent run for attempt {attempt.attempt}</span>
              </Link>
            </dd>
          </div>
        </dl>
      ) : (
        <p className="text-xs text-muted-foreground">The agent run of this attempt is no longer recorded.</p>
      )}
      {attempt.violations.length > 0 && (
        <ViolationList title={`Guardian violations (${String(attempt.violations.length)})`} violations={attempt.violations} />
      )}
      {attempt.warnings.length > 0 && (
        <ViolationList title={`Guardian warnings (${String(attempt.warnings.length)})`} violations={attempt.warnings} />
      )}
      {attempt.producedFiles.length > 0 && (
        <p className="text-xs">
          Committed: {attempt.producedFiles.map((f) => f.path).join(', ')}
        </p>
      )}
      <div className="flex flex-col gap-1">
        <span className="text-xs font-semibold">Tool calls ({attempt.toolCalls.length})</span>
        <ToolCalls calls={attempt.toolCalls} attempt={attempt.attempt} />
      </div>
    </li>
  )
}

/** FR-57: a step's attempt history, loaded when first opened. */
export function AttemptHistory({ stepId, stepNumber, attempts }: { stepId: string; stepNumber: number; attempts: number }) {
  const { project } = useProjectAccess()
  const [open, setOpen] = useState(false)
  const regionId = useId()
  const query = useStepAttempts(project.id, stepId, open)
  return (
    <div className="flex flex-col gap-2">
      <button
        type="button"
        aria-expanded={open}
        aria-controls={regionId}
        onClick={() => {
          setOpen((o) => !o)
        }}
        className="self-start text-sm text-primary underline"
      >
        {open ? 'Hide' : 'Show'} attempt history ({attempts}){' '}<span className="sr-only">of step {stepNumber}</span>
      </button>
      <div id={regionId} hidden={!open}>
        {open &&
          (query.isPending ? (
            <LoadingState label="Loading attempts…" />
          ) : query.isError ? (
            <ErrorState error={query.error} title="The attempts couldn't load" onRetry={() => void query.refetch()} />
          ) : query.data.length === 0 ? (
            <p className="text-sm text-muted-foreground">No attempts yet.</p>
          ) : (
            <ol aria-label={`Attempts of step ${String(stepNumber)}`} className="flex flex-col gap-2">
              {query.data.map((a) => (
                <AttemptItem key={a.attempt} projectId={project.id} attempt={a} />
              ))}
            </ol>
          ))}
      </div>
    </div>
  )
}

/** FR-57: declared vs produced files of a step. */
function Files({ step }: { step: ExecutionStepSummary }) {
  const produced = new Set(step.producedFiles.map((f) => f.path))
  const declared = new Set(step.declaredFiles.map((f) => f.path))
  const undeclared = step.producedFiles.filter((f) => !declared.has(f.path))
  return (
    <details className="text-sm">
      <summary className="cursor-pointer">
        Files: {plural(step.declaredFiles.length, 'declared', 'declared')}, {plural(step.producedFiles.length, 'produced', 'produced')}
      </summary>
      <ul className="mt-2 flex flex-col gap-0.5">
        {step.declaredFiles.map((f) => (
          <li key={`d:${f.path}`} className="break-all">
            <code className="text-xs">{f.path}</code>{' '}
            <span className="text-xs text-muted-foreground">
              ({f.action}, {produced.has(f.path) ? 'produced' : 'not produced'})
            </span>
          </li>
        ))}
        {undeclared.map((f) => (
          <li key={`p:${f.path}`} className="break-all">
            <code className="text-xs">{f.path}</code> <span className="text-xs text-muted-foreground">(produced, not declared)</span>
          </li>
        ))}
      </ul>
    </details>
  )
}

/** FR-57: what the run adds to the active run's detailed step: specialist, declared vs produced files, attempts. */
export function StepRunExtras({ step }: { step: ExecutionStepSummary }) {
  return (
    <div className="flex flex-col gap-2 border-t border-border/60 pt-2">
      <p className="text-sm">
        <span className="text-muted-foreground">Specialist </span>
        {step.agentKey ?? 'Code Generator'}
      </p>
      <Files step={step} />
      <AttemptHistory stepId={step.id} stepNumber={step.number} attempts={step.attempt} />
    </div>
  )
}

/** One step of a (non-active) run, from the run summary. */
function RunStepItem({ step, headingLevel }: { step: ExecutionStepSummary; headingLevel: 4 | 5 }) {
  const titleId = useId()
  const Heading = headingLevel === 4 ? 'h4' : 'h5'
  return (
    <li aria-labelledby={titleId} className="flex flex-col gap-2 rounded-lg border border-border bg-card p-3">
      <div className="flex flex-wrap items-center gap-2">
        <Heading id={titleId} className="text-base font-semibold break-words">
          Step {step.number}: {step.title}
        </Heading>
        <Badge tone={stepTones[step.status]}>{stepStatusLabels[step.status]}</Badge>
      </div>
      <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm">
        <div className="flex gap-1">
          <dt className="text-muted-foreground">Specialist</dt>
          <dd>{step.agentKey ?? 'Code Generator'}</dd>
        </div>
        <div className="flex gap-1">
          <dt className="text-muted-foreground">Attempts</dt>
          <dd>{step.attempt}</dd>
        </div>
        {step.commitSha && (
          <div className="flex gap-1">
            <dt className="text-muted-foreground">Commit</dt>
            <dd>
              <Commit sha={step.commitSha} url={step.commitUrl} />
            </dd>
          </div>
        )}
      </dl>
      <Files step={step} />
      <AttemptHistory stepId={step.id} stepNumber={step.number} attempts={step.attempt} />
    </li>
  )
}

function RunMeta({ run }: { run: ExecutionRun }) {
  return (
    <dl className="flex flex-wrap gap-x-5 gap-y-1 text-xs text-muted-foreground">
      <div className="flex gap-1">
        <dt>Progress</dt>
        <dd className="font-mono text-foreground tabular-nums">
          {run.completedCount} / {run.stepCount} completed
        </dd>
      </div>
      {run.startedAt && (
        <div className="flex gap-1">
          <dt>Started</dt>
          <dd className="text-foreground">{formatDateTime(run.startedAt)}</dd>
        </div>
      )}
      {run.completedAt && (
        <div className="flex gap-1">
          <dt>Completed</dt>
          <dd className="text-foreground">{formatDateTime(run.completedAt)}</dd>
        </div>
      )}
      <div className="flex gap-1">
        <dt>Commits</dt>
        <dd className="font-mono text-foreground tabular-nums">{run.commitCount}</dd>
      </div>
      <div className="flex gap-1">
        <dt>Tokens</dt>
        <dd className="font-mono text-foreground tabular-nums">{formatNumber(run.tokens)}</dd>
      </div>
    </dl>
  )
}

/** FR-56, FR-57: a review-fix run links to the review report whose optimisation plan it runs. */
function ReportLink({ cycle, reportId }: { cycle: number; reportId: string }) {
  const { project } = useProjectAccess()
  return (
    <Link
      to="/projects/$projectId/review"
      params={{ projectId: project.id }}
      search={{ cycle, report: reportId }}
      className="self-start text-sm text-primary underline"
    >
      See the cycle {cycle} review report
    </Link>
  )
}

/** FR-57: one run as a collapsible group (header: plan and version, cycle, status, n / m, times, commits, tokens). */
function RunGroup({
  run,
  expanded,
  onToggle,
  active,
  activeSteps,
  headingLevel,
}: {
  run: ExecutionRun
  expanded: boolean
  onToggle: () => void
  active: boolean
  /** The active run's detailed steps (live), in place of the summary steps. */
  activeSteps: ReactNode
  headingLevel: 3 | 4
}) {
  const titleId = useId()
  const bodyId = useId()
  const Heading = headingLevel === 3 ? 'h3' : 'h4'
  const Chevron = expanded ? ChevronDown : ChevronRight
  return (
    <li id={`run-${run.id}`} aria-labelledby={titleId} className="flex scroll-mt-24 flex-col gap-3 rounded-xl border border-border/60 bg-card/70 p-4 shadow-sm backdrop-blur-md">
      <div className="flex flex-col gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <Heading id={titleId} className="text-base">
            <button
              type="button"
              aria-expanded={expanded}
              aria-controls={bodyId}
              onClick={onToggle}
              className="inline-flex min-h-11 items-center gap-1 text-left font-semibold"
            >
              <Chevron className="size-4 shrink-0" aria-hidden />
              {runTitle(run)} · {runKind(run)}
            </button>
          </Heading>
          <StatusBadge tone={runTones[run.status]} pulse={run.status === 'running'}>
            {planRunStatusLabels[run.status]}
          </StatusBadge>
          {active && <Badge tone="info">Active run</Badge>}
        </div>
        <RunMeta run={run} />
        {run.complianceReportId !== null && run.cycle !== null && (
          <ReportLink cycle={run.cycle} reportId={run.complianceReportId} />
        )}
        {run.superseded && run.supersededReason && <p className="text-sm">Superseded: {run.supersededReason}</p>}
      </div>
      <div id={bodyId} hidden={!expanded}>
        {expanded &&
          (active && activeSteps ? (
            activeSteps
          ) : (
            <ol aria-label={`Steps of ${runTitle(run)}`} className="flex flex-col gap-3">
              {run.steps.map((s) => (
                <RunStepItem key={s.id} step={s} headingLevel={headingLevel === 3 ? 4 : 5} />
              ))}
            </ol>
          ))}
      </div>
    </li>
  )
}

/**
 * FR-57: every execution run of the release version on screen: the development run and one review-fix run per review
 * cycle, each a collapsible group; the active run is expanded and shows the live, detailed steps (`activeSteps`).
 * Runs of invalidated plans are listed under "Superseded" with the reason.
 */
export function ExecutionRuns({
  runs,
  activeRunId,
  activeSteps,
}: {
  runs: ExecutionRun[]
  activeRunId: string | null
  activeSteps: ReactNode
}) {
  const [toggled, setToggled] = useState<Record<string, boolean>>({})
  const live = runs.filter((r) => !r.superseded)
  const superseded = runs.filter((r) => r.superseded)
  // The active run is open; without one (e.g. a past release version), the latest run is.
  const defaultOpen = activeRunId ?? live.at(-1)?.id ?? null
  const isOpen = (id: string) => toggled[id] ?? id === defaultOpen
  const toggle = (id: string) => {
    setToggled((t) => ({ ...t, [id]: !isOpen(id) }))
  }

  return (
    <div className="flex flex-col gap-4">
      <ul aria-label="Runs" className="flex flex-col gap-3">
        {live.map((run) => (
          <RunGroup
            key={run.id}
            run={run}
            expanded={isOpen(run.id)}
            onToggle={() => {
              toggle(run.id)
            }}
            active={run.id === activeRunId}
            activeSteps={activeSteps}
            headingLevel={3}
          />
        ))}
      </ul>
      {superseded.length > 0 && (
        <section aria-labelledby="superseded-runs-title" className="flex flex-col gap-3">
          <h3 id="superseded-runs-title" className="text-lg">
            Superseded
          </h3>
          <ul aria-label="Superseded runs" className="flex flex-col gap-3">
            {superseded.map((run) => (
              <RunGroup
                key={run.id}
                run={run}
                expanded={toggled[run.id] ?? false}
                onToggle={() => {
                  setToggled((t) => ({ ...t, [run.id]: !(t[run.id] ?? false) }))
                }}
                active={false}
                activeSteps={null}
                headingLevel={4}
              />
            ))}
          </ul>
        </section>
      )}
    </div>
  )
}

/** Loading / error wrapper for the runs list. */
export function RunsState({ isPending, error, onRetry }: { isPending: boolean; error: unknown; onRetry: () => void }) {
  if (isPending) {
    return <LoadingState label="Loading execution runs…" />
  }
  return <ErrorState error={error} title="The execution runs couldn't load" onRetry={onRetry} />
}
