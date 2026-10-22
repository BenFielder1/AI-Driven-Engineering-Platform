import type { Schemas } from '../../api/client'
import { phaseLabels } from '../../features/projects/labels'
import { StatusBadge, type StatusTone } from './status-badge'

const phaseTone: Record<Schemas['Phase'], StatusTone> = {
  refinement: 'neutral',
  specification: 'info',
  planning: 'info',
  execution: 'warning',
  review: 'warning',
  release: 'success',
}

/** The lifecycle phase as a pill (SPEC §7.1). */
export function PhaseBadge({ phase, className }: { phase: Schemas['Phase']; className?: string }) {
  return (
    <StatusBadge tone={phaseTone[phase]} className={className}>
      {phaseLabels[phase]}
    </StatusBadge>
  )
}
