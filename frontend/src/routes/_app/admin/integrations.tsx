import { createFileRoute } from '@tanstack/react-router'

import { AdminIntegrationsPage } from '../../../features/admin/admin-integrations-page'

export const Route = createFileRoute('/_app/admin/integrations')({
  component: AdminIntegrationsPage,
})
