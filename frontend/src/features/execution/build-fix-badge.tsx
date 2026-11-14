import { Badge } from '../../components/ui/badge'
import type { ExecutionStep, ExecutionStepSummary } from './execution-api'

/**
 * FR-259: the "Build fix · attempt n" badge on a step planned from a failed build check (origin `build_fix`), in the
 * 0.6.0 badge style next to the step's status (like the "Added by Tevrin" badge on plan steps). Nothing for plan steps.
 * `buildCheckAttempt` is the attempt of the check that failed, so fix n (planned from attempt n − 1) shows n: it matches
 * the "Attempt n of max" check that verifies it.
 */
export function BuildFixBadge({ step }: { step: Pick<ExecutionStep | ExecutionStepSummary, 'origin' | 'buildCheckAttempt'> }) {
  if (step.origin !== 'build_fix') {
    return null
  }
  return (
    <span data-testid="build-fix-badge">
      <Badge tone="info">
        {step.buildCheckAttempt === null ? 'Build fix' : `Build fix · attempt ${String(step.buildCheckAttempt + 1)}`}
      </Badge>
    </span>
  )
}
