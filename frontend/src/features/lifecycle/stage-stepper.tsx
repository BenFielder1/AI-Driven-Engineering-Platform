import { Link } from '@tanstack/react-router'
import { Check, ChevronLeft, ChevronRight } from 'lucide-react'
import { useRef, type KeyboardEvent } from 'react'

import { buttonClasses } from '../../components/ui/button-classes'
import { cn } from '../../lib/utils'
import { phaseLabels } from '../projects/labels'
import { stageRoute, type Phase } from '../shell/nav-config'
import type { StageStatus } from '../shell/stage-states'
import { adjacentStages } from './stage-nav'

const stateText = { done: 'done', current: 'current stage', upcoming: 'upcoming' } as const

/**
 * FR-54 horizontal stage stepper: the six stages with the sidebar's states (done ✓, current with a pulsing dot,
 * upcoming muted, needs-action count). Every stage is a link (later stages stay navigable); Left/Right, Home and End
 * move between them. The project's current stage is `aria-current="step"`, the page being viewed `aria-current="page"`.
 */
export function StageStepper({
  projectId,
  stages,
  viewing,
  className,
}: {
  projectId: string
  stages: readonly StageStatus[]
  /** The stage page on screen, if any. */
  viewing?: Phase
  className?: string
}) {
  const links = useRef<(HTMLAnchorElement | null)[]>([])

  function onKeyDown(event: KeyboardEvent<HTMLOListElement>) {
    const index = links.current.findIndex((el) => el === document.activeElement)
    if (index < 0) {
      return
    }
    const last = stages.length - 1
    const target =
      event.key === 'ArrowRight'
        ? Math.min(last, index + 1)
        : event.key === 'ArrowLeft'
          ? Math.max(0, index - 1)
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? last
              : null
    if (target !== null) {
      event.preventDefault()
      links.current[target]?.focus()
    }
  }

  return (
    <nav aria-label="Lifecycle stages" className={cn('min-w-0 overflow-x-auto', className)}>
      <ol onKeyDown={onKeyDown} className="flex min-w-max items-center gap-1 py-1" data-testid="stage-stepper">
        {stages.map((s, i) => {
          const isViewing = s.stage === viewing
          const ariaCurrent = isViewing ? 'page' : s.state === 'current' ? 'step' : undefined
          return (
            <li key={s.stage} className="flex items-center gap-1" data-stage-state={s.state}>
              {i > 0 && (
                <span
                  aria-hidden
                  className={cn('h-px w-3 sm:w-6', s.state === 'upcoming' ? 'bg-border' : 'bg-primary/60')}
                />
              )}
              <Link
                ref={(el) => {
                  links.current[i] = el
                }}
                to={stageRoute[s.stage]}
                params={{ projectId }}
                aria-current={ariaCurrent}
                className={cn(
                  'group relative flex min-h-11 items-center gap-2 rounded-full border px-3 text-sm whitespace-nowrap transition-colors',
                  'outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50',
                  s.state === 'current' && 'border-primary/60 bg-primary/10 font-medium text-foreground',
                  s.state === 'done' && 'border-border/60 bg-card/60 text-foreground hover:bg-accent',
                  s.state === 'upcoming' && 'border-dashed border-border/60 text-muted-foreground hover:text-foreground',
                  isViewing && 'ring-2 ring-primary/50',
                )}
              >
                <span
                  aria-hidden
                  className={cn(
                    'flex size-5 shrink-0 items-center justify-center rounded-full font-mono text-[11px]',
                    s.state === 'done' && 'bg-success/20 text-success',
                    s.state === 'current' && 'bg-primary text-primary-foreground',
                    s.state === 'upcoming' && 'bg-muted text-muted-foreground',
                  )}
                >
                  {s.state === 'done' ? (
                    <Check className="size-3" />
                  ) : s.state === 'current' ? (
                    <span className="size-2 animate-pulse rounded-full bg-primary-foreground" />
                  ) : (
                    i + 1
                  )}
                </span>
                {phaseLabels[s.stage]}
                <span className="sr-only">
                  {' '}
                  ({stateText[s.state]}
                  {s.needsAction > 0 ? `, ${String(s.needsAction)} awaiting action` : ''})
                </span>
                {s.needsAction > 0 && (
                  <span
                    aria-hidden
                    data-testid="stepper-needs-action"
                    className="flex h-5 min-w-5 items-center justify-center rounded-full bg-warning px-1.5 font-mono text-[11px] leading-none font-semibold text-warning-foreground"
                  >
                    {s.needsAction}
                  </span>
                )}
              </Link>
            </li>
          )
        })}
      </ol>
    </nav>
  )
}

/**
 * FR-54 "Previous stage" / "Next stage": move between stage pages, relative to the stage on screen (or the project's
 * current stage on other project pages). Disabled at either end.
 */
export function StageNavButtons({ projectId, from }: { projectId: string; from: Phase }) {
  const { previous, next } = adjacentStages(from)
  const base = buttonClasses('secondary', 'min-h-11 px-3')
  return (
    <div className="flex shrink-0 items-center gap-2">
      {previous ? (
        <Link to={stageRoute[previous]} params={{ projectId }} className={base}>
          <ChevronLeft className="size-4" aria-hidden />
          <span className="max-sm:sr-only">Previous stage</span>
          <span className="sr-only">: {phaseLabels[previous]}</span>
        </Link>
      ) : (
        <button type="button" disabled className={base}>
          <ChevronLeft className="size-4" aria-hidden />
          <span className="max-sm:sr-only">Previous stage</span>
        </button>
      )}
      {next ? (
        <Link to={stageRoute[next]} params={{ projectId }} className={base}>
          <span className="max-sm:sr-only">Next stage</span>
          <span className="sr-only">: {phaseLabels[next]}</span>
          <ChevronRight className="size-4" aria-hidden />
        </Link>
      ) : (
        <button type="button" disabled className={base}>
          <span className="max-sm:sr-only">Next stage</span>
          <ChevronRight className="size-4" aria-hidden />
        </button>
      )}
    </div>
  )
}
