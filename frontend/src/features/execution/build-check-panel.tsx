import type { ReactNode } from 'react'
import { TriangleAlert, Ban, CircleCheck, CircleDashed, Clock, Info, LoaderCircle, Wrench, CircleX } from 'lucide-react'

import { Badge, type BadgeTone } from '../../components/ui/badge'
import { ErrorState } from '../../components/ui/states'
import { EvidenceBlock } from '../../components/shared/evidence-block'
import { panelClass } from '../../components/shared/panel-class'
import { cn } from '../../lib/utils'
import { buildCheckStatusLabels, formatDuration, stepStatusLabels } from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import {
  useBuildChecks,
  type BuildCheck,
  type BuildCheckFailure,
  type BuildCheckSource,
  type BuildCheckStatus,
} from './build-checks-api'
import type { ExecutionStep } from './execution-api'
import { stepTones } from './step-tones'

/** The fields of an execution step (or a run's step summary) the fix card needs (FR-256, D-0.6.1-23). */
export type FixStep = Pick<ExecutionStep, 'id' | 'number' | 'title' | 'status' | 'origin' | 'buildCheckId'>

const statusTones: Record<BuildCheckStatus, BadgeTone> = {
  waiting: 'info',
  passed: 'success',
  failed: 'danger',
  skipped: 'neutral',
  exhausted: 'warning',
  cancelled: 'neutral',
}

type Icon = typeof CircleCheck

/** How a source stands, in words: `busy` adds a spinner (reduced motion: still). Colour and icon only repeat the label. */
interface SourceState {
  label: string
  tone: BadgeTone
  icon: Icon
  busy?: boolean
}

/** Where a pending source stands (D-0.6.1-23: a preview's deployment state, or a check's run status). */
function pendingState(source: BuildCheckSource): SourceState {
  switch (source.status) {
    case 'queued':
      return { label: 'Queued', tone: 'neutral', icon: Clock }
    case 'building':
      return { label: 'Building…', tone: 'info', icon: LoaderCircle, busy: true }
    case 'in_progress':
      return { label: 'Running…', tone: 'info', icon: LoaderCircle, busy: true }
    case 'ready':
      return { label: 'Ready', tone: 'success', icon: CircleCheck }
    case 'completed':
      return { label: 'Finishing…', tone: 'info', icon: LoaderCircle, busy: true }
    case 'error':
      return { label: 'Failed', tone: 'danger', icon: CircleX }
    case 'canceled':
      return { label: 'Cancelled', tone: 'neutral', icon: Ban }
    default:
      return { label: 'Waiting', tone: 'neutral', icon: CircleDashed }
  }
}

/** A source's result: its conclusion when it has one, else where it stands. */
function sourceState(source: BuildCheckSource): SourceState {
  switch (source.conclusion) {
    case null:
      return pendingState(source)
    case 'success':
      return { label: source.kind === 'preview' ? 'Ready' : 'Passed', tone: 'success', icon: CircleCheck }
    case 'failure':
      return { label: 'Failed', tone: 'danger', icon: CircleX }
    case 'timed_out':
      return { label: 'Timed out', tone: 'danger', icon: Clock }
    case 'unavailable':
      return { label: 'Unavailable', tone: 'warning', icon: TriangleAlert }
    case 'action_required':
      return { label: 'Action required', tone: 'warning', icon: TriangleAlert }
    case 'cancelled':
      return { label: 'Cancelled', tone: 'neutral', icon: Ban }
    case 'skipped':
      return { label: 'Skipped', tone: 'neutral', icon: Ban }
    case 'neutral':
      return { label: 'Neutral', tone: 'neutral', icon: Info }
    default: {
      const words = source.conclusion.replaceAll('_', ' ')
      return { label: words.charAt(0).toUpperCase() + words.slice(1), tone: 'neutral', icon: Info }
    }
  }
}


/** "A", "A and B", "A, B and C". */
function listText(names: string[]): string {
  return names.length <= 1 ? (names[0] ?? '') : `${names.slice(0, -1).join(', ')} and ${names.at(-1) ?? ''}`
}

