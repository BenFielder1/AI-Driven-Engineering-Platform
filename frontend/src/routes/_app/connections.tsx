import { createFileRoute } from '@tanstack/react-router'

import { ConnectionsPage } from '../../features/connections/connections-page'

export const Route = createFileRoute('/_app/connections')({
  component: ConnectionsPage,
})
