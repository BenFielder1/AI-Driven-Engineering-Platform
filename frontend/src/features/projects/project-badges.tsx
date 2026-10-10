import { Badge, type BadgeTone } from '../../components/ui/badge'
import { phaseLabels, phases, statusLabels } from './labels'
import type { Phase, ProjectStatus } from './projects-api'

export function PhaseBadge({ phase }: { phase: Phase }) {
  return <Badge tone="info">{phaseLabels[phase]}</Badge>
}

const statusTones: Record<ProjectStatus, BadgeTone> = {
  active: 'neutral',
  building: 'info',
  reviewing: 'info',
  releasing: 'warning',
  released: 'success',
  rolled_back: 'danger',
}

export function StatusBadge({ status }: { status: ProjectStatus }) {
  return <Badge tone={statusTones[status]}>{statusLabels[status]}</Badge>
}

/** The six lifecycle phases in order, with the current one marked (aria-current="step"). */
export function PhaseTimeline({ current }: { current: Phase }) {
  const currentIndex = phases.indexOf(current)
  return (
    <ol aria-label="Lifecycle phases" className="flex flex-wrap gap-1 text-xs sm:text-sm">
      {phases.map((phase, index) => {
        const state = index < currentIndex ? 'done' : index === currentIndex ? 'current' : 'upcoming'
        return (
          <li
            key={phase}
            aria-current={state === 'current' ? 'step' : undefined}
            className={
              'rounded-md border px-2 py-1 ' +
              (state === 'current'
                ? 'border-primary bg-primary font-semibold text-on-primary'
                : state === 'done'
                  ? 'border-border bg-surface text-fg'
                  : 'border-border text-muted')
            }
          >
            <span className="sr-only">
              {state === 'done' ? 'Completed: ' : state === 'current' ? 'Current: ' : 'Upcoming: '}
            </span>
            {phaseLabels[phase]}
          </li>
        )
      })}
    </ol>
  )
}
