import { Link } from '@tanstack/react-router'
import { ArrowRight } from 'lucide-react'
import { useId } from 'react'
import { toast } from 'sonner'

import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { errorMessage, errorReasons } from '../../lib/errors'
import { gateTypeLabels, phaseLabels } from '../projects/labels'
import { useAdvancePhase } from './lifecycle-api'
import type { NextAction } from './next-action'

/**
 * FR-54 primary action button: the next action the user can take. Advances run in place (with a toast for the
 * result); everything else links to the page that holds the control. When the step belongs to someone else the
 * button is disabled and the reason is shown next to it (SPEC §7.2 UI rules).
 */
export function NextActionButton({ projectId, action, className }: { projectId: string; action: NextAction; className?: string }) {
  const reasonId = useId()
  const advance = useAdvancePhase(projectId)

  if (action.disabledReason) {
    return (
      <div className={className} data-testid="next-action">
        <div className="flex flex-col items-start gap-1 sm:items-end">
          <Button disabled aria-describedby={reasonId}>
            {action.label}
          </Button>
          <p id={reasonId} className="max-w-64 text-xs text-muted-foreground sm:text-right">
            {action.disabledReason}
          </p>
        </div>
      </div>
    )
  }

  if (action.key === 'advance') {
    return (
      <div className={className} data-testid="next-action">
        <Button
          pending={advance.isPending}
          pendingLabel="Advancing…"
          onClick={() => {
            advance.mutate(
              {},
              {
                onSuccess: (result) => {
                  if (result.gate) {
                    toast.info('Approval required', {
                      description: `${gateTypeLabels[result.gate.type]} must be approved by a project Owner first.`,
                    })
                  } else {
                    toast.success(`The project moved to ${phaseLabels[result.state.phase]}.`)
                  }
                },
                onError: (error) => {
                  toast.error(errorMessage(error), { description: errorReasons(error).join(' ') || undefined })
                },
              },
            )
          }}
        >
          {action.label}
          <ArrowRight className="size-4" aria-hidden />
        </Button>
      </div>
    )
  }

  return (
    <div className={className} data-testid="next-action">
      {/* exact: a CTA isn't navigation, so it must never claim aria-current on sub-pages. */}
      <Link
        to={action.to}
        params={{ projectId, ...action.params }}
        hash={action.hash}
        activeOptions={{ exact: true, includeHash: true }}
        className={buttonClasses('primary')}
      >
        {action.label}
        <ArrowRight className="size-4" aria-hidden />
      </Link>
    </div>
  )
}
