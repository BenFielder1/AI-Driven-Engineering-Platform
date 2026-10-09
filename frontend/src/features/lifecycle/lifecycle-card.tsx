import { Link } from '@tanstack/react-router'
import { useState } from 'react'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { errorMessage, errorReasons } from '../../lib/errors'
import { StatusBadge } from '../projects/project-badges'
import { gateTypeLabels, nextPhase, phaseLabels, phases } from '../projects/labels'
import type { ProjectState } from '../projects/projects-api'
import { useAdvancePhase, type AdvanceResult } from './lifecycle-api'

/**
 * The six lifecycle phases in order (SPEC §4.1), as an ordered list with the current phase marked
 * `aria-current="step"`; earlier phases read as completed.
 */
export function PhaseTimelineDetailed({ current }: { current: ProjectState['phase'] }) {
  const currentIndex = phases.indexOf(current)
  return (
    <ol aria-label="Phase timeline" className="grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-6">
      {phases.map((phase, index) => {
        const step = index < currentIndex ? 'done' : index === currentIndex ? 'current' : 'upcoming'
        return (
          <li
            key={phase}
            aria-current={step === 'current' ? 'step' : undefined}
            className={
              'flex flex-col rounded-md border px-3 py-2 text-sm ' +
              (step === 'current'
                ? 'border-primary bg-primary text-on-primary'
                : step === 'done'
                  ? 'border-border bg-surface text-fg'
                  : 'border-dashed border-border text-muted')
            }
          >
            <span className="text-xs">
              {String(index + 1)}. {step === 'done' ? 'Completed' : step === 'current' ? 'Current' : 'Upcoming'}
            </span>
            <span className="font-semibold">{phaseLabels[phase]}</span>
          </li>
        )
      })}
    </ol>
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
 * Overview → Lifecycle (FR-11, FR-12): phase timeline, status and version, and "Advance to <next phase>" for writers.
 * An advance either moves the phase or creates the gate the approval mode requires; precondition failures list the
 * server's reasons.
 */
export function LifecycleCard({
  projectId,
  state,
  canAdvance,
}: {
  projectId: string
  state: ProjectState
  canAdvance: boolean
}) {
  const advance = useAdvancePhase(projectId)
  const [outcome, setOutcome] = useState<AdvanceResult | null>(null)
  const next = nextPhase(state.phase)
  const reasons = errorReasons(advance.error)

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
    <section aria-labelledby="lifecycle-title" className="rounded-lg border border-primary bg-surface p-4">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
        <h2 id="lifecycle-title" className="text-lg font-semibold">
          Lifecycle
        </h2>
        <dl className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm">
          <div className="flex items-center gap-2">
            <dt className="text-muted">Status</dt>
            <dd>
              <StatusBadge status={state.status} />
            </dd>
          </div>
          <div className="flex items-center gap-2">
            <dt className="text-muted">Version</dt>
            <dd className="font-medium">{state.projectVersion}</dd>
          </div>
        </dl>
      </div>

      <PhaseTimelineDetailed current={state.phase} />

      {state.phase === 'refinement' && (
        <div className="mt-4">
          <Link
            to="/projects/$projectId/refinement"
            params={{ projectId }}
            className={buttonClasses(canAdvance ? 'primary' : 'secondary')}
          >
            {canAdvance ? 'Refine the idea' : 'Follow refinement'}
          </Link>
        </div>
      )}

      {canAdvance && (
        <div className="mt-4">
          {next ? (
            <Button onClick={onAdvance} pending={advance.isPending} pendingLabel="Advancing…">
              Advance to {phaseLabels[next]}
            </Button>
          ) : (
            <p className="text-sm text-muted">Release is the last phase.</p>
          )}
        </div>
      )}
      {state.executionPaused && <p className="mt-2 text-sm text-muted">Execution is paused.</p>}

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
