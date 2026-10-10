import { createFileRoute } from '@tanstack/react-router'

import { AdminEventsPage } from '../../../features/admin/admin-events-page'
import { adminEventsSearchSchema } from '../../../features/admin/admin-search'

export const Route = createFileRoute('/_app/admin/events')({
  validateSearch: (search: Record<string, unknown>) => adminEventsSearchSchema.parse(search),
  component: AdminEventsRoute,
})

/** Filter and page changes replace the history entry. */
function AdminEventsRoute() {
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <AdminEventsPage
      search={search}
      onSearchChange={(next) => {
        void navigate({ search: next, replace: true })
      }}
    />
  )
}
