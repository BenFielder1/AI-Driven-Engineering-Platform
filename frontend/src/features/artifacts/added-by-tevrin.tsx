import { Badge } from '../../components/ui/badge'
import type { PlanPreviewStep } from './plan-block'

/**
 * FR-244, D-0.6.1-10: the "Added by Tevrin" badge on a plan step Tevrin inserted (§4.41), with the reason as visible
 * secondary text (not hover-only). Badge and reason are one unit, read as "Added by Tevrin: <reason>" (a visually
 * hidden ": " joins them). Renders nothing for agent-written steps.
 */
export function AddedByTevrin({ addedBy, addedReason }: Partial<Pick<PlanPreviewStep, 'addedBy' | 'addedReason'>>) {
  if (addedBy !== 'tevrin') {
    return null
  }
  return (
    <span data-testid="plan-step-added-by-tevrin" className="mt-1 flex flex-col items-start gap-0.5">
      <Badge tone="info">Added by Tevrin</Badge>
      {addedReason && (
        <span className="text-xs text-muted-foreground">
          <span className="sr-only">: </span>
          {addedReason}
        </span>
      )}
    </span>
  )
}
