import { Link } from '@tanstack/react-router'
import { useEffect, useRef, useState } from 'react'

import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { Pagination } from '../../components/ui/pagination'
import { resultSummary } from '../../components/ui/result-summary'
import { EmptyState, ErrorState, LoadingState, PageHeader } from '../../components/ui/states'
import { SelectField } from '../../components/ui/select-field'
import { TextField } from '../../components/ui/text-field'
import { formatDateTime, phaseLabels, phases, roleLabels, statusLabels, statuses } from './labels'
import { PhaseBadge, StatusBadge } from './project-badges'
import { PROJECTS_PAGE_SIZE, useProjects, type ProjectSortBy, type ProjectSummary, type SortOrder } from './projects-api'
import { oneOf, SEARCH_DEBOUNCE_MS, type ProjectsSearch } from './projects-search'

const sortByOptions: Record<ProjectSortBy, string> = { updated: 'Last updated', created: 'Created', name: 'Name' }
const sortOrderOptions: Record<SortOrder, string> = { desc: 'Descending', asc: 'Ascending' }

interface ProjectsListPageProps {
  search: ProjectsSearch
  onSearchChange: (next: ProjectsSearch) => void
}

/** /projects (FR-6): the caller's projects with search, filters, sort and paging kept in the URL. */
export function ProjectsListPage({ search, onSearchChange }: ProjectsListPageProps) {
  const page = search.page ?? 1
  const projects = useProjects({
    page,
    limit: PROJECTS_PAGE_SIZE,
    search: search.search,
    phase: search.phase,
    status: search.status,
    sortBy: search.sortBy ?? 'updated',
    sortOrder: search.sortOrder ?? 'desc',
  })
  const filtered = Boolean(search.search ?? search.phase ?? search.status)

  /** Any filter change goes back to page 1. */
  const update = (patch: Partial<ProjectsSearch>) => {
    onSearchChange({ ...search, ...patch, page: undefined })
  }

  return (
    <>
      <PageHeader title="Projects">
        <Link to="/projects/new" className={buttonClasses('primary')}>
          New project
        </Link>
      </PageHeader>

      <ProjectFilters search={search} onChange={update} />

      {projects.isPending && <LoadingState label="Loading projects…" />}
      {projects.isError && <ErrorState error={projects.error} onRetry={() => void projects.refetch()} />}
      {projects.isSuccess && projects.data.total === 0 && !filtered && (
        <EmptyState title="No projects yet">
          <p>Turn an idea into released software: start with a project.</p>
          <Link to="/projects/new" className={buttonClasses('primary', 'mt-3')}>
            New project
          </Link>
        </EmptyState>
      )}
      {projects.isSuccess && projects.data.total === 0 && filtered && (
        <EmptyState title="No projects match your filters">
          <Button
            variant="secondary"
            className="mt-3"
            onClick={() => {
              onSearchChange({ sortBy: search.sortBy, sortOrder: search.sortOrder })
            }}
          >
            Clear filters
          </Button>
        </EmptyState>
      )}
      {projects.isSuccess && projects.data.total > 0 && (
        <>
          <p role="status" aria-live="polite" className="mb-3 text-sm text-muted">
            {resultSummary(projects.data.page, projects.data.limit, projects.data.items.length, projects.data.total, 'project')}
          </p>
          <ul aria-label="Projects" aria-busy={projects.isPlaceholderData || undefined} className="flex flex-col gap-3">
            {projects.data.items.map((p) => (
              <ProjectCard key={p.id} project={p} />
            ))}
          </ul>
          <Pagination
            page={projects.data.page}
            pageCount={Math.max(1, Math.ceil(projects.data.total / projects.data.limit))}
            onPage={(next) => {
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
      className="mb-5 grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-5">
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

function ProjectCard({ project }: { project: ProjectSummary }) {
  return (
    <li className="rounded-lg border border-border bg-surface-raised p-4">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <h2 className="min-w-0 break-words text-base font-semibold">
          <Link
            to="/projects/$projectId"
            params={{ projectId: project.id }}
            className="text-link underline-offset-4 hover:underline"
          >
            {project.name}
          </Link>
        </h2>
        <div className="flex flex-wrap items-center gap-1">
          <PhaseBadge phase={project.currentPhase} />
          <StatusBadge status={project.status} />
        </div>
      </div>
      <dl className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-sm text-muted">
        <div className="flex gap-1">
          <dt>Version</dt>
          <dd className="text-fg">{project.projectVersion}</dd>
        </div>
        <div className="flex gap-1">
          <dt>Your role</dt>
          <dd className="text-fg">{project.myRole ? roleLabels[project.myRole] : 'Admin (not a member)'}</dd>
        </div>
        <div className="flex gap-1">
          <dt>Updated</dt>
          <dd className="text-fg">
            <time dateTime={project.updatedAt}>{formatDateTime(project.updatedAt)}</time>
          </dd>
        </div>
      </dl>
    </li>
  )
}
