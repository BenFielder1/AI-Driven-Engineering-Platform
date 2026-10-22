import type { ReactNode } from 'react'

import { cn } from '../../lib/utils'

export type StatusTone = 'success' | 'warning' | 'destructive' | 'info' | 'neutral'

const toneSurface: Record<StatusTone, string> = {
  success: 'border-success/40 bg-success/15',
  warning: 'border-warning/40 bg-warning/15',
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
 * A dot plus a tinted pill (SPEC §7.1). The text stays `foreground` for AA in both themes, so colour is never the only
 * signal. `pulse` animates the dot for live states (running, in development).
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
      className={cn(
        'inline-flex w-fit shrink-0 items-center gap-1.5 rounded-full border px-2 py-0.5 text-xs font-medium whitespace-nowrap text-foreground',
        toneSurface[tone],
        className,
      )}
    >
      <span aria-hidden className={cn('size-1.5 rounded-full', toneDot[tone], pulse && 'animate-pulse')} />
      {children}
    </span>
  )
}
