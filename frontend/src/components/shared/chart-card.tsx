import { BarChart3, Table2 } from 'lucide-react'
import { useId, useState, type ReactNode } from 'react'

import { cn } from '../../lib/utils'
import { buttonClasses } from '../ui/button-classes'
import { panelClass } from './panel-class'

export interface ChartTableColumn {
  key: string
  header: string
  align?: 'left' | 'right'
}

export interface ChartTableData {
  /** The table's accessible name; defaults to the card title. */
  caption?: string
  columns: readonly ChartTableColumn[]
  /** Pre-formatted cells keyed by column key. */
  rows: readonly Record<string, ReactNode>[]
}

/**
 * Card for every chart (SPEC §7.1), with a "View as table" toggle that swaps the chart for an accessible data table
 * (NFR: charts have table alternatives).
 */
export function ChartCard({
  title,
  description,
  actions,
  table,
  headingLevel = 2,
  children,
  className,
}: {
  title: string
  /** Keeps the outline right when the card sits under an h2 (e.g. a stage page section). */
  headingLevel?: 2 | 3
  description?: ReactNode
  actions?: ReactNode
  table?: ChartTableData
  children: ReactNode
  className?: string
}) {
  const [showTable, setShowTable] = useState(false)
  const headingId = useId()
  const regionId = useId()
  const Heading = headingLevel === 3 ? 'h3' : 'h2'

  return (
    <section aria-labelledby={headingId} className={cn(panelClass, 'flex min-w-0 flex-col gap-4 p-4', className)}>
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0 flex-1">
          <Heading id={headingId} className="text-base">
            {title}
          </Heading>
          {description && <p className="mt-0.5 text-sm text-muted-foreground">{description}</p>}
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {actions}
          {table && (
            <button
              type="button"
              className={buttonClasses('ghost', 'min-h-11 px-3')}
              aria-pressed={showTable}
              aria-controls={regionId}
              onClick={() => {
                setShowTable((v) => !v)
              }}
            >
              {showTable ? <BarChart3 className="size-4" aria-hidden /> : <Table2 className="size-4" aria-hidden />}
              {showTable ? 'View as chart' : 'View as table'}
            </button>
          )}
        </div>
      </div>
      <div id={regionId} className="min-w-0">
        {table && showTable ? (
          // NFR-A11Y: a wide table scrolls inside a keyboard-reachable region instead of the page scrolling sideways.
          <div
            role="region"
            aria-label={`${title} table`}
            tabIndex={0}
            data-testid="chart-table-region"
            className="max-h-96 overflow-auto rounded-lg border border-border/60 outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
          >
            <table className="w-full text-sm">
              <caption className="sr-only">{table.caption ?? title}</caption>
              <thead className="sticky top-0 bg-card">
                <tr className="border-b border-border/60">
                  {table.columns.map((c) => (
                    <th
                      key={c.key}
                      scope="col"
                      className={cn(
                        'px-3 py-2 font-medium whitespace-nowrap text-muted-foreground',
                        c.align === 'right' ? 'text-right' : 'text-left',
                      )}
                    >
                      {c.header}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {table.rows.map((row, r) => (
                  <tr key={r} className="border-b border-border/40 last:border-0">
                    {table.columns.map((c, ci) =>
                      ci === 0 ? (
                        <th key={c.key} scope="row" className="px-3 py-2 text-left font-normal whitespace-nowrap">
                          {row[c.key]}
                        </th>
                      ) : (
                        <td
                          key={c.key}
                          data-numeric={c.align === 'right' ? '' : undefined}
                          className={cn('px-3 py-2 whitespace-nowrap', c.align === 'right' ? 'text-right' : 'text-left')}
                        >
                          {row[c.key]}
                        </td>
                      ),
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          children
        )}
      </div>
    </section>
  )
}
