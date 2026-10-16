import { createFileRoute } from '@tanstack/react-router'

import { ReviewPage } from '../../../../features/review/review-page'

interface ReviewSearch {
  /** FR-56: the review cycle on screen (the latest when absent). */
  cycle?: number
  /** FR-56: the exact report, only when the cycle number repeats (cycles restart after a rollback). */
  report?: string
}

export const Route = createFileRoute('/_app/projects/$projectId/review')({
  validateSearch: (search: Record<string, unknown>): ReviewSearch => {
    const cycle = typeof search.cycle === 'number' ? search.cycle : Number(search.cycle)
    const report = typeof search.report === 'string' && search.report !== '' ? search.report : undefined
    return { cycle: Number.isInteger(cycle) && cycle > 0 ? cycle : undefined, report }
  },
  component: ReviewRoute,
})

function ReviewRoute() {
  const { cycle, report } = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <ReviewPage
      cycle={cycle}
      report={report}
      onSelectionChange={(next) => {
        void navigate({ search: (prev) => ({ ...prev, cycle: next.cycle, report: next.report }), replace: true })
      }}
    />
  )
}
