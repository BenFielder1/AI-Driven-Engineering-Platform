import { ArrowDown, ArrowUp, ChevronLeft, ChevronRight, ChevronsUpDown } from 'lucide-react'
import { useState, type ReactNode } from 'react'

import { MD_QUERY, useMediaQuery } from '../../lib/use-media-query'
import { cn } from '../../lib/utils'
import { EmptyState } from './empty-state'
import { panelClass } from './panel-class'

export type SortDir = 'asc' | 'desc'

export interface TableSort {
  key: string
  dir: SortDir
}

export interface DataTableColumn<T> {
  key: string
  header: string
  cell: (row: T) => ReactNode
  /** Makes the column sortable. Client-side sorting uses it; with `onSortChange` the caller sorts. */
  sortValue?: (row: T) => string | number
  align?: 'left' | 'right' | 'center'
  /** Monospace tabular figures (counts, tokens); right-aligned unless `align` is set. */
  numeric?: boolean
  /** Label in the stacked card below md; defaults to `header`. */
  mobileLabel?: string
  hideOnMobile?: boolean
  /** The card title below md. Defaults to the first column. */
  primary?: boolean
  className?: string
}

export interface DataTableProps<T> {
  columns: readonly DataTableColumn<T>[]
  rows: readonly T[]
  rowKey: (row: T) => string
  /** Accessible table name (visually hidden caption). */
  caption: string
  /** Controlled sort (server-side): pass both. Otherwise the table sorts `rows` itself. */
  sort?: TableSort | null
  onSortChange?: (sort: TableSort) => void
  initialSort?: TableSort
  /** Rows per page. Client-side unless `page`, `total` and `onPageChange` are passed (server-side paging). */
  pageSize?: number
  page?: number
  total?: number
  onPageChange?: (page: number) => void
  /** Shown instead of the table when there are no rows. */
  empty?: ReactNode
  /** Marks the rows as stale while a new page or sort loads. */
  busy?: boolean
  /** Test id on the table / card list (defaults to `data-table` / `data-table-cards`). */
  className?: string
}

const alignClass = {
  left: 'text-left',
  right: 'text-right',
  center: 'text-center',
} as const

function alignOf<T>(col: DataTableColumn<T>): 'left' | 'right' | 'center' {
  return col.align ?? (col.numeric ? 'right' : 'left')
}

const controlClass =
  'inline-flex min-h-11 items-center gap-1 rounded-lg outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50'

function SortIcon({ dir }: { dir: SortDir | null }) {
  if (dir === 'asc') {
    return <ArrowUp className="size-3.5" aria-hidden />
  }
  if (dir === 'desc') {
    return <ArrowDown className="size-3.5" aria-hidden />
  }
  return <ChevronsUpDown className="size-3.5 opacity-60" aria-hidden />
}

function compare(a: string | number, b: string | number): number {
  return typeof a === 'number' && typeof b === 'number' ? a - b : String(a).localeCompare(String(b))
}

/**
 * The shared data table (SPEC §7.1): sortable headers (aria-sort), paging, a semantic <table> from md up and stacked
 * cards below md. Sorting and paging are client-side by default, or controlled for server-side lists.
 */
