'use client'

import type { ReactNode } from 'react'

import { AdminLayout } from '../../../features/admin/admin-layout'

/** /admin/* (FR-51, FR-3): non-admins get the forbidden view and no admin request is made. */
export default function AdminRouteLayout({ children }: { children: ReactNode }) {
  return <AdminLayout>{children}</AdminLayout>
}
