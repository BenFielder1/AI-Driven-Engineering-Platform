'use client'

import { AdminEventsPage } from '../../../../features/admin/admin-events-page'
import { adminEventsSearchSchema } from '../../../../features/admin/admin-search'
import { useSearch, useSetSearch } from '../../../../lib/navigation'

/** Filter and page changes replace the history entry. */
export default function AdminEventsRoute() {
  const search = useSearch(adminEventsSearchSchema)
  const setSearch = useSetSearch()
  return (
    <AdminEventsPage
      search={search}
      onSearchChange={(next) => {
        void setSearch(next, { replace: true })
      }}
    />
  )
}
