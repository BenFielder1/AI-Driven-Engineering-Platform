import { useState } from 'react'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { EmptyState, ErrorState, LoadingState, PageHeader } from '../../components/ui/states'
import { errorMessage } from '../../lib/errors'
import { formatNumber } from '../projects/labels'
import { NotificationItem } from './notification-item'
import { useMarkAllNotificationsRead, useNotifications } from './notifications-api'

/**
 * /notifications (FR-47, SPEC §7): the user's notifications, newest first, with unread state, mark one or all as
 * read, an "Unread only" filter and "Load more".
 */
export function NotificationsPage({
  unreadOnly,
  onUnreadOnlyChange,
}: {
  unreadOnly: boolean
  onUnreadOnlyChange: (unreadOnly: boolean) => void
}) {
  const list = useNotifications(unreadOnly)
  const markAll = useMarkAllNotificationsRead()
  const [actionError, setActionError] = useState<unknown>(null)
  const [markedAll, setMarkedAll] = useState(false)

  const pages = list.data?.pages ?? []
  const items = pages.flatMap((p) => p.items)
  const unreadCount = pages[0]?.unreadCount ?? 0

  return (
    <div className="flex flex-col gap-4">
      <PageHeader title="Notifications">
        <Button
          variant="secondary"
          disabled={!list.isSuccess || unreadCount === 0}
          pending={markAll.isPending}
          pendingLabel="Marking…"
          onClick={() => {
            setActionError(null)
            setMarkedAll(false)
            markAll.mutate(undefined, {
              onSuccess: () => {
                setMarkedAll(true)
              },
              onError: setActionError,
            })
          }}
        >
          Mark all as read
        </Button>
      </PageHeader>

      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-2">
          <input
            id="notifications-unread-only"
            type="checkbox"
            className="size-4"
            checked={unreadOnly}
            onChange={(e) => {
              setMarkedAll(false)
              onUnreadOnlyChange(e.currentTarget.checked)
            }}
          />
          <label htmlFor="notifications-unread-only" className="text-sm font-medium">
            Unread only
          </label>
        </div>
        {list.isSuccess && (
          <p className="text-sm text-muted">
            {unreadCount === 0 ? 'No unread notifications' : `${formatNumber(unreadCount)} unread`}
          </p>
        )}
      </div>

      <div role="status" aria-live="polite">
        {markedAll && <Alert tone="success">All notifications are marked as read.</Alert>}
        {actionError !== null && (
          <Alert tone="error" title="We couldn't update your notifications">
            <p>{errorMessage(actionError, 'markNotificationRead')}</p>
          </Alert>
        )}
      </div>

      {list.isPending && <LoadingState label="Loading notifications…" />}
      {list.isError && (
        <ErrorState error={list.error} onRetry={() => void list.refetch()} title="Notifications couldn't load" />
      )}
      {list.isSuccess && items.length === 0 && (
        <EmptyState title={unreadOnly ? 'No unread notifications' : 'No notifications yet'}>
          {unreadOnly
            ? "You're all caught up."
            : 'Gate decisions, finished reviews and releases, failures and rollbacks on your projects appear here.'}
        </EmptyState>
      )}
      {items.length > 0 && (
        <ul aria-label="Notifications" className="divide-y divide-border overflow-hidden rounded-lg border border-border">
          {items.map((n) => (
            <NotificationItem key={n.id} notification={n} onMarkReadError={setActionError} />
          ))}
        </ul>
      )}
      {list.hasNextPage && (
        <div>
          <Button
            variant="secondary"
            pending={list.isFetchingNextPage}
            pendingLabel="Loading more…"
            onClick={() => void list.fetchNextPage()}
          >
            Load more
          </Button>
        </div>
      )}
      {list.isFetchNextPageError && (
        <Alert tone="error" role="alert">
          {errorMessage(list.error)}
        </Alert>
      )}
    </div>
  )
}