export function DataTable<T>({
  columns,
  rows,
  rowKey,
  caption,
  sort: controlledSort,
  onSortChange,
  initialSort,
  pageSize,
  page: controlledPage,
  total,
  onPageChange,
  empty,
  busy = false,
  className,
}: DataTableProps<T>) {
  // One layout at a time (not both hidden by CSS), so assistive tech and tests see a single table or card list.
  const desktop = useMediaQuery(MD_QUERY)
  const [localSort, setLocalSort] = useState<TableSort | null>(initialSort ?? null)
  const [localPage, setLocalPage] = useState(1)
  const serverSort = onSortChange !== undefined
  const serverPaging = onPageChange !== undefined && controlledPage !== undefined && total !== undefined
  const sort = serverSort ? (controlledSort ?? null) : localSort

  function changeSort(key: string) {
    const next: TableSort = sort?.key === key ? { key, dir: sort.dir === 'asc' ? 'desc' : 'asc' } : { key, dir: 'asc' }
    if (serverSort) {
      onSortChange(next)
    } else {
      setLocalSort(next)
      setLocalPage(1)
    }
  }

  let visible = [...rows]
  const sortColumn = sort ? columns.find((c) => c.key === sort.key) : undefined
  if (!serverSort && sort && sortColumn?.sortValue) {
    const value = sortColumn.sortValue
    visible.sort((a, b) => compare(value(a), value(b)) * (sort.dir === 'asc' ? 1 : -1))
  }
  const page = serverPaging ? controlledPage : localPage
  const count = serverPaging ? total : visible.length
  const pages = pageSize ? Math.max(1, Math.ceil(count / pageSize)) : 1
  if (pageSize && !serverPaging) {
    visible = visible.slice((page - 1) * pageSize, page * pageSize)
  }
  const goTo = (next: number) => {
    if (serverPaging) {
      onPageChange(next)
    } else {
      setLocalPage(next)
    }
  }

  if (rows.length === 0) {
    return <div className={className}>{empty ?? <EmptyState variant="no_data" title="Nothing to show yet" />}</div>
  }

  const primaryIndex = Math.max(
    0,
    columns.findIndex((c) => c.primary),
  )
  const dirFor = (key: string): SortDir | null => (sort?.key === key ? sort.dir : null)
  const sortable = columns.filter((c) => c.sortValue)

  return (
    <div className={cn('min-w-0 space-y-3', className)}>
      {!desktop && sortable.length > 0 && (
        <div className="flex flex-wrap items-center gap-2" data-testid="data-table-mobile-sort">
          <span className="text-xs text-muted-foreground">Sort by</span>
          {sortable.map((col) => {
            const dir = dirFor(col.key)
            return (
              <button
                key={col.key}
                type="button"
                onClick={() => {
                  changeSort(col.key)
                }}
                aria-pressed={dir !== null}
                className={cn(
                  controlClass,
                  'rounded-full border px-3 text-xs',
                  dir ? 'border-primary/50 bg-primary/10 text-foreground' : 'border-border text-muted-foreground',
                )}
              >
                {col.header}
                <SortIcon dir={dir} />
              </button>
            )
          })}
        </div>
      )}

      {desktop ? (
        <div className={cn(panelClass, 'overflow-x-auto')}>
          <table className="w-full text-sm" data-testid="data-table" aria-busy={busy || undefined}>
            <caption className="sr-only">{caption}</caption>
            <thead>
              <tr className="border-b border-border/60">
                {columns.map((col) => {
                  const dir = dirFor(col.key)
                  const align = alignOf(col)
                  return (
                    <th
                      key={col.key}
                      scope="col"
                      aria-sort={
                        col.sortValue
                          ? dir === 'asc'
                            ? 'ascending'
                            : dir === 'desc'
                              ? 'descending'
                              : 'none'
                          : undefined
                      }
                      className={cn(
                        'px-3 font-medium whitespace-nowrap text-muted-foreground',
                        alignClass[align],
                        col.className,
                      )}
                    >
                      {col.sortValue ? (
                        <button
                          type="button"
                          onClick={() => {
                            changeSort(col.key)
                          }}
                          className={cn(controlClass, 'hover:text-foreground', align === 'right' && 'flex-row-reverse')}
                        >
                          {col.header}
                          <SortIcon dir={dir} />
                        </button>
                      ) : (
                        <span className="inline-flex min-h-11 items-center">{col.header}</span>
                      )}
                    </th>
                  )
                })}
              </tr>
            </thead>
            <tbody>
              {visible.map((row) => (
                <tr
                  key={rowKey(row)}
                  className="border-b border-border/40 transition-colors last:border-0 hover:bg-muted/40"
                >
                  {columns.map((col) => (
                    <td
                      key={col.key}
                      data-numeric={col.numeric ? '' : undefined}
                      className={cn('px-3 py-2 whitespace-nowrap', alignClass[alignOf(col)], col.className)}
                    >
                      {col.cell(row)}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <ul className="space-y-2" data-testid="data-table-cards" aria-label={caption} aria-busy={busy || undefined}>
          {visible.map((row) => (
            <li key={rowKey(row)} className={cn(panelClass, 'p-3')}>
              <div className="min-w-0 truncate text-sm font-medium">{columns[primaryIndex]?.cell(row)}</div>
              <dl className="mt-1 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1.5 text-sm">
                {columns.map((col, i) =>
                  i === primaryIndex || col.hideOnMobile ? null : (
                    <div key={col.key} className="contents">
                      <dt className="text-muted-foreground">{col.mobileLabel ?? col.header}</dt>
                      <dd className={cn('min-w-0 text-right break-words', col.numeric && 'tabular')}>
                        {col.cell(row)}
                      </dd>
                    </div>
                  ),
                )}
              </dl>
            </li>
          ))}
        </ul>
      )}

      {pageSize !== undefined && pages > 1 && (
        <nav aria-label={`${caption} pages`} className="flex flex-wrap items-center justify-between gap-2 text-sm">
          <p className="text-muted-foreground">
            <span>
              Page {page} of {pages}
            </span>
            <span className="hidden sm:inline"> · {count} results</span>
          </p>
          <div className="flex items-center gap-2">
            <button
              type="button"
              disabled={page <= 1}
              onClick={() => {
                goTo(page - 1)
              }}
              aria-label="Previous page"
              className={cn(
                controlClass,
                'min-w-11 justify-center border border-border px-3 hover:bg-accent disabled:opacity-50',
              )}
            >
              <ChevronLeft className="size-4" aria-hidden />
              <span className="hidden sm:inline">Previous</span>
            </button>
            <button
              type="button"
              disabled={page >= pages}
              onClick={() => {
                goTo(page + 1)
              }}
              aria-label="Next page"
              className={cn(
                controlClass,
                'min-w-11 justify-center border border-border px-3 hover:bg-accent disabled:opacity-50',
              )}
            >
              <span className="hidden sm:inline">Next</span>
              <ChevronRight className="size-4" aria-hidden />
            </button>
          </div>
        </nav>
      )}
    </div>
  )
}
