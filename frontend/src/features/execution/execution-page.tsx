import { useState, type ReactNode } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Badge, type BadgeTone } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage, errorReasons } from '../../lib/errors'
import { useDocumentTitle } from '../../lib/use-document-title'
import { PullRequestSection } from '../git/pull-request-section'
import { useGates } from '../gates/gates-api'
import type { ArtifactType } from '../artifacts/artifacts-api'
import { artifactTypeLabels, executionRunStatusLabels, phaseLabels } from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { useProjectState, type ProjectState } from '../projects/projects-api'
import {
  useExecutionControl,
  useExecutionSteps,
  type ExecutionAction,
  type ExecutionRunStatus,
  type ExecutionStep,
} from './execution-api'
import { ExecutionStepItem } from './execution-steps'

const runTones: Record<ExecutionRunStatus, BadgeTone> = {
  idle: 'neutral',
  running: 'info',
  paused: 'warning',
  completed: 'success',
  blocked: 'danger',
}

const actionLabels: Record<ExecutionAction, { label: string; pending: string }> = {
  start: { label: 'Start execution', pending: 'Starting…' },
  pause: { label: 'Pause execution', pending: 'Pausing…' },
  resume: { label: 'Resume execution', pending: 'Resuming…' },
}

/** SPEC 7.1: the async result of a control call, as a toast. */
const controlToasts: Record<ExecutionAction, string> = {
  start: 'Execution started.',
  pause: 'Execution will pause after the current step.',
  resume: 'Execution resumed.',
}

interface Control {
  action: ExecutionAction
  /** Why the button is disabled; `null` when it can be used. */
  disabledReason: string | null
}

/**
 * FR-31, FR-33: steps a start can run: pending ones, rejected ones (retried first) and an interrupted `in_progress`
 * step (the next start continues it).
 */
function runnableSteps(state: ProjectState): number {
  const c = state.execution
  return c.pending + c.rejected + c.inProgress
}

/**
 * The current plan, as the backend defines it: the most recently created locked plan. An approved implementation
 * plan invalidates the optimisation plan (§4.4), so a locked optimisation plan is always the newer one.
 */
function currentPlanType(state: ProjectState): ArtifactType {
  const optimisation = state.artifacts.find((a) => a.type === 'optimization_plan')
  return (optimisation?.currentLockedVersion ?? null) !== null ? 'optimization_plan' : 'implementation_plan'
}

/** FR-31, FR-37: which control the status offers, and why it can't be used right now (if so). */
function controlFor(status: ExecutionRunStatus, state: ProjectState, hasPlan: boolean): Control | null {
  const outOfPhase =
    state.phase === 'execution'
      ? null
      : `Execution runs only in the Execution phase; the project is in ${phaseLabels[state.phase]}.`
  switch (status) {
    case 'completed':
      return null
    case 'paused':
      return { action: 'resume', disabledReason: outOfPhase }
    case 'running':
      return { action: 'pause', disabledReason: outOfPhase }
    case 'blocked':
      return {
        action: 'start',
        disabledReason: 'A step is blocked. Execution continues once an Owner decides its escalation gate.',
      }
    case 'idle':
      return {
        action: 'start',
        disabledReason:
          outOfPhase ??
          (!hasPlan
            ? 'There are no steps yet: they are created when the implementation plan is approved.'
            : runnableSteps(state) === 0
              ? 'Nothing is left to run: no step is pending, rejected (waiting for a retry) or interrupted.'
              : null),
      }
  }
}

function Stat({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="flex flex-col">
      <dt className="text-xs text-muted-foreground">{term}</dt>
      <dd className="font-mono text-lg font-medium tabular-nums">{children}</dd>
    </div>
  )
}

/** Progress summary (FR-31, FR-33, FR-37): status, current step and counts over the current plan's steps. */
function ProgressSummary({
  status,
  state,
  current,
}: {
  status: ExecutionRunStatus
  state: ProjectState
  current: ExecutionStep | undefined
}) {
  const c = state.execution
  return (
    <section aria-labelledby="execution-progress" className="flex flex-col gap-3 rounded-xl border border-border/60 bg-card/70 shadow-sm backdrop-blur-md p-4">
      <div className="flex flex-wrap items-center gap-2">
        <h3 id="execution-progress" className="text-lg">
          Progress
        </h3>
        <Badge tone={runTones[status]}>{executionRunStatusLabels[status]}</Badge>
      </div>
      <div aria-live="polite" className="text-sm">
        {current && status === 'paused' ? (
          <p>
            Pausing: step {current.number} ({current.title}) finishes, then no new step starts until execution is resumed.
          </p>
        ) : current ? (
          <p>
            Running step {current.number}: {current.title}
          </p>
        ) : status === 'running' ? (
          <p role="status" aria-busy="true">
            <span aria-hidden="true" className="mr-2 inline-block size-3 animate-pulse rounded-full bg-primary align-middle" />
            Waiting for the next step to start…
          </p>
        ) : status === 'paused' ? (
          <p>Paused: no new step starts until execution is resumed.</p>
        ) : null}
      </div>
      {c.total > 0 && (
        <>
          <label className="flex flex-col gap-1 text-sm">
            <span>
              {c.completed} of {c.total} steps completed
            </span>
            <progress value={c.completed} max={c.total} className="h-2 w-full" />
          </label>
          <dl className="grid grid-cols-3 gap-3 sm:grid-cols-6">
            <Stat term="Total">{c.total}</Stat>
            <Stat term="Pending">{c.pending}</Stat>
            <Stat term="In progress">{c.inProgress}</Stat>
            <Stat term="Completed">{c.completed}</Stat>
            <Stat term="Rejected">{c.rejected}</Stat>
            <Stat term="Blocked">{c.blocked}</Stat>
          </dl>
        </>
      )}
    </section>
  )
}

