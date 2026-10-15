import { Link } from '@tanstack/react-router'
import { useState } from 'react'

import { panelClass } from '../../components/shared/panel-class'
import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { errorMessage, errorReasons } from '../../lib/errors'
import { cn } from '../../lib/utils'
import { BranchPair } from '../git/pull-request-section'
import type { PullRequestSummary } from '../git/pull-request-api'
import { PhaseBadge, StatusBadge } from '../projects/project-badges'
import { gateTypeLabels, nextPhase, phaseLabels } from '../projects/labels'
import type { ProjectState } from '../projects/projects-api'
import { useAdvancePhase, type AdvanceResult } from './lifecycle-api'
import type { NextAction } from './next-action'
import { NextActionButton } from './next-action-button'

/**
 * FR-36: the recorded pull request. GitHub links out to the PR; a Local project's "PR" is the recorded branch
 * comparison, shown on the Execution page.
 */
function PullRequestSummaryLine({ projectId, pr }: { projectId: string; pr: PullRequestSummary }) {
  return (
    <section aria-labelledby="lifecycle-pull-request" className="mt-4 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
      <h3 id="lifecycle-pull-request" className="font-semibold">
        Pull request
      </h3>
      {pr.number !== null && pr.url ? (
        <a href={pr.url} target="_blank" rel="noopener noreferrer" className="text-primary underline">
          #{pr.number}
          <span className="sr-only"> (opens on GitHub)</span>
        </a>
      ) : (
        <span>Branch comparison recorded</span>
      )}
      <BranchPair pr={pr} />
      {pr.url === null && (
        <Link
          to="/projects/$projectId/execution"
          params={{ projectId }}
          hash="pull-request"
          className="text-primary underline"
        >
          View the comparison
        </Link>
      )}
    </section>
  )
}

function AdvanceOutcome({ result }: { result: AdvanceResult }) {
  if (result.gate) {
    return (
      <Alert tone="warning" title="Approval required" className="mt-3">
        <p>
          {gateTypeLabels[result.gate.type]} must be approved by a project Owner before the project moves on. It&apos;s
          listed under Pending gates, and the project stays in {phaseLabels[result.state.phase]}.
        </p>
      </Alert>
    )
  }
  return (
    <Alert tone="success" className="mt-3">
      The project moved to {phaseLabels[result.state.phase]} (version {result.state.projectVersion}).
    </Alert>
  )
}

/**
 * Overview → next-action card (FR-54, with v1 FR-11/FR-12): what the user can do next (from the next-action helper),
 * status and version, and "Advance to <next phase>" for writers. An advance either moves the phase or creates the
 * gate the approval mode requires; precondition failures list the server's reasons. Results are also toasted.
 */
export function LifecycleCard({
  projectId,
  state,
  canAdvance,
  action,
}: {
  projectId: string
  state: ProjectState
  canAdvance: boolean
  action: NextAction | null
}) {
  const advance = useAdvancePhase(projectId)
  const [outcome, setOutcome] = useState<AdvanceResult | null>(null)
  const next = nextPhase(state.phase)
  const reasons = errorReasons(advance.error)
  // The advance button below already is the action when the next step is an advance.
  const linkAction = action && action.key !== 'advance' ? action : null

  function onAdvance() {
    setOutcome(null)
    advance.mutate(
      {},
      {
        onSuccess: (result) => {
          setOutcome(result)
        },
      },
    )
  }

  return (
    <section aria-labelledby="lifecycle-title" className={cn(panelClass, 'relative overflow-hidden border-primary/40 p-5')}>
      <div aria-hidden className="pointer-events-none absolute -top-16 -right-16 size-48 rounded-full bg-primary/15 blur-3xl" />
      <div className="relative flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div className="min-w-0 space-y-1">
          <h2 id="lifecycle-title" className="text-sm font-medium tracking-wider text-muted-foreground uppercase">
            Next action
          </h2>
          {action ? (
            <>
              <p className="font-heading text-xl">{action.label}</p>
              <p className="text-sm text-muted-foreground">{action.description}</p>
            </>
          ) : (
            <p className="text-sm text-muted-foreground">
              {state.status === 'released'
                ? 'This version is released.'
                : canAdvance
                  ? 'Nothing is waiting on you right now: agents are working, or the next step needs an artifact first.'
                  : 'Nothing for you to do here: you can follow the project as it moves.'}
            </p>
          )}
        </div>
        <dl className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm">
          <div className="flex items-center gap-2">
            <dt className="text-muted-foreground">Phase</dt>
            <dd>
              <PhaseBadge phase={state.phase} />
            </dd>
          </div>
          <div className="flex items-center gap-2">
            <dt className="text-muted-foreground">Status</dt>
            <dd>
              <StatusBadge status={state.status} />
            </dd>
          </div>
          <div className="flex items-center gap-2">
            <dt className="text-muted-foreground">Version</dt>
            <dd className="font-mono font-medium tabular-nums">{state.projectVersion}</dd>
          </div>
        </dl>
      </div>

      <div className="relative mt-4 flex flex-wrap items-start gap-3">
        {linkAction && <NextActionButton projectId={projectId} action={linkAction} />}
        {state.phase === 'refinement' && linkAction?.key !== 'refine' && (
          <Link
            to="/projects/$projectId/refinement"
            params={{ projectId }}
            className={buttonClasses(canAdvance && !linkAction ? 'primary' : 'secondary')}
          >
            {canAdvance ? 'Refine the idea' : 'Follow refinement'}
          </Link>
        )}
        {canAdvance &&
          (next ? (
            <Button
              variant={linkAction ? 'secondary' : 'primary'}
              onClick={onAdvance}
              pending={advance.isPending}
              pendingLabel="Advancing…"
            >
              Advance to {phaseLabels[next]}
            </Button>
          ) : (
            <p className="self-center text-sm text-muted-foreground">Release is the last phase.</p>
          ))}
      </div>

      {state.pullRequest && <PullRequestSummaryLine projectId={projectId} pr={state.pullRequest} />}
      {state.executionPaused && <p className="mt-2 text-sm text-muted-foreground">Execution is paused.</p>}

      <div role="status" aria-live="polite">
        {outcome && !advance.isPending && <AdvanceOutcome result={outcome} />}
      </div>
      <div role="alert" aria-live="assertive">
        {advance.isError && (
          <Alert tone="error" title={errorMessage(advance.error)} className="mt-3">
            {reasons.length > 0 && (
              <ul className="mt-1 list-disc pl-5">
                {reasons.map((reason) => (
                  <li key={reason}>{reason}</li>
                ))}
              </ul>
            )}
          </Alert>
        )}
      </div>
    </section>
  )
}