const plural = (n: number, one: string, many = `${one}s`) => `${String(n)} ${n === 1 ? one : many}`

/** "Attempt 2 of 5"; attempt 0 checks the plan's own commit, before any fix (§4.47). */
function attemptText(check: BuildCheck, max: number): string {
  return check.attempt === 0 ? 'Initial check' : `Attempt ${String(check.attempt)} of ${String(max)}`
}

/** The fix planned from a check is fix `attempt + 1`, as on the steps' "Build fix · attempt n" badge (FR-259). */
const fixLabel = (check: BuildCheck) => `Build fix · attempt ${String(check.attempt + 1)}`

/**
 * D-0.6.1-23: why a check ended `exhausted`, in words. `attempts_used` counts the fixes actually planned in the cycle (not
 * the attempt number: a cancelled or superseded check leaves gaps), and never says "after 0 fix attempts".
 */
function exhaustedText(check: BuildCheck, fixes: number): string {
  switch (check.endReason) {
    case 'fix_plan_invalid':
      return "Tevrin couldn't plan a valid fix for the build failures. Review will include the failures."
    case 'escalation_continue':
      return 'Fix step blocked; you chose to continue to review. Review will include the failures.'
    case 'job_failed':
      return 'The build check stopped because of an error. Review will include any failures.'
    default:
      return fixes > 0
        ? `Build still failing after ${plural(fixes, 'fix attempt')}. Review will include the failures.`
        : 'Build still failing. Review will include the failures.'
  }
}

/** What the check is doing or concluded, in words (SPEC §7 part 2, D-0.6.1-23). */
function statusLine(check: BuildCheck, fixes: number): string {
  switch (check.status) {
    case 'waiting': {
      const pending = check.sources.filter((s) => s.conclusion === null && !s.informational).map((s) => s.name)
      return pending.length > 0 ? `Waiting for ${listText(pending)}` : 'Waiting for the preview build and CI checks'
    }
    case 'passed':
      return 'Build passed, review started'
    case 'failed':
      switch (check.fixPlanning) {
        case 'planned':
          return check.fixStepNumbers.length > 0
            ? `Build failed: fix ${check.fixStepNumbers.length === 1 ? 'step' : 'steps'} ${listText(check.fixStepNumbers.map(String))} planned`
            : 'Build failed: fix planned'
        case 'failed':
          return "Build failed: Tevrin couldn't plan a valid fix"
        default:
          return 'Build failed: planning a fix'
      }
    case 'skipped':
      // D-0.6.1-23: also skipped with no preview and CI checks still unreadable at the checks timeout.
      return check.sources.some((s) => s.kind === 'check' && s.conclusion === 'unavailable')
        ? "No preview target, and the CI checks couldn't be read"
        : 'No preview target or required checks'
    case 'exhausted':
      return exhaustedText(check, fixes)
    case 'cancelled':
      return check.endReason === 'cancelled_by_change'
        ? 'Cancelled: the plan or version changed, or execution was stopped'
        : 'Cancelled: the version changed or execution was stopped'
  }
}

/** A spinner that stays still under reduced motion (the label beside it carries the meaning). */
function StateIcon({ icon: IconComponent, busy, className }: { icon: Icon; busy?: boolean; className?: string }) {
  return <IconComponent aria-hidden="true" className={cn('size-4 shrink-0', busy && 'motion-safe:animate-spin', className)} />
}

function SourceRow({ source }: { source: BuildCheckSource }) {
  const state = sourceState(source)
  const informational = source.informational
  return (
    <li className="flex min-w-0 flex-col gap-0.5" data-testid="build-check-source" data-source-name={source.name}>
      <div className="flex min-w-0 flex-wrap items-center gap-2">
        <StateIcon icon={state.icon} busy={state.busy} className="text-muted-foreground" />
        <span className="text-muted-foreground">{source.kind === 'preview' ? 'Preview' : 'CI check'}</span>
        {source.url ? (
          <a href={source.url} target="_blank" rel="noreferrer" className="break-all text-primary underline">
            {source.name}
            <span className="sr-only"> (opens in a new tab)</span>
          </a>
        ) : (
          <span className="break-all">{source.name}</span>
        )}
        <Badge tone={state.tone}>{state.label}</Badge>
        {informational && <Badge tone="neutral">Not counted</Badge>}
      </div>
      {source.message && <p className="pl-6 text-xs break-words text-muted-foreground">{source.message}</p>}
    </li>
  )
}

