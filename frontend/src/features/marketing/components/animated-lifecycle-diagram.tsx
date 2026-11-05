import type { CSSProperties } from 'react'

import { cn } from '../../../lib/utils'
import { lifecyclePhases, lifecycleSummary } from '../content/lifecycle'

/** The pulse runs once per phase in turn: 7 s round, each node starting 1 s after the previous one. */
function delay(i: number): CSSProperties {
  return { animationDelay: `${String(i)}s`, animationDuration: '7s' }
}

const label = (i: number, phase: string) => `${String(i + 1)}. ${phase}`

function PhaseNode({ cx, cy, i }: { cx: number; cy: number; i: number }) {
  return (
    <circle
      cx={cx}
      cy={cy}
      r="8"
      className="fill-primary stroke-background motion-safe:animate-pulse"
      strokeWidth="3"
      style={delay(i)}
      data-animated="phase"
    />
  )
}

function GateNode({ cx, cy, i }: { cx: number; cy: number; i: number }) {
  return (
    <rect
      x={cx - 5}
      y={cy - 5}
      width="10"
      height="10"
      transform={`rotate(45 ${String(cx)} ${String(cy)})`}
      className="fill-ring motion-safe:animate-pulse"
      style={delay(i + 0.5)}
      data-animated="gate"
    />
  )
}

function AnalysisNode({ cx, cy }: { cx: number; cy: number }) {
  return <circle cx={cx} cy={cy} r="6" className="fill-background stroke-muted-foreground" strokeWidth="2" strokeDasharray="3 3" />
}

/** Wide screens: one horizontal rail under the page intro, labels under the nodes. */
const H = { analysisX: 70, firstX: 200, step: 140, railY: 20, labelY: 52, width: 960, height: 64 }

function HorizontalLifecycle() {
  const lastX = H.firstX + H.step * (lifecyclePhases.length - 1)
  return (
    <svg
      viewBox={`0 0 ${String(H.width)} ${String(H.height)}`}
      className="hidden h-auto w-full lg:block"
      aria-hidden="true"
      focusable="false"
      data-layout="horizontal"
    >
      <line x1={H.analysisX} y1={H.railY} x2={H.firstX} y2={H.railY} className="stroke-border" strokeWidth="2" strokeDasharray="4 4" />
      <AnalysisNode cx={H.analysisX} cy={H.railY} />
      <text x={H.analysisX} y={H.labelY} textAnchor="middle" className="fill-muted-foreground text-sm">
        Analysis (optional)
      </text>
      <line x1={H.firstX} y1={H.railY} x2={lastX} y2={H.railY} className="stroke-primary/50" strokeWidth="2" />
      {lifecyclePhases.map((phase, i) => {
        const x = H.firstX + i * H.step
        return (
          <g key={phase}>
            {i < lifecyclePhases.length - 1 && <GateNode cx={x + H.step / 2} cy={H.railY} i={i} />}
            <PhaseNode cx={x} cy={H.railY} i={i} />
            <text x={x} y={H.labelY} textAnchor="middle" className="fill-foreground text-sm font-medium">
              {label(i, phase)}
            </text>
          </g>
        )
      })}
    </svg>
  )
}

/** Narrow screens: the same lifecycle as a vertical rail. */
const V = { top: 40, step: 52 }

function VerticalLifecycle() {
  const last = V.top + V.step * (lifecyclePhases.length - 1)
  return (
    <svg
      viewBox={`0 0 260 ${String(last + 24)}`}
      className="h-auto w-full max-w-72 lg:hidden"
      aria-hidden="true"
      focusable="false"
      data-layout="vertical"
    >
      <line x1="16" y1="8" x2="16" y2={V.top} className="stroke-border" strokeWidth="2" strokeDasharray="4 4" />
      <AnalysisNode cx={16} cy={8} />
      <text x="36" y="13" className="fill-muted-foreground text-sm">
        Analysis (optional)
      </text>
      <line x1="16" y1={V.top} x2="16" y2={last} className="stroke-primary/50" strokeWidth="2" />
      {lifecyclePhases.map((phase, i) => {
        const y = V.top + i * V.step
        return (
          <g key={phase}>
            <PhaseNode cx={16} cy={y} i={i} />
            <text x="36" y={y + 5} className="fill-foreground text-sm font-medium">
              {label(i, phase)}
            </text>
            {i < lifecyclePhases.length - 1 && <GateNode cx={16} cy={y + V.step / 2} i={i} />}
          </g>
        )
      })}
    </svg>
  )
}

/**
 * FR-214: the lifecycle as an inline SVG: optional Analysis (dashed), then the six phases joined by a rail with a gate
 * (diamond) between each. It sits under the page intro: a horizontal rail on wide screens and a vertical one on narrow
 * screens (only one is displayed). The nodes pulse in sequence only under `motion-safe` (no animation at all with
 * prefers-reduced-motion). The drawings are `aria-hidden`; the caption is their text equivalent.
 */
export function AnimatedLifecycleDiagram({ className }: { className?: string }) {
  return (
    <figure className={cn('m-0', className)} data-testid="lifecycle-diagram">
      <HorizontalLifecycle />
      <VerticalLifecycle />
      <figcaption className="mt-3 max-w-2xl text-sm text-muted-foreground lg:mt-4">
        {lifecycleSummary} An optional Analysis phase can come first.
      </figcaption>
    </figure>
  )
}
