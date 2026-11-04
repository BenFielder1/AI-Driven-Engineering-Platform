import type { CSSProperties } from 'react'

import { cn } from '../../../lib/utils'
import { lifecyclePhases, lifecycleSummary } from '../content/lifecycle'

const STEP = 52
const TOP = 40

/** The pulse runs once per phase in turn: 7 s round, each node starting 1 s after the previous one. */
function delay(i: number): CSSProperties {
  return { animationDelay: `${String(i)}s`, animationDuration: '7s' }
}

/**
 * FR-214: the lifecycle as an inline SVG: optional Analysis (dashed), then the six phases joined by a rail with a gate
 * (diamond) between each. The nodes pulse in sequence only under `motion-safe` (no animation at all with
 * prefers-reduced-motion). The drawing is `aria-hidden`; the caption is its text equivalent.
 */
export function AnimatedLifecycleDiagram({ className }: { className?: string }) {
  const last = TOP + STEP * (lifecyclePhases.length - 1)
  return (
    <figure className={cn('m-0', className)} data-testid="lifecycle-diagram">
      <svg viewBox={`0 0 260 ${String(last + 24)}`} className="h-auto w-full max-w-72" aria-hidden="true" focusable="false">
        <line x1="16" y1="8" x2="16" y2={TOP} className="stroke-border" strokeWidth="2" strokeDasharray="4 4" />
        <circle cx="16" cy="8" r="6" className="fill-background stroke-muted-foreground" strokeWidth="2" strokeDasharray="3 3" />
        <text x="36" y="13" className="fill-muted-foreground text-sm">
          Analysis (optional)
        </text>
        <line x1="16" y1={TOP} x2="16" y2={last} className="stroke-primary/50" strokeWidth="2" />
        {lifecyclePhases.map((phase, i) => {
          const y = TOP + i * STEP
          return (
            <g key={phase}>
              <circle
                cx="16"
                cy={y}
                r="8"
                className="fill-primary stroke-background motion-safe:animate-pulse"
                strokeWidth="3"
                style={delay(i)}
                data-animated="phase"
              />
              <text x="36" y={y + 5} className="fill-foreground text-sm font-medium">
                {`${String(i + 1)}. ${phase}`}
              </text>
              {i < lifecyclePhases.length - 1 && (
                <rect
                  x="11"
                  y={y + STEP / 2 - 5}
                  width="10"
                  height="10"
                  transform={`rotate(45 16 ${String(y + STEP / 2)})`}
                  className="fill-ring motion-safe:animate-pulse"
                  style={delay(i + 0.5)}
                  data-animated="gate"
                />
              )}
            </g>
          )
        })}
      </svg>
      <figcaption className="mt-3 max-w-72 text-sm text-muted-foreground">{lifecycleSummary} An optional Analysis phase can come first.</figcaption>
    </figure>
  )
}
