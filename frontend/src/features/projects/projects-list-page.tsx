import { Link } from '../../lib/navigation'
import { useEffect, useRef, useState } from 'react'

import { Plus } from 'lucide-react'

import { DataTable, type DataTableColumn, type TableSort } from '../../components/shared/data-table'
import { EmptyState } from '../../components/shared/empty-state'
import { MetricCard } from '../../components/shared/metric-card'
import { PageHeader } from '../../components/shared/page-header'
import { panelClass } from '../../components/shared/panel-class'
import { SkeletonCards, SkeletonTable } from '../../components/shared/skeletons'
import { ProjectVersionBadges } from '../../components/shared/version-badge'
import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { resultSummary } from '../../components/ui/result-summary'
import { ErrorState } from '../../components/ui/states'
import { SelectField } from '../../components/ui/select-field'
import { TextField } from '../../components/ui/text-field'
import { cn } from '../../lib/utils'
import { formatDateTime, formatNumber, phaseLabels, phases, roleLabels, statusLabels, statuses } from './labels'
import { PresetChip } from '../presets/preset-chip'
import { usePresets } from '../presets/presets-api'
import { useStackCatalog } from '../stack/stack-api'
import { catalogPacks, StackChips } from '../stack/stack-chips'
import { PhaseBadge, StatusBadge } from './project-badges'
import {
  PROJECTS_PAGE_SIZE,
  useProjectKpis,
  useProjects,
  type ProjectKpiFilters,
  type ProjectSortBy,
  type ProjectSummary,
  type SortOrder,
} from './projects-api'
import { oneOf, SEARCH_DEBOUNCE_MS, type ProjectsSearch } from './projects-search'

const sortByOptions: Record<ProjectSortBy, string> = { updated: 'Last updated', created: 'Created', name: 'Name' }
const sortOrderOptions: Record<SortOrder, string> = { desc: 'Descending', asc: 'Ascending' }

interface ProjectsListPageProps {
  search: ProjectsSearch
  onSearchChange: (next: ProjectsSearch) => void
}

/**
 * SPEC 7.2 KPI row (FR-53): active, in review, awaiting gates and released, counted by GET /projects/kpis over the
 * projects matching the list's filters.
 */
function ProjectKpiRow({ filters, filtered }: { filters: ProjectKpiFilters; filtered: boolean }) {
  const kpis = useProjectKpis(filters)
  if (kpis.isPending) {
    return <SkeletonCards className="mb-5" />
  }
  // The list itself reports load errors; the tiles just stay empty.
  const value = (n: number | undefined) => (n === undefined ? '—' : formatNumber(n))
  const footer = filtered && kpis.data ? `Of ${formatNumber(kpis.data.total)} matching ${kpis.data.total === 1 ? 'project' : 'projects'}` : undefined
  return (
    <section aria-label="Project totals" className="mb-5 grid grid-cols-2 gap-3 lg:grid-cols-4">
      <MetricCard label="Active" value={value(kpis.data?.active)} footer={footer} />
      <MetricCard label="In review" value={value(kpis.data?.inReview)} footer={footer} />
      <MetricCard label="Awaiting gates" value={value(kpis.data?.awaitingGates)} footer={footer} />
      <MetricCard label="Released" value={value(kpis.data?.released)} footer={footer} />
    </section>
  )
}

/** `?preset=none`: projects whose current version has no preset (FR-74). */
const NO_PRESET_FILTER = 'none'

const sortKeys: Partial<Record<string, ProjectSortBy>> = { name: 'name', updated: 'updated' }

const columns: DataTableColumn<ProjectSummary>[] = [
  {
    key: 'name',
    header: 'Project',
    primary: true,
    sortValue: (p) => p.name,
    cell: (p) => (
      <Link
        to="/projects/$projectId"
        params={{ projectId: p.id }}
        search={{ release: undefined }}
        className="font-medium break-words whitespace-normal text-primary underline-offset-4 hover:underline"
      >
        {p.name}
      </Link>
    ),
  },
  { key: 'preset', header: 'Preset', cell: (p) => <PresetChip name={p.presetName} version={p.presetVersion} /> },
  { key: 'stack', header: 'Stack', cell: (p) => <StackChips packs={p.stackPacks} label={`${p.name} stack`} /> },
  { key: 'phase', header: 'Phase', cell: (p) => <PhaseBadge phase={p.currentPhase} /> },
  { key: 'status', header: 'Status', cell: (p) => <StatusBadge status={p.status} /> },
  {
    key: 'versions',
    header: 'Versions',
    cell: (p) => <ProjectVersionBadges released={p.releasedVersion} development={p.developmentVersion} />,
  },
  { key: 'role', header: 'Your role', cell: (p) => (p.myRole ? roleLabels[p.myRole] : 'Admin (not a member)') },
  {
    key: 'updated',
    header: 'Updated',
    sortValue: (p) => p.updatedAt,
    cell: (p) => <time dateTime={p.updatedAt}>{formatDateTime(p.updatedAt)}</time>,
  },
]

