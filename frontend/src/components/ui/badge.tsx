import type { ReactNode } from 'react'

import { StatusBadge, type StatusTone } from '../shared/status-badge'

export type BadgeTone = 'neutral' | 'info' | 'success' | 'warning' | 'danger'

const toneMap: Record<BadgeTone, StatusTone> = {
  neutral: 'neutral',
  info: 'info',
  success: 'success',
  warning: 'warning',
  danger: 'destructive',
}

/**
 * A small status label, drawn as the v2 StatusBadge (dot plus tinted pill, SPEC §7.1). Colour is decorative only: the
 * text always carries the meaning.
 */
export function Badge({ tone = 'neutral', children, className = '' }: { tone?: BadgeTone; children: ReactNode; className?: string }) {
  return (
    <StatusBadge tone={toneMap[tone]} className={className}>
      {children}
    </StatusBadge>
  )
}