function SourceList({ sources, label }: { sources: BuildCheckSource[]; label: string }) {
  if (sources.length === 0) {
    return <p className="text-sm text-muted-foreground">No results yet.</p>
  }
  return (
    <ul aria-label={label} className="flex flex-col gap-1.5 text-sm">
      {sources.map((s) => (
        <SourceRow key={`${s.kind}:${s.name}`} source={s} />
      ))}
    </ul>
  )
}

function FailureList({ failures, label, open }: { failures: BuildCheckFailure[]; label: string; open: boolean }) {
  if (failures.length === 0) {
    return null
  }
  return (
    <ul aria-label={label} className="flex flex-col gap-2">
      {failures.map((f) => (
        <li key={f.failureId} className="flex min-w-0 flex-col gap-1 rounded-lg border border-border/60 p-2 text-sm">
          <p className="break-words">
            <code className="text-xs break-all text-muted-foreground">{f.failureId}</code> {f.message}
          </p>
          {f.excerpt && (
            <details open={open} className="min-w-0">
              <summary className="cursor-pointer text-xs">Log excerpt</summary>
              <div className="mt-1">
                <EvidenceBlock text={f.excerpt} label={`Log excerpt for ${f.failureId}`} testId="build-check-excerpt" />
              </div>
            </details>
          )}
        </li>
      ))}
    </ul>
  )
}

const shortSha = (sha: string) => (
  <code className="text-xs" title={sha}>
    {sha.slice(0, 7)}
  </code>
)

function Times({ check }: { check: BuildCheck }) {
  const took = check.completedAt === null ? null : Date.parse(check.completedAt) - Date.parse(check.startedAt)
  return (
    <dl className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
      <div className="flex gap-1">
        <dt>Started</dt>
        <dd>
          <RelativeTime iso={check.startedAt} />
        </dd>
      </div>
      {check.completedAt && (
        <div className="flex gap-1">
          <dt>Finished</dt>
          <dd>
            <RelativeTime iso={check.completedAt} />
          </dd>
        </div>
      )}
      {took !== null && Number.isFinite(took) && took >= 0 && (
        <div className="flex gap-1">
          <dt>Took</dt>
          <dd className="tabular-nums">{formatDuration(took)}</dd>
        </div>
      )}
    </dl>
  )
}

/** One step of the timeline: a dot on the connecting line, and a card (the current one outlined and marked). */
function TimelineItem({
  current,
  testId,
  children,
  label,
}: {
  current: boolean
  testId: string
  label: string
  children: ReactNode
}) {
  return (
    <li aria-label={label} aria-current={current ? 'step' : undefined} data-testid={testId} className="relative min-w-0">
      <span
        aria-hidden="true"
        className={cn(
          'absolute top-4 -left-[1.4rem] size-3 rounded-full border-2 bg-card',
          current ? 'border-primary bg-primary' : 'border-border',
        )}
      />
      <div className={cn('flex min-w-0 flex-col gap-2 rounded-lg border bg-card p-3', current ? 'border-primary' : 'border-border/60')}>
        {children}
      </div>
    </li>
  )
}

/** Collapsible content of an earlier card: a one-line summary that expands. */
function Collapsible({ summary, open, children }: { summary: ReactNode; open: boolean; children: ReactNode }) {
  return (
    <details open={open} className="min-w-0">
      <summary className="cursor-pointer text-sm">{summary}</summary>
      <div className="mt-2 flex min-w-0 flex-col gap-2">{children}</div>
    </details>
  )
}