/**
 * /projects (FR-6, SPEC 7.2): the KPI row, then the caller's projects as a DataTable (stacked cards below md) with
 * search, filters, sort and paging kept in the URL.
 */
export function ProjectsListPage({ search, onSearchChange }: ProjectsListPageProps) {
  const page = search.page ?? 1
  const sortBy = search.sortBy ?? 'updated'
  const sortOrder = search.sortOrder ?? 'desc'
  const filters: ProjectKpiFilters = {
    search: search.search,
    phase: search.phase,
    status: search.status,
    hasDevelopmentVersion: search.hasDevelopmentVersion,
    preset: search.preset,
    pack: search.pack,
  }
  const projects = useProjects({ page, limit: PROJECTS_PAGE_SIZE, ...filters, sortBy, sortOrder })
  const filtered =
    Boolean(search.search ?? search.phase ?? search.status ?? search.preset ?? search.pack) ||
    search.hasDevelopmentVersion !== undefined

  /** Any filter change goes back to page 1. */
  const update = (patch: Partial<ProjectsSearch>) => {
    onSearchChange({ ...search, ...patch, page: undefined })
  }

  const tableSort: TableSort | null = sortBy === 'created' ? null : { key: sortBy, dir: sortOrder }

  return (
    <>
      <PageHeader
        title="Projects"
        description="Every project you're a member of, from idea to release."
        className="mb-6"
        actions={
          <Link to="/projects/new" className={buttonClasses('primary')}>
            <Plus className="size-4" aria-hidden />
            New project
          </Link>
        }
      />

      <ProjectKpiRow filters={filters} filtered={filtered} />

      <ProjectFilters search={search} onChange={update} />

      {projects.isPending && <SkeletonTable />}
      {projects.isError && <ErrorState error={projects.error} onRetry={() => void projects.refetch()} />}
      {projects.isSuccess && projects.data.total === 0 && !filtered && (
        <EmptyState
          variant="no_data"
          title="No projects yet"
          description="Turn an idea into released software: start with a project."
          action={
            <Link to="/projects/new" className={buttonClasses('primary')}>
              New project
            </Link>
          }
        />
      )}
      {projects.isSuccess && projects.data.total === 0 && filtered && (
        <EmptyState
          variant="no_data"
          title="No projects match your filters"
          action={
            <Button
              variant="secondary"
              onClick={() => {
                onSearchChange({ sortBy: search.sortBy, sortOrder: search.sortOrder })
              }}
            >
              Clear filters
            </Button>
          }
        />
      )}
      {projects.isSuccess && projects.data.total > 0 && (
        <>
          <p role="status" aria-live="polite" className="mb-3 text-sm text-muted-foreground">
            {resultSummary(projects.data.page, projects.data.limit, projects.data.items.length, projects.data.total, 'project')}
          </p>
          <DataTable
            caption="Projects"
            columns={columns}
            rows={projects.data.items}
            rowKey={(p) => p.id}
            busy={projects.isPlaceholderData}
            sort={tableSort}
            onSortChange={(next) => {
              const key = sortKeys[next.key] ?? 'updated'
              update({ sortBy: key === 'updated' ? undefined : key, sortOrder: next.dir === 'desc' ? undefined : next.dir })
            }}
            pageSize={projects.data.limit}
            page={projects.data.page}
            total={projects.data.total}
            onPageChange={(next) => {
              onSearchChange({ ...search, page: next > 1 ? next : undefined })
            }}
          />
        </>
      )}
    </>
  )
}

