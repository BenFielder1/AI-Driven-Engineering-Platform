import type { ReactNode } from 'react'

export type BadgeTone = 'neutral' | 'info' | 'success' | 'warning' | 'danger'

const tones: Record<BadgeTone, string> = {
  neutral: 'border-border bg-surface text-fg',
  info: 'border-primary bg-surface text-fg',
  success: 'border-success bg-success-bg text-fg',
  warning: 'border-warning bg-warning-bg text-fg',
  danger: 'border-danger bg-danger-bg text-fg',
}

/** A small text label. Colour is decorative only: the text always carries the meaning. */
export function Badge({ tone = 'neutral', children, className = '' }: { tone?: BadgeTone; children: ReactNode; className?: string }) {
  return (
    <span className={`inline-flex items-center rounded border px-1.5 py-0.5 text-xs font-medium ${tones[tone]} ${className}`}>
      {children}
    </span>
  )
}
