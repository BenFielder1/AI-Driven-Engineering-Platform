import type { ReactNode } from 'react'

import { cn } from '../../lib/utils'

/** Badge tones (FR-205, §4.31): neutral slate, info indigo, success emerald, warning amber, destructive rose. */
export type StatusTone = 'success' | 'warning' | 'destructive' | 'info' | 'neutral'

const toneSurface: Record<StatusTone, string> = {
  success: 'border-success/40 bg-success/15',
  warning: 'border-warning/50 bg-warning/15',
  destructive: 'border-destructive/40 bg-destructive/15',
  info: 'border-primary/40 bg-primary/15',
  neutral: 'border-border bg-muted',
}

const toneDot: Record<StatusTone, string> = {
  success: 'bg-success',
  warning: 'bg-warning',
  destructive: 'bg-destructive',
  info: 'bg-primary',
  neutral: 'bg-muted-foreground',
}

/**
 * A dot plus a label on a tinted pill (FR-205): colour is never the only signal, and the label stays `foreground` for
 * AA in both themes (tinted pairs in tokens.test.ts). `pulse` animates the dot for live states (Running); the pulse is
 * opacity only and stops under prefers-reduced-motion.
 */
export function StatusBadge({
  tone,
  children,
  pulse = false,
  className,
}: {
  tone: StatusTone
  children: ReactNode
  pulse?: boolean
  className?: string
}) {
  return (
    <span
      data-tone={tone}
      data-slot="status-badge"
      className={cn(
        'inline-flex w-fit shrink-0 items-center gap-1.5 rounded-full border px-2 py-0.5 text-xs font-medium whitespace-nowrap text-foreground',
        toneSurface[tone],
        className,
      )}
    >
      <span aria-hidden data-pulse={pulse || undefined} className={cn('size-1.5 rounded-full', toneDot[tone], pulse && 'motion-safe:animate-pulse')} />
      {children}
    </span>
  )
}

/** The lifecycle statuses with a fixed look (§4.31): Locked slate, In review amber, Approved emerald, Failed rose, Running indigo. */
export type LifecycleStatus = 'locked' | 'in_review' | 'approved' | 'failed' | 'running'

export const LIFECYCLE_STATUS_STYLES: Record<LifecycleStatus, { tone: StatusTone; label: string; pulse: boolean }> = {
  locked: { tone: 'neutral', label: 'Locked', pulse: false },
  in_review: { tone: 'warning', label: 'In review', pulse: false },
  approved: { tone: 'success', label: 'Approved', pulse: false },
  failed: { tone: 'destructive', label: 'Failed', pulse: false },
  running: { tone: 'info', label: 'Running', pulse: true },
}

/** A lifecycle status badge with its standard tone and label; `children` overrides the label (e.g. "Running · 2/5"). */
export function LifecycleStatusBadge({ status, children, className }: { status: LifecycleStatus; children?: ReactNode; className?: string }) {
  const style = LIFECYCLE_STATUS_STYLES[status]
  return (
    <StatusBadge tone={style.tone} pulse={style.pulse} className={className}>
      {children ?? style.label}
    </StatusBadge>
  )
}