function ProjectFilters({ search, onChange }: { search: ProjectsSearch; onChange: (patch: Partial<ProjectsSearch>) => void }) {
  const urlText = search.search ?? ''
  const [text, setText] = useState(urlText)
  const [lastUrlText, setLastUrlText] = useState(urlText)

  // The URL changed from elsewhere (e.g. "Clear filters"): show its value.
  if (urlText !== lastUrlText) {
    setLastUrlText(urlText)
    setText(urlText)
  }

  const onChangeRef = useRef(onChange)
  useEffect(() => {
    onChangeRef.current = onChange
  })

  useEffect(() => {
    if (text.trim() === urlText) {
      return undefined
    }
    const timer = setTimeout(() => {
      onChangeRef.current({ search: text.trim() || undefined })
    }, SEARCH_DEBOUNCE_MS)
    return () => {
      clearTimeout(timer)
    }
  }, [text, urlText])

  return (
    <form
      role="search"
      aria-label="Filter projects"
      onSubmit={(e) => {
        e.preventDefault()
        onChange({ search: text.trim() || undefined })
      }}
      className={cn(panelClass, 'mb-5 grid grid-cols-1 gap-3 p-4 sm:grid-cols-2 lg:grid-cols-4 xl:grid-cols-7')}>
      <TextField
        id="projects-search"
        label="Search by name"
        type="search"
        value={text}
        maxLength={200}
        className="sm:col-span-2 lg:col-span-1"
        onChange={(e) => {
          setText(e.target.value)
        }}
      />
      <SelectField
        id="projects-phase"
        label="Phase"
        value={search.phase ?? ''}
        onChange={(e) => {
          onChange({ phase: oneOf(phases, e.target.value) })
        }}
      >
        <option value="">All phases</option>
        {phases.map((p) => (
          <option key={p} value={p}>
            {phaseLabels[p]}
          </option>
        ))}
      </SelectField>
      <SelectField
        id="projects-status"
        label="Status"
        value={search.status ?? ''}
        onChange={(e) => {
          onChange({ status: oneOf(statuses, e.target.value) })
        }}
      >
        <option value="">All statuses</option>
        {statuses.map((s) => (
          <option key={s} value={s}>
            {statusLabels[s]}
          </option>
        ))}
      </SelectField>
      <SelectField
        id="projects-development"
        label="Has a version in development"
        value={search.hasDevelopmentVersion === undefined ? '' : String(search.hasDevelopmentVersion)}
        onChange={(e) => {
          const v = e.target.value
          onChange({ hasDevelopmentVersion: v === 'true' ? true : v === 'false' ? false : undefined })
        }}
      >
        <option value="">Any</option>
        <option value="true">Yes</option>
        <option value="false">No</option>
      </SelectField>
      <PresetFilter
        value={search.preset}
        onChange={(preset) => {
          onChange({ preset })
        }}
      />
      <PackFilter
        value={search.pack}
        onChange={(pack) => {
          onChange({ pack })
        }}
      />
      <SelectField
        id="projects-sort-by"
        label="Sort by"
        value={search.sortBy ?? 'updated'}
        onChange={(e) => {
          const v = oneOf(['created', 'updated', 'name'] as const, e.target.value)
          onChange({ sortBy: v === 'updated' ? undefined : v })
        }}
      >
        {Object.entries(sortByOptions).map(([value, label]) => (
          <option key={value} value={value}>
            {label}
          </option>
        ))}
      </SelectField>
      <SelectField
        id="projects-sort-order"
        label="Order"
        value={search.sortOrder ?? 'desc'}
        onChange={(e) => {
          const v = oneOf(['asc', 'desc'] as const, e.target.value)
          onChange({ sortOrder: v === 'desc' ? undefined : v })
        }}
      >
        {Object.entries(sortOrderOptions).map(([value, label]) => (
          <option key={value} value={value}>
            {label}
          </option>
        ))}
      </SelectField>
    </form>
  )
}

/** FR-74, SPEC §7.2: filter by the current version's preset: any, none (`preset=none`) or one preset id. */
function PresetFilter({ value, onChange }: { value: string | undefined; onChange: (preset: string | undefined) => void }) {
  const presets = usePresets()
  const listed = presets.data ?? []
  return (
    <SelectField
      id="projects-preset"
      label="Preset"
      value={value ?? ''}
      onChange={(e) => {
        onChange(e.target.value || undefined)
      }}
    >
      <option value="">Any preset</option>
      <option value={NO_PRESET_FILTER}>No preset</option>
      {listed.map((p) => (
        <option key={p.id} value={p.id}>
          {p.name}
        </option>
      ))}
      {value && value !== NO_PRESET_FILTER && !listed.some((p) => p.id === value) && <option value={value}>{value}</option>}
    </SelectField>
  )
}

/** FR-117, SPEC §7.2: filter by a pack of the current version's stack composition (any, or one pack id). */
function PackFilter({ value, onChange }: { value: string | undefined; onChange: (pack: string | undefined) => void }) {
  const catalog = useStackCatalog()
  const packs = catalogPacks(catalog.data)
  return (
    <SelectField
      id="projects-pack"
      label="Stack pack"
      value={value ?? ''}
      onChange={(e) => {
        onChange(e.target.value || undefined)
      }}
    >
      <option value="">Any pack</option>
      {packs.map((p) => (
        <option key={p.pack} value={p.pack}>
          {p.label}
        </option>
      ))}
      {value && !packs.some((p) => p.pack === value) && <option value={value}>{value}</option>}
    </SelectField>
  )
}
