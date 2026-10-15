import type { ProjectSummary } from './projects-api'

/** How many projects the KPI row reads (the list API's maximum page size). */
export const KPI_SAMPLE_SIZE = 100

export interface ProjectKpis {
  /** Not yet released (in any phase, including rolled back). */
  active: number
  /** Status `reviewing`. */
  inReview: number
  /** Pending gates across projects: the list API doesn't report them yet, so `null` (shown as "—"). */
  awaitingGates: number | null
  released: number
  /** True when there are more projects than were read. */
  partial: boolean
}

/** SPEC §7.2 /projects KPI row, computed from what GET /projects returns today. */
export function projectKpis(items: readonly Pick<ProjectSummary, 'status'>[], total: number): ProjectKpis {
  const count = (pred: (s: ProjectSummary['status']) => boolean) => items.filter((p) => pred(p.status)).length
  return {
    active: count((s) => s !== 'released'),
    inReview: count((s) => s === 'reviewing'),
    awaitingGates: null,
    released: count((s) => s === 'released'),
    partial: total > items.length,
  }
}
