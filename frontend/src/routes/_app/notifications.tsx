import { createFileRoute } from '@tanstack/react-router'

import { NotificationsPage } from '../../features/placeholders/placeholder-pages'

export const Route = createFileRoute('/_app/notifications')({
  component: NotificationsPage,
})
