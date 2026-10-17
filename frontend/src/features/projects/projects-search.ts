import { phases, statuses } from './labels'
import type { Phase, ProjectSortBy, ProjectStatus, SortOrder } from './projects-api'

/** /projects search params (FR-6). Defaults are omitted from the URL. */
export interface ProjectsSearch {
  search?: string
  phase?: Phase
  status?: ProjectStatus
  /** FR-62: only projects with (true) or without (false) a version in development. */
  hasDevelopmentVersion?: boolean
  /** FR-74: only projects whose current version uses this preset id. */
  preset?: string
  sortBy?: ProjectSortBy
  sortOrder?: SortOrder
  page?: number
}

/** A preset id as the API accepts it (§4.11 folder names: lowercase letters, digits and dashes). */
const PRESET_ID = /^[a-z0-9][a-z0-9-]{0,63}$/

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
    hasDevelopmentVersion:
      search.hasDevelopmentVersion === true || search.hasDevelopmentVersion === 'true'
        ? true
        : search.hasDevelopmentVersion === false || search.hasDevelopmentVersion === 'false'
          ? false
          : undefined,
    preset: typeof search.preset === 'string' && PRESET_ID.test(search.preset) ? search.preset : undefined,
    sortBy: oneOf(['created', 'updated', 'name'] as const, search.sortBy),
    sortOrder: oneOf(['asc', 'desc'] as const, search.sortOrder),
    page: Number.isInteger(page) && page > 1 ? page : undefined,
  }
}

/** Search input debounce (ms). */
export const SEARCH_DEBOUNCE_MS = 300
