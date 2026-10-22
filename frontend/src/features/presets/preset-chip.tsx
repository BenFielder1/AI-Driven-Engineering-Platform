import { Layers } from 'lucide-react'

import { cn } from '../../lib/utils'

/**
 * The preset chip (SPEC §7.2, FR-74): "Preset Next.js frontend 1.0.0", or "No preset" (general). The text reads as one
 * phrase for screen readers; the icon is decorative.
 */
export function PresetChip({
  name,
  version,
  className,
  hideWhenNone = false,
}: {
  name: string | null | undefined
  version?: string | null | undefined
  className?: string
  /** Render nothing for a project without a preset (compact places such as the switcher). */
  hideWhenNone?: boolean
}) {
  if (!name && hideWhenNone) {
    return null
  }
  return (
    <span
      data-testid="preset-chip"
      className={cn(
        'inline-flex max-w-full items-center gap-1 rounded-full border px-2 py-0.5 text-xs',
        name ? 'border-primary/40 bg-primary/10 text-foreground' : 'border-border bg-muted text-muted-foreground',
        className,
      )}
    >
      <Layers aria-hidden className="size-3 shrink-0" />
      {name ? (
        <span className="truncate">
          <span className="sr-only">Preset </span>
          {name}
          {version && <span className="font-mono tabular-nums text-muted-foreground"> {version}</span>}
        </span>
      ) : (
        <span>No preset</span>
      )}
    </span>
  )
}

/** Small neutral chips for a preset's stack or deployment targets. */
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
