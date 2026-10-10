import { Link } from '@tanstack/react-router'

import { Alert } from '../../components/ui/alert'
import { messageFor } from '../../lib/errors'
import { formatNumber } from '../projects/labels'

/** Share of the budget at which the meter warns that it's almost used up. */
const NEARLY_USED = 0.9

/**
 * FR-26: tokens used against the project's optional budget, as a meter whose state is always in the text (never colour
 * alone). A `null` budget means unlimited. When the budget is used up, new runs are refused with BUDGET_EXCEEDED;
 * Owners get a link to raise it in Settings.
 */
export function TokenBudget({
  projectId,
  tokensUsed,
  tokenBudget,
  canRaise,
}: {
  projectId: string
  tokensUsed: number
  tokenBudget: number | null
  canRaise: boolean
}) {
  if (tokenBudget === null) {
    return (
      <div className="text-sm">
        <p className="font-medium">No budget</p>
        <p className="mt-1 text-muted">{formatNumber(tokensUsed)} tokens used. Agent runs aren&apos;t capped on this project.</p>
      </div>
    )
  }

  const ratio = tokenBudget > 0 ? tokensUsed / tokenBudget : 1
  const percent = Math.min(100, Math.floor(ratio * 100))
  const exhausted = tokensUsed >= tokenBudget
  const nearly = !exhausted && ratio >= NEARLY_USED
  const text = `${formatNumber(tokensUsed)} of ${formatNumber(tokenBudget)} tokens used (${String(percent)}%)`
  const state = exhausted ? 'Used up' : nearly ? 'Almost used up' : null

  return (
    <div className="text-sm">
      <p className="font-medium">
        {text}
        {state && <span className="ml-1 font-semibold">· {state}</span>}
      </p>
      <div
        role="meter"
        aria-label="Token budget used"
        aria-valuemin={0}
        aria-valuemax={tokenBudget}
        aria-valuenow={Math.min(tokensUsed, tokenBudget)}
        aria-valuetext={state ? `${text}, ${state.toLowerCase()}` : text}
        className="mt-2 h-3 w-full overflow-hidden rounded-full border border-border bg-surface"
      >
        <div
          className={`h-full ${exhausted ? 'bg-danger' : nearly ? 'bg-warning' : 'bg-primary'}`}
          style={{ width: `${String(percent)}%` }}
        />
      </div>
      <p className="mt-1 text-muted">
        {exhausted
          ? 'No tokens left.'
          : `${formatNumber(tokenBudget - tokensUsed)} tokens left.`}
      </p>
      {exhausted && (
        <Alert tone="warning" className="mt-3">
          <p>{messageFor('BUDGET_EXCEEDED')}</p>
          {canRaise && (
            <Link
              to="/projects/$projectId/settings"
              params={{ projectId }}
              className="mt-1 inline-block font-medium text-link underline"
            >
              Raise the token budget
            </Link>
          )}
        </Alert>
      )}
    </div>
  )
}
