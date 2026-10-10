import { createFileRoute } from '@tanstack/react-router'

import { AdminOverviewPage } from '../../../features/admin/admin-overview-page'

export const Route = createFileRoute('/_app/admin/')({
  component: AdminOverviewPage,
})
