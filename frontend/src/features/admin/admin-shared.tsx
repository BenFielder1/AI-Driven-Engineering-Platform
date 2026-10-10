import type { UseQueryResult } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import type { SubmitEventHandler, ReactNode } from 'react'

import { Button } from '../../components/ui/button'
import { Pagination } from '../../components/ui/pagination'
import { resultSummary } from '../../components/ui/result-summary'
import { SelectField } from '../../components/ui/select-field'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { useProjects } from '../projects/projects-api'

interface Paged<T> {
  items: T[]
  page: number
  limit: number
  total: number
}

interface PagedListProps<T> {
  query: UseQueryResult<Paged<T>>
  /** Singular noun for the result summary ("event" → "Showing 1–20 of 57 events"). */
  noun: string
  loadingLabel: string
  errorTitle: string
  /** Whether any filter is applied (switches the empty state to "nothing matches"). */
  filtered: boolean
  emptyTitle: string
  emptyText: ReactNode
  onClearFilters: () => void
  onPage: (page: number) => void
  children: (items: T[]) => ReactNode
}

/** Loading, error, empty (with and without filters), result summary, the table and paging of an admin list. */
export function PagedList<T>({
  query,
  noun,
  loadingLabel,
  errorTitle,
  filtered,
  emptyTitle,
  emptyText,
  onClearFilters,
  onPage,
  children,
}: PagedListProps<T>) {
  if (query.isPending) {
    return <LoadingState label={loadingLabel} />
  }
  if (query.isError) {
    return <ErrorState error={query.error} onRetry={() => void query.refetch()} title={errorTitle} />
  }
  const d = query.data
  if (d.total === 0 && filtered) {
    return (
      <EmptyState title={`No ${noun}s match these filters`}>
        <Button variant="secondary" className="mt-3" onClick={onClearFilters}>
          Clear filters
        </Button>
      </EmptyState>
    )
  }
  if (d.total === 0) {
    return <EmptyState title={emptyTitle}>{emptyText}</EmptyState>
  }
  const pageCount = Math.max(1, Math.ceil(d.total / d.limit))
  return (
    <>
      <p role="status" aria-live="polite" className="mb-3 text-sm text-muted">
        {d.items.length === 0
          ? `Page ${String(d.page)} is past the last page (${String(pageCount)}).`
          : resultSummary(d.page, d.limit, d.items.length, d.total, noun)}
      </p>
      <div aria-busy={query.isPlaceholderData || undefined}>{d.items.length > 0 && children(d.items)}</div>
      <Pagination page={d.page}pageCount={Math.max(pageCount, d.page)} onPage={onPage} />
    </>
  )
}

/** A filter form: its fields, then Apply and Clear. Applying writes the filters to the URL (and goes to page 1). */
export function FilterForm({
  label,
  onSubmit,
  onClear,
  children,
}: {
  label: string
  onSubmit: SubmitEventHandler<HTMLFormElement>
  onClear: () => void
  children: ReactNode
}) {
  return (
    <form
      noValidate
      role="search"
      aria-label={label}
      onSubmit={onSubmit}
      className="mb-5 rounded-lg border border-border bg-surface p-4"
    >
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">{children}</div>
      <div className="mt-4 flex flex-wrap gap-2">
        <Button type="submit">Apply filters</Button>
        <Button variant="secondary" onClick={onClear}>
          Clear filters
        </Button>
      </div>
    </form>
  )
}

/** Projects for the project filter: every project (Admins see all), alphabetically, first 100. */
export function ProjectSelect({
  id,
  value,
  onChange,
}: {
  id: string
  value: string
  onChange: (projectId: string) => void
}) {
  const projects = useProjects({ sortBy: 'name', sortOrder: 'asc', page: 1, limit: 100 })
  const items = projects.data?.items ?? []
  const known = value === '' || items.some((p) => p.id === value)
  return (
    <SelectField
      id={id}
      label="Project"
      value={value}
      hint={projects.isError ? 'The project list couldn’t load.' : undefined}
      onChange={(e) => {
        onChange(e.target.value)
      }}
    >
      <option value="">All projects</option>
      {!known && <option value={value}>{projects.isPending ? 'Loading projects…' : 'Unknown project'}</option>}
      {items.map((p) => (
        <option key={p.id} value={p.id}>
          {p.name}
        </option>
      ))}
    </SelectField>
  )
}

/** The project's name linked to its overview, or a dash for records without a project. */
export function ProjectCell({ id, name }: { id: string | null; name: string | null }) {
  if (!id) {
    return <span className="text-muted">—</span>
  }
  return (
    <Link to="/projects/$projectId" params={{ projectId: id }} className="text-link underline underline-offset-4">
      {name ?? 'Deleted project'}
    </Link>
  )
}

export const headCell = 'py-2 pr-3 font-medium'
export const bodyCell = 'py-2 pr-3 align-top'

/** A horizontally scrollable table with a caption (visible to screen readers) and column headers. */
export function AdminTable({
  caption,
  headers,
  minWidth = '44rem',
  children,
}: {
  caption: string
  headers: readonly (string | { label: string; align: 'right' })[]
  minWidth?: string
  children: ReactNode
}) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse text-sm" style={{ minWidth }}>
        <caption className="sr-only">{caption}</caption>
        <thead>
          <tr className="border-b border-border text-left text-xs text-muted">
            {headers.map((h) => {
              const label = typeof h === 'string' ? h : h.label
              return (
                <th key={label} scope="col" className={typeof h === 'string' ? headCell : `${headCell} text-right`}>
                  {label}
                </th>
              )
            })}
          </tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  )
}
