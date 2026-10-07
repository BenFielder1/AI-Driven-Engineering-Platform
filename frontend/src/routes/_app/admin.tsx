import { createFileRoute } from '@tanstack/react-router'

import { AdminPage } from '../../features/placeholders/placeholder-pages'

export const Route = createFileRoute('/_app/admin')({
  component: AdminPage,
})
