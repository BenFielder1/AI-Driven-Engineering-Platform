'use client'

import { adminUsersSearchSchema } from '../../../../features/admin/admin-search'
import { AdminUsersPage } from '../../../../features/admin/admin-users-page'
import { useSearch, useSetSearch } from '../../../../lib/navigation'

/** Search and page changes replace the history entry. */
export default function AdminUsersRoute() {
  const search = useSearch(adminUsersSearchSchema)
  const setSearch = useSetSearch()
  return (
    <AdminUsersPage
      search={search}
      onSearchChange={(next) => {
        void setSearch(next, { replace: true })
      }}
    />
  )
}