/**
 * /projects/:id/execution (SPEC §7; FR-31–FR-34, FR-37): progress summary, start/pause/resume for writers, and the
 * current plan's steps with status, attempt, Guardian violations, commit and file manifest, and once every step
 * completed the pull request or branch comparison (FR-36). Live `step` events refresh the steps, /state and the PR (FR-46).
 */
export function ExecutionPage() {
  const { project, can, readOnly, releaseVersionId } = useProjectAccess()
  useDocumentTitle(`Execution · ${project.name}`)
  const state = useProjectState(project.id)
  // The counts and runner status in /state cover the current plan, so the list shows that plan's steps too. A past
  // release version (FR-62) shows its implementation plan's steps, read-only.
  const planType = readOnly ? 'implementation_plan' : state.data ? currentPlanType(state.data) : 'implementation_plan'
  const steps = useExecutionSteps(project.id, planType, null, state.data !== undefined, releaseVersionId)
  const gates = useGates(project.id)
  const writer = can('startExecution')
  const control = useExecutionControl(project.id)
  const [controlError, setControlError] = useState<unknown>(null)

  const header = (
    <h2 id="execution-title" className="mb-4 text-xl">
      Execution
    </h2>
  )

  if (state.isPending || steps.isPending) {
    return (
      <>
        {header}
        <LoadingState label="Loading execution…" />
      </>
    )
  }
  if (state.isError || steps.isError) {
    return (
      <>
        {header}
        <ErrorState
          error={state.error ?? steps.error}
          title="Execution couldn't load"
          onRetry={() => {
            void state.refetch()
            void steps.refetch()
          }}
        />
      </>
    )
  }

  const s = state.data
  const { plan } = steps.data
  const list = steps.data.steps
  // FR-31: the runner's status comes from /state (the 202 of a control call is written into it straight away).
  const status = s.executionStatus
  const current = list.find((x) => x.status === 'in_progress')
  // FR-33: a blocked step links to its own pending escalation gate.
  const escalationGateFor = (step: ExecutionStep) =>
    gates.data?.find((g) => g.type === 'escalation' && g.status === 'pending' && g.executionStepId === step.id) ?? null
  const ctl = controlFor(status, s, plan !== null && list.length > 0)

  const run = (action: ExecutionAction) => {
    setControlError(null)
    control.mutate(action, {
      onSuccess: () => {
        toast.success(controlToasts[action])
      },
      onError: (error) => {
        setControlError(error)
      },
    })
  }

  const reasons = controlError ? errorReasons(controlError) : []

  return (
    <section aria-labelledby="execution-title">
      {header}
      <div className="flex flex-col gap-5">
        {writer && ctl && (
          <div className="flex flex-col gap-2">
            <div>
              <Button
                variant={ctl.action === 'pause' ? 'secondary' : 'primary'}
                disabled={ctl.disabledReason !== null}
                aria-describedby={ctl.disabledReason ? 'execution-control-reason' : undefined}
                pending={control.isPending}
                pendingLabel={actionLabels[ctl.action].pending}
                onClick={() => {
                  run(ctl.action)
                }}
              >
                {actionLabels[ctl.action].label}
              </Button>
            </div>
            {ctl.disabledReason && (
              <p id="execution-control-reason" className="text-sm text-muted-foreground">
                {ctl.disabledReason}
              </p>
            )}
          </div>
        )}
        {!writer && !readOnly && (
          <Alert tone="info">You can follow execution here. Only Contributors and Owners can start, pause or resume it.</Alert>
        )}
        <div role="alert" aria-live="assertive">
          {controlError !== null && (
            <Alert tone="error" title={errorMessage(controlError, 'executionControl')}>
              {reasons.length > 0 && (
                <ul className="mt-1 list-disc pl-5">
                  {reasons.map((r) => (
                    <li key={r}>{r}</li>
                  ))}
                </ul>
              )}
            </Alert>
          )}
        </div>

        {!readOnly && <ProgressSummary status={status} state={s} current={current} />}

        {!readOnly && <PullRequestSection projectId={project.id} state={s} writer={writer} />}

        {plan === null || list.length === 0 ? (
          <EmptyState title="No execution steps yet">
            <p>Steps are created from the implementation plan when it&apos;s approved.</p>
          </EmptyState>
        ) : (
          <section aria-labelledby="execution-steps-title" className="flex flex-col gap-3">
            <h3 id="execution-steps-title" className="text-lg">
              Steps of {artifactTypeLabels[plan.type].toLowerCase()} v{plan.version}
            </h3>
            <ol aria-label="Execution steps" className="flex flex-col gap-3">
              {list.map((step) => (
                <ExecutionStepItem
                  key={step.id}
                  projectId={project.id}
                  step={step}
                  current={step.id === current?.id}
                  escalationGate={step.status === 'blocked' ? escalationGateFor(step) : null}
                />
              ))}
            </ol>
          </section>
        )}
      </div>
    </section>
  )
}
