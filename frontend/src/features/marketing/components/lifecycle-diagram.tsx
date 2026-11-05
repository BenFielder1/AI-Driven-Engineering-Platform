import { cn } from '../../../lib/utils'
import { lifecyclePhases, lifecycleSummary } from '../content/lifecycle'

/**
 * A static lifecycle diagram (FR-223; reusable by FR-214): the six phases top to bottom, joined by a rail with a gate
 * (diamond) between each. Inline SVG, `aria-hidden`, with the text equivalent in an sr-only paragraph. No animation.
 */
export function LifecycleDiagram({ className }: { className?: string }) {
  const step = 44
  const height = step * (lifecyclePhases.length - 1) + 24
  return (
    <figure className={cn('m-0', className)}>
      <svg viewBox={`0 0 240 ${String(height)}`} className="h-auto w-full max-w-60" aria-hidden="true" focusable="false">
        <line x1="12" y1="12" x2="12" y2={height - 12} className="stroke-border" strokeWidth="2" />
        {lifecyclePhases.map((phase, i) => {
          const y = 12 + i * step
          return (
            <g key={phase}>
              <circle cx="12" cy={y} r="7" className="fill-background stroke-primary" strokeWidth="2" />
              <text x="30" y={y + 5} className="fill-foreground text-sm">
                {phase}
              </text>
              {i < lifecyclePhases.length - 1 && (
                <rect
                  x="8"
                  y={y + step / 2 - 4}
                  width="8"
                  height="8"
                  transform={`rotate(45 12 ${String(y + step / 2)})`}
                  className="fill-ring"
                />
              )}
            </g>
          )
        })}
      </svg>
      <figcaption className="sr-only">{lifecycleSummary}</figcaption>
    </figure>
  )
}
