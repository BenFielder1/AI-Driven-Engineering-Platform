import { phases, statuses } from './labels'
import type { Phase, ProjectSortBy, ProjectStatus, SortOrder } from './projects-api'

/** /projects search params (FR-6). Defaults are omitted from the URL. */
export interface ProjectsSearch {
  search?: string
  phase?: Phase
  status?: ProjectStatus
  sortBy?: ProjectSortBy
  sortOrder?: SortOrder
  page?: number
}

export const oneOf = <T extends string>(values: readonly T[], v: unknown): T | undefined =>
  typeof v === 'string' && (values as readonly string[]).includes(v) ? (v as T) : undefined

/** Parses untrusted URL search params into ProjectsSearch (invalid values are dropped). */
export function parseProjectsSearch(search: Record<string, unknown>): ProjectsSearch {
  const text = typeof search.search === 'string' ? search.search.trim().slice(0, 200) : ''
  const page = typeof search.page === 'number' ? search.page : Number(search.page)
  return {
    search: text || undefined,
    phase: oneOf(phases, search.phase),
    status: oneOf(statuses, search.status),
    sortBy: oneOf(['created', 'updated', 'name'] as const, search.sortBy),
    sortOrder: oneOf(['asc', 'desc'] as const, search.sortOrder),
    page: Number.isInteger(page) && page > 1 ? page : undefined,
  }
}

/** Search input debounce (ms). */
export const SEARCH_DEBOUNCE_MS = 300
