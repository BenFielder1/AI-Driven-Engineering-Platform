'use client'

import { ReviewPage } from '../../../../../features/review/review-page'
import { parseReviewSearch } from '../../../../../features/review/review-search'
import { useSearch, useSetSearch } from '../../../../../lib/navigation'

/** /projects/:id/review: the review stage page; `?cycle=N&report=` picks the review cycle shown (FR-56). */
export default function ReviewRoute() {
  const { cycle, report } = useSearch(parseReviewSearch)
  const setSearch = useSetSearch()
  return (
    <ReviewPage
      cycle={cycle}
      report={report}
      onSelectionChange={(next) => {
        void setSearch((prev) => ({ ...prev, cycle: next.cycle, report: next.report }), { replace: true })
      }}
    />
  )
}
