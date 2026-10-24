'use client'

import { useRef } from 'react'

import { RunsPage } from '../../../../../features/runs/runs-page'
import { parseRunsSearch } from '../../../../../features/runs/runs-search'
import { useAppRouter, useSearch, useSetSearch } from '../../../../../lib/navigation'

/**
 * /projects/:id/runs (FR-23). Filters and paging replace the history entry; opening a run pushes one so Back closes
 * the drawer. Closing with the Close button or Escape goes back when this page pushed the entry (so Forward/Back don't
 * loop through stale drawer entries), and otherwise (a deep link to `?run=`) replaces it.
 */
export default function RunsRoute() {
  const search = useSearch(parseRunsSearch)
  const setSearch = useSetSearch()
  const router = useAppRouter()
  const pushedRun = useRef<string | null>(null)
  return (
    <RunsPage
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
          // Kept set: Forward re-shows this pushed entry, whose previous entry is still the list.
          router.history.back()
        } else {
          void setSearch({ ...search, run: undefined }, { replace: true })
        }
      }}
    />
  )
}
