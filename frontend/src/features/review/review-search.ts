/** /projects/:id/review search params (FR-56). */
export interface ReviewSearch {
  /** FR-56: the review cycle on screen (the latest when absent). */
  cycle?: number
  /** FR-56: the exact report, only when the cycle number repeats (cycles restart after a rollback). */
  report?: string
}

/** Parses untrusted URL search params into ReviewSearch (invalid values are dropped). */
export function parseReviewSearch(search: Record<string, unknown>): ReviewSearch {
  const cycle = typeof search.cycle === 'number' ? search.cycle : Number(search.cycle)
  const report = typeof search.report === 'string' && search.report !== '' ? search.report : undefined
  return { cycle: Number.isInteger(cycle) && cycle > 0 ? cycle : undefined, report }
}
