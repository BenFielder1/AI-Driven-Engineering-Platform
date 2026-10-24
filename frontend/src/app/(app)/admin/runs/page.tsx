'use client'

import { useRef } from 'react'

import { AdminRunsPage } from '../../../../features/admin/admin-runs-page'
import { adminRunsSearchSchema } from '../../../../features/admin/admin-search'
import { useAppRouter, useSearch, useSetSearch } from '../../../../lib/navigation'

/**
 * Filters and paging replace the history entry; opening a run pushes one so Back closes the drawer (as on
 * /projects/:id/runs).
 */
export default function AdminRunsRoute() {
  const search = useSearch(adminRunsSearchSchema)
  const setSearch = useSetSearch()
  const router = useAppRouter()
  const pushedRun = useRef<string | null>(null)
  return (
    <AdminRunsPage
      search={search}
      onSearchChange={(next) => {
        void setSearch(next, { replace: true })
      }}
      onOpenRun={(runId) => {
        pushedRun.current = runId
        void setSearch({ ...search, run: runId })
      }}
      onCloseRun={() => {
        if (search.run !== undefined && pushedRun.current === search.run) {
          router.history.back()
        } else {
          void setSearch({ ...search, run: undefined }, { replace: true })
        }
      }}
    />
  )
}