/** D-0.6.1-23: a build card: commit, each source with its live status and link, the failures, and the times. */
function BuildCard({
  check,
  max,
  current,
  expanded,
}: {
  check: BuildCheck
  max: number
  /** The newest check of the cycle: shown in full, never collapsed. */
  current: boolean
  /** An earlier card shown open (the previous attempt while the next one waits, so what is being fixed stays in view). */
  expanded: boolean
}) {
  const label = attemptText(check, max)
  const heading = (
    <>
      <span className="font-medium">Build · {label}</span> · <Badge tone={statusTones[check.status]}>{buildCheckStatusLabels[check.status]}</Badge>
      {' · '}commit {shortSha(check.headSha)}
      {check.failures.length > 0 && <> · {plural(check.failures.length, 'failure')}</>}
      {current && <span className="text-xs font-medium"> (current)</span>}
    </>
  )
  const body = (
    <>
      <SourceList sources={check.sources} label={`Sources of the ${label.toLowerCase()}`} />
      <FailureList
        failures={check.failures}
        label={`Failures of the ${label.toLowerCase()}`}
        open={current || expanded}
      />
      <Times check={check} />
    </>
  )
  return (
    <TimelineItem current={current && check.fixPlanning === 'none'} testId="build-check-build-card" label={`Build: ${label}`}>
      {current ? (
        <>
          <p className="text-sm">{heading}</p>
          {body}
        </>
      ) : (
        <Collapsible summary={heading} open={expanded}>
          {body}
        </Collapsible>
      )}
    </TimelineItem>
  )
}

/**
 * The fix steps planned from `check`: its `fixStepNumbers` joined to the execution steps on the page that name the check
 * (`buildCheckId`), so their status is live with the step list.
 */
function fixStepsOf(check: BuildCheck, steps: FixStep[]): { number: number; step: FixStep | undefined }[] {
  return check.fixStepNumbers.map((number) => ({
    number,
    step: steps.find((s) => s.buildCheckId === check.id && s.number === number),
  }))
}

/** D-0.6.1-23: a fix card: the fix being planned, then its steps with their live execution status. */
function FixCard({
  check,
  steps,
  current,
  newest,
}: {
  check: BuildCheck
  steps: FixStep[]
  /** The last card of the timeline (the fix in progress). */
  current: boolean
  /** The fix of the cycle's newest check: the next build hasn't started yet. */
  newest: boolean
}) {
  const label = fixLabel(check)
  const fixSteps = fixStepsOf(check, steps)
  const done = fixSteps.filter((f) => f.step?.status === 'completed').length

  let summary: ReactNode
  let body: ReactNode = null
  switch (check.fixPlanning) {
    case 'planning':
      summary = (
        <span className="inline-flex items-center gap-2">
          <StateIcon icon={LoaderCircle} busy className="text-primary" />
          Planning a fix…
        </span>
      )
      break
    case 'failed':
      summary = (
        <span className="inline-flex items-center gap-2">
          <StateIcon icon={CircleX} className="text-destructive" />
          Couldn&apos;t plan a valid fix
        </span>
      )
      break
    default:
      summary =
        fixSteps.length === 0 ? (
          'Fix planned'
        ) : (
          <>
            {fixSteps.length === 1 ? 'Step' : 'Steps'} {listText(fixSteps.map((f) => String(f.number)))} · {done} of{' '}
            {fixSteps.length} completed
          </>
        )
      body =
        fixSteps.length > 0 ? (
          <>
            <ol aria-label={`Steps of ${label.toLowerCase()}`} className="flex flex-col gap-1.5 text-sm">
              {fixSteps.map(({ number, step }) => (
                <li key={number} className="flex min-w-0 flex-wrap items-center gap-2">
                  {step?.status === 'in_progress' ? (
                    <StateIcon icon={LoaderCircle} busy className="text-primary" />
                  ) : (
                    <StateIcon icon={Wrench} className="text-muted-foreground" />
                  )}
                  <span className="break-words">
                    Step {number}
                    {step && <>: {step.title}</>}
                  </span>
                  {step ? (
                    <Badge tone={stepTones[step.status]}>{stepStatusLabels[step.status]}</Badge>
                  ) : (
                    <span className="text-xs text-muted-foreground">(status not loaded)</span>
                  )}
                </li>
              ))}
            </ol>
            {newest && done === fixSteps.length && (
              <p className="text-xs text-muted-foreground">Fix steps done: the next build starts once they&apos;re pushed.</p>
            )}
          </>
        ) : null
  }

  const heading = (
    <>
      <span className="font-medium">{label}</span> · {summary}
    </>
  )
  return (
    <TimelineItem current={current} testId="build-check-fix-card" label={label}>
      {newest || body === null ? (
        <>
          <p className="text-sm">{heading}</p>
          {body}
        </>
      ) : (
        <Collapsible summary={heading} open={false}>
          {body}
        </Collapsible>
      )}
    </TimelineItem>
  )
}

