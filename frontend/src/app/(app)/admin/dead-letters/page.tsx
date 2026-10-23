'use client'

import { AdminDeadLettersPage } from '../../../../features/admin/admin-dead-letters-page'
import { deadLettersSearchSchema } from '../../../../features/admin/admin-search'
import { useSearch, useSetSearch } from '../../../../lib/navigation'

/** Filter and page changes replace the history entry. */
export default function AdminDeadLettersRoute() {
  const search = useSearch(deadLettersSearchSchema)
  const setSearch = useSetSearch()
  return (
    <AdminDeadLettersPage
      search={search}
      onSearchChange={(next) => {
        void setSearch(next, { replace: true })
      }}
    />
  )
}
