import { createFileRoute, Outlet } from '@tanstack/react-router'

import { AdminLayout } from '../../features/admin/admin-layout'

/** /admin/* (FR-51, FR-3): non-admins get the forbidden view and no admin request is made. */
export const Route = createFileRoute('/_app/admin')({
  component: AdminRoute,
})

function AdminRoute() {
  return (
    <AdminLayout>
      <Outlet />
    </AdminLayout>
  )
}
