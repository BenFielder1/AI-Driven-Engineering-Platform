import { cn } from '../../lib/utils'
import { Skeleton } from '../ui/skeleton'
import { panelClass } from './panel-class'

/** Loading placeholders per layout (SPEC §7.1). Each group is one labelled status for assistive tech. */

export function SkeletonCards({ count = 4, className }: { count?: number; className?: string }) {
  return (
    <div role="status" aria-busy className={cn('grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4', className)}>
      <span className="sr-only">Loading metrics…</span>
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className={cn(panelClass, 'space-y-3 p-4')}>
          <Skeleton className="h-4 w-24" />
          <Skeleton className="h-8 w-32" />
          <Skeleton className="h-3 w-20" />
        </div>
      ))}
    </div>
  )
}

export function SkeletonChart({ height = 260, className }: { height?: number; className?: string }) {
  return (
    <div role="status" aria-busy className={cn(panelClass, 'space-y-4 p-4', className)}>
      <span className="sr-only">Loading chart…</span>
      <Skeleton className="h-5 w-40" />
      <Skeleton className="w-full" style={{ height }} />
    </div>
  )
}

export function SkeletonTable({ rows = 6, columns = 5, className }: { rows?: number; columns?: number; className?: string }) {
  return (
    <div role="status" aria-busy className={cn(panelClass, 'p-4', className)}>
      <span className="sr-only">Loading table…</span>
      <div className="hidden space-y-3 md:block">
        {Array.from({ length: rows + 1 }, (_, r) => (
          <div key={r} className={cn('flex gap-4', r > 0 && 'border-t border-border/60 pt-3')}>
            {Array.from({ length: columns }, (_, c) => (
              <Skeleton key={c} className={cn('flex-1', r === 0 ? 'h-4' : 'h-5')} />
            ))}
          </div>
        ))}
      </div>
      <div className="space-y-3 md:hidden">
        {Array.from({ length: Math.min(rows, 4) }, (_, r) => (
          <div key={r} className="space-y-2 rounded-lg border border-border/60 p-3">
            <Skeleton className="h-5 w-1/2" />
            <Skeleton className="h-4 w-3/4" />
          </div>
        ))}
      </div>
    </div>
  )
}

export function SkeletonPage({ className }: { className?: string }) {
  return (
    <div role="status" aria-busy className={cn('space-y-4', className)}>
      <span className="sr-only">Loading…</span>
      <Skeleton className="h-8 w-64" />
      <Skeleton className="h-4 w-96 max-w-full" />
      <Skeleton className="h-48 w-full rounded-xl" />
    </div>
  )
}
