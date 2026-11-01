import { cn } from '../../lib/utils'

/** Small neutral chips, e.g. a stack option's features (FR-114). */
export function TagList({ label, items, className }: { label: string; items: readonly string[]; className?: string }) {
  if (items.length === 0) {
    return null
  }
  return (
    <ul aria-label={label} className={cn('flex flex-wrap gap-1', className)}>
      {items.map((item) => (
        <li key={item} className="rounded border border-border/60 bg-background/60 px-1.5 py-0.5 text-xs">
          {item}
        </li>
      ))}
    </ul>
  )
}
