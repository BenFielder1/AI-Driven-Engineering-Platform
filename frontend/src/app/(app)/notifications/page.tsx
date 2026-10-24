'use client'

import { NotificationsPage } from '../../../features/notifications/notifications-page'
import { useSearch, useSetSearch } from '../../../lib/navigation'

interface NotificationsSearch {
  unread?: boolean
}

function parseNotificationsSearch(search: Record<string, unknown>): NotificationsSearch {
  return { unread: search.unread === true || search.unread === 'true' ? true : undefined }
}

export default function NotificationsRoute() {
  const { unread } = useSearch(parseNotificationsSearch)
  const setSearch = useSetSearch()
  return (
    <NotificationsPage
      unreadOnly={unread === true}
      onUnreadOnlyChange={(next) => {
        void setSearch(next ? { unread: true } : {}, { replace: true })
      }}
    />
  )
}
