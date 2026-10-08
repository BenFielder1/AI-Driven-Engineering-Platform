import { createFileRoute, useRouter } from '@tanstack/react-router'
import { useRef } from 'react'

import { RunsPage } from '../../../../features/runs/runs-page'
import { parseRunsSearch, type RunsSearch } from '../../../../features/runs/runs-search'

export const Route = createFileRoute('/_app/projects/$projectId/runs')({
  validateSearch: (search: Record<string, unknown>): RunsSearch => parseRunsSearch(search),
  component: RunsRoute,
})

/**
 * Filters and paging replace the history entry; opening a run pushes one so Back closes the drawer. Closing with the
 * Close button or Escape goes back when this page pushed the entry (so Forward/Back don't loop through stale drawer
 * entries), and otherwise (a deep link to `?run=`) replaces it.
 */
function RunsRoute() {
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  const router = useRouter()
  const pushedRun = useRef<string | null>(null)
  return (
    <RunsPage
      search={search}
      onSearchChange={(next) => {
        void navigate({ search: next, replace: true })
      }}
      onOpenRun={(runId) => {
        pushedRun.current = runId
        void navigate({ search: { ...search, run: runId } })
      }}
      onCloseRun={() => {
        if (search.run !== undefined && pushedRun.current === search.run) {
          // Kept set: Forward re-shows this pushed entry, whose previous entry is still the list.
          router.history.back()
        } else {
          void navigate({ search: { ...search, run: undefined }, replace: true })
        }
      }}
    />
  )
}
