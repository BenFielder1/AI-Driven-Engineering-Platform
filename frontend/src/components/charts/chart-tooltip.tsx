import { cn } from '../../lib/utils'

interface TooltipEntry {
  name?: unknown
  value?: unknown
  color?: string
  fill?: string
  stroke?: string
  dataKey?: unknown
}

export interface ChartTooltipProps {
  active?: boolean
  label?: string | number
  payload?: readonly TooltipEntry[]
  valueFormatter?: (value: number) => string
  labelFormatter?: (label: string | number) => string
  className?: string
}

const text = (v: unknown, fallback = ''): string => (typeof v === 'string' || typeof v === 'number' ? String(v) : fallback)

/** The frosted tooltip for every themed chart: `<Tooltip content={<ChartTooltip />} />` (SPEC §7.1). */
export function ChartTooltip({
  active,
  label,
  payload,
  valueFormatter = (v) => String(v),
  labelFormatter,
  className,
}: ChartTooltipProps) {
  if (!active || !payload || payload.length === 0) {
    return null
  }
  return (
    <div
      className={cn(
        'min-w-36 rounded-lg border border-border/60 bg-popover/90 px-3 py-2 text-xs text-popover-foreground shadow-lg backdrop-blur-md',
        className,
      )}
    >
      {label !== undefined && label !== '' && (
        <p className="mb-1.5 font-medium">{labelFormatter ? labelFormatter(label) : String(label)}</p>
      )}
      <ul className="space-y-1">
        {payload.map((entry, i) => (
          <li key={text(entry.dataKey, text(entry.name, String(i)))} className="flex items-center justify-between gap-4">
            <span className="flex items-center gap-1.5 text-muted-foreground">
              <span
                aria-hidden
                className="size-2 shrink-0 rounded-full"
                style={{ backgroundColor: entry.color ?? entry.fill ?? entry.stroke }}
              />
              {text(entry.name, text(entry.dataKey))}
            </span>
            <span className="font-mono font-medium tabular-nums">
              {typeof entry.value === 'number' ? valueFormatter(entry.value) : text(entry.value, '—')}
            </span>
          </li>
        ))}
      </ul>
    </div>
  )
}
