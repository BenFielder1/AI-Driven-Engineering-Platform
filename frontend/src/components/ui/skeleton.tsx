import type { ComponentProps } from 'react'

import { cn } from '../../lib/utils'

/**
 * shadcn/ui Skeleton: a placeholder block shaped like the content it stands in for (FR-205). It pulses (opacity) only
 * when motion is allowed. Decorative: wrap groups in a labelled status (components/shared/skeletons.tsx).
 */
export function Skeleton({ className, ...props }: ComponentProps<'div'>) {
  return <div data-slot="skeleton" aria-hidden className={cn('rounded-md bg-muted motion-safe:animate-pulse', className)} {...props} />
}
