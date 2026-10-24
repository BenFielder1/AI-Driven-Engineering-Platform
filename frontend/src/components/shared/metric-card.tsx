import { ArrowDownRight, ArrowUpRight, Minus } from 'lucide-react'
import dynamic from 'next/dynamic'
import type { ReactNode } from 'react'

import { cn } from '../../lib/utils'
import { panelClass } from './panel-class'

/** SPEC §8 bundle budget: recharts loads after first paint, outside the initial /projects chunks. */
const Sparkline = dynamic(() => import('./sparkline').then((m) => m.Sparkline), { ssr: false })
const SPARKLINE_HEIGHT = 40

export type DeltaTone = 'positive' | 'negative' | 'neutral'

const toneClass: Record<DeltaTone, string> = {
  positive: 'text-success',
  negative: 'text-destructive',
  neutral: 'text-muted-foreground',
}

const sparkColor: Record<DeltaTone, string> = {
  positive: 'var(--success)',
  negative: 'var(--destructive)',
  neutral: 'var(--chart-1)',
}

export interface MetricDelta {
  /** Pre-formatted change, e.g. "+3" or "-0.04". */
  text: string
  direction: 'up' | 'down' | 'flat'
  /** Whether the change is good, bad or neither (up can be bad, e.g. tokens used). */
  tone: DeltaTone
  /** After the change, e.g. "since the last cycle". */
  label?: string
}

/**
 * KPI tile (SPEC §7.1): label, mono value, optional delta (arrow + signed text, so colour is never the only signal)
 * and an optional decorative sparkline.
 */
export function MetricCard({
  label,
  value,
  delta,
  sparkline,
  footer,
  className,
}: {
  label: string
  /** Pre-formatted value. */
  value: string
  delta?: MetricDelta
  sparkline?: readonly number[]
  footer?: ReactNode
  className?: string
}) {
  const tone = delta?.tone ?? 'neutral'
  const Icon = delta?.direction === 'up' ? ArrowUpRight : delta?.direction === 'down' ? ArrowDownRight : Minus
  return (
    <div data-testid="metric-card" className={cn(panelClass, 'flex min-w-0 flex-col p-4', className)}>
      <p className="truncate text-sm text-muted-foreground">{label}</p>
      <p className="mt-1 truncate font-mono text-2xl font-medium tabular-nums sm:text-3xl" title={value}>
        {value}
      </p>
      {delta && (
        <p className="mt-1 flex flex-wrap items-center gap-x-1.5 text-xs">
          <span data-tone={tone} className={cn('inline-flex items-center gap-0.5 font-medium', toneClass[tone])}>
            <Icon className="size-3.5" aria-hidden />
            <span className="sr-only">{delta.direction === 'up' ? 'Up' : delta.direction === 'down' ? 'Down' : 'No change'} </span>
            <span className="tabular-nums">{delta.text}</span>
          </span>
          {delta.label && <span className="text-muted-foreground">{delta.label}</span>}
        </p>
      )}
      {sparkline && sparkline.length > 1 && (
        // Fixed-size slot, so the chart arriving after the lazy import causes no layout shift.
        <div aria-hidden className="mt-3 w-full" style={{ height: SPARKLINE_HEIGHT }}>
          <Sparkline data={sparkline} color={sparkColor[tone]} height={SPARKLINE_HEIGHT} />
        </div>
      )}
      {footer && <div className="mt-2 text-xs text-muted-foreground">{footer}</div>}
    </div>
  )
}
