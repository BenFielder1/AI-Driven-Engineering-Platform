import { createFileRoute } from '@tanstack/react-router'

import { AdminDeadLettersPage } from '../../../features/admin/admin-dead-letters-page'
import { deadLettersSearchSchema } from '../../../features/admin/admin-search'

export const Route = createFileRoute('/_app/admin/dead-letters')({
  validateSearch: (search: Record<string, unknown>) => deadLettersSearchSchema.parse(search),
  component: AdminDeadLettersRoute,
})

/** Filter and page changes replace the history entry. */
function AdminDeadLettersRoute() {
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <AdminDeadLettersPage
      search={search}
      onSearchChange={(next) => {
        void navigate({ search: next, replace: true })
      }}
    />
  )
}