/**
 * FR-259 amended by D-0.6.1-23 (§7 part 2): the build check of the version's current review cycle, above the step list,
 * as a card timeline, oldest at the top and the current card (outlined, "current") at the bottom: each attempt's build
 * card (commit, each source with its live status and link, the failures with their log excerpts, the times), then the
 * fix card when it failed (planning, then the fix steps with their live status from the step list), then the next
 * build. A summary on top says the state and "Attempt n of max", in a polite live region. Earlier cards collapse to one
 * line (the previous build stays open while the next one waits, so what is being fixed stays in view). Renders nothing
 * while loading or when the cycle has no check (0.6.0 behaviour, or `maxBuildFixAttempts` 0). A check's `reviewCycle`
 * is the cycle it ran in, so it gates review `reviewCycle + 1`. `cycle` null: the newest check's cycle (once the project
 * has left Execution, or a past release version). `steps`: the execution steps on the page (the fix steps' status).
 */
export function BuildCheckPanel({
  projectId,
  versionId,
  cycle,
  maxAttempts,
  steps,
}: {
  projectId: string
  versionId: string | null
  cycle: number | null
  maxAttempts: number
  steps: FixStep[]
}) {
  const query = useBuildChecks(projectId, versionId)
  if (query.isPending) {
    return null
  }
  if (query.isError) {
    return (
      <ErrorState error={query.error} title="The build check couldn't load" onRetry={() => void query.refetch()} />
    )
  }
  const shownCycle = cycle ?? query.data[0]?.reviewCycle ?? null
  // The API lists newest first; the timeline reads oldest to newest.
  const checks = query.data.filter((c) => c.reviewCycle === shownCycle)
  const current = checks[0]
  if (!current) {
    return null
  }
  const timeline = [...checks].reverse()
  const fixes = checks.filter((c) => c.fixStepNumbers.length > 0).length
  const waiting = current.status === 'waiting'
  const previous = checks[1]

  return (
    <section
      aria-labelledby="build-check-title"
      data-testid="build-check-panel"
      className={cn(panelClass, 'flex min-w-0 flex-col gap-3 p-4')}
    >
      <div className="flex flex-wrap items-center gap-2">
        <h3 id="build-check-title" className="text-lg">
          Build check
        </h3>
        <Badge tone={statusTones[current.status]}>{buildCheckStatusLabels[current.status]}</Badge>
      </div>
      <div aria-live="polite" className="text-sm">
        <p data-testid="build-check-summary" className="flex items-start gap-2">
          {(waiting || current.fixPlanning === 'planning') && <StateIcon icon={LoaderCircle} busy className="mt-0.5 text-primary" />}
          {current.status === 'exhausted' && <StateIcon icon={TriangleAlert} className="mt-0.5 text-warning" />}
          <span>
            {attemptText(current, maxAttempts)} · {statusLine(current, fixes)}
          </span>
        </p>
      </div>
      <p className="text-xs text-muted-foreground">Before review cycle {current.reviewCycle + 1}</p>
      <ol aria-label="Build check timeline" className="ml-2 flex min-w-0 flex-col gap-3 border-l-2 border-border pl-4">
        {timeline.map((check) => {
          const newest = check.id === current.id
          return [
            <BuildCard
              key={`build:${check.id}`}
              check={check}
              max={maxAttempts}
              current={newest}
              expanded={waiting && check.id === previous?.id}
            />,
            check.fixPlanning !== 'none' && (
              <FixCard key={`fix:${check.id}`} check={check} steps={steps} current={newest} newest={newest} />
            ),
          ]
        })}
      </ol>
    </section>
  )
}
