import type { ComponentProps } from 'react'

import { cn } from '../../lib/utils'

/** shadcn/ui Skeleton: a pulsing placeholder block (decorative; wrap groups in a labelled status). */
export function Skeleton({ className, ...props }: ComponentProps<'div'>) {
  return <div data-slot="skeleton" aria-hidden className={cn('animate-pulse rounded-md bg-accent', className)} {...props} />
}
