import { createFileRoute } from '@tanstack/react-router'

import { AdminUsersPage } from '../../../features/admin/admin-users-page'
import { adminUsersSearchSchema } from '../../../features/admin/admin-search'

export const Route = createFileRoute('/_app/admin/users')({
  validateSearch: (search: Record<string, unknown>) => adminUsersSearchSchema.parse(search),
  component: AdminUsersRoute,
})

/** Search and page changes replace the history entry. */
function AdminUsersRoute() {
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <AdminUsersPage
      search={search}
      onSearchChange={(next) => {
        void navigate({ search: next, replace: true })
      }}
    />
  )
}
