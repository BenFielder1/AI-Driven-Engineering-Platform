import { AlertTriangle, CircleDashed, Inbox, Lock, PlugZap, type LucideIcon } from 'lucide-react'
import type { ReactNode } from 'react'

import { cn } from '../../lib/utils'
import { panelClass } from './panel-class'

export type EmptyStateVariant = 'no_data' | 'not_started' | 'not_connected' | 'forbidden' | 'error'

const icons: Record<EmptyStateVariant, LucideIcon> = {
  no_data: Inbox,
  not_started: CircleDashed,
  not_connected: PlugZap,
  forbidden: Lock,
  error: AlertTriangle,
}

const iconTone: Record<EmptyStateVariant, string> = {
  no_data: 'bg-muted text-muted-foreground',
  not_started: 'bg-muted text-muted-foreground',
  not_connected: 'bg-primary/10 text-primary',
  forbidden: 'bg-muted text-muted-foreground',
  error: 'bg-destructive/10 text-destructive',
}

/**
 * Typed empty state (SPEC §7.1): no data, not started, not connected, forbidden, error. `action` is a ready-made
 * control so the caller picks a role-appropriate call to action. `headingLevel` keeps the outline right.
 */
export function EmptyState({
  variant,
  title,
  description,
  action,
  bare = false,
  headingLevel = 2,
  className,
}: {
  variant: EmptyStateVariant
  title: string
  description?: ReactNode
  action?: ReactNode
  /** Without the panel surface (e.g. inside another card). */
  bare?: boolean
  headingLevel?: 1 | 2 | 3
  className?: string
}) {
  const Icon = icons[variant]
  const Heading = headingLevel === 1 ? 'h1' : headingLevel === 3 ? 'h3' : 'h2'
  return (
    <div
      data-testid="empty-state"
      data-variant={variant}
      role={variant === 'error' ? 'alert' : undefined}
      className={cn(!bare && panelClass, 'flex flex-col items-center justify-center gap-3 px-6 py-10 text-center', className)}
    >
      <span className={cn('flex size-12 items-center justify-center rounded-full', iconTone[variant])}>
        <Icon className="size-6" aria-hidden />
      </span>
      <div className="max-w-md space-y-1">
        <Heading className="text-lg">{title}</Heading>
        {description && <div className="text-sm text-muted-foreground">{description}</div>}
      </div>
      {action && <div className="mt-1 flex flex-wrap justify-center gap-2">{action}</div>}
    </div>
  )
}
