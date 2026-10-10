import { createFileRoute, useRouter } from '@tanstack/react-router'
import { useRef } from 'react'

import { AdminRunsPage } from '../../../features/admin/admin-runs-page'
import { adminRunsSearchSchema } from '../../../features/admin/admin-search'

export const Route = createFileRoute('/_app/admin/runs')({
  validateSearch: (search: Record<string, unknown>) => adminRunsSearchSchema.parse(search),
  component: AdminRunsRoute,
})

/**
 * Filters and paging replace the history entry; opening a run pushes one so Back closes the drawer (as on
 * /projects/:id/runs).
 */
function AdminRunsRoute() {
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  const router = useRouter()
  const pushedRun = useRef<string | null>(null)
  return (
    <AdminRunsPage
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
          router.history.back()
        } else {
          void navigate({ search: { ...search, run: undefined }, replace: true })
        }
      }}
    />
  )
}
