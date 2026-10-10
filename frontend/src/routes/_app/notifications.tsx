import { createFileRoute } from '@tanstack/react-router'

import { NotificationsPage } from '../../features/notifications/notifications-page'

export interface NotificationsSearch {
  unread?: boolean
}

export const Route = createFileRoute('/_app/notifications')({
  validateSearch: (search: Record<string, unknown>): NotificationsSearch => ({
    unread: search.unread === true || search.unread === 'true' ? true : undefined,
  }),
  component: NotificationsRoute,
})

function NotificationsRoute() {
  const { unread } = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <NotificationsPage
      unreadOnly={unread === true}
      onUnreadOnlyChange={(next) => {
        void navigate({ search: next ? { unread: true } : {}, replace: true })
      }}
    />
  )
}
