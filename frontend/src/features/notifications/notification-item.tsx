import { useRouter } from '@tanstack/react-router'
import type { MouseEvent } from 'react'

import { Badge } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { safeAppPath } from '../../lib/return-url'
import { notificationTypeLabels } from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import { useMarkNotificationRead, type Notification } from './notifications-api'

/**
 * One notification (FR-47). Unread items carry an "Unread" text badge and a bold title (never colour alone).
 * Opening it marks it read and navigates to its link; unread items also offer "Mark as read" without leaving.
 */
export function NotificationItem({
  notification: n,
  onOpen,
  showMarkRead = true,
  onMarkReadError,
}: {
  notification: Notification
  /** Called after the user opens the notification (e.g. to close the bell's dropdown). */
  onOpen?: () => void
  showMarkRead?: boolean
  onMarkReadError?: (error: unknown) => void
}) {
  const router = useRouter()
  const markRead = useMarkNotificationRead()
  const unread = n.readAt === null
  const href = safeAppPath(n.link)
  const titleId = `notification-${n.id}`

  function markAsRead() {
    if (unread) {
      markRead.mutate(n.id, { onError: onMarkReadError })
    }
  }

  function open(event: MouseEvent<HTMLAnchorElement>) {
    markAsRead()
    if (!href || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || event.button !== 0) {
      return
    }
    event.preventDefault()
    onOpen?.()
    router.history.push(href)
  }

  return (
    <li aria-labelledby={titleId} className={`flex gap-3 px-3 py-3 ${unread ? 'bg-surface' : ''}`}>
      <span
        aria-hidden="true"
        className={`mt-1.5 size-2 shrink-0 rounded-full ${unread ? 'bg-primary' : 'bg-transparent'}`}
      />
      <div className="flex min-w-0 flex-1 flex-col gap-1 text-sm">
        <div className="flex flex-wrap items-center gap-2">
          {href ? (
            <a
              id={titleId}
              href={href}
              onClick={open}
              className={`break-words text-link underline ${unread ? 'font-semibold' : ''}`}
            >
              {n.title}
            </a>
          ) : (
            <span id={titleId} className={`break-words ${unread ? 'font-semibold' : ''}`}>
              {n.title}
            </span>
          )}
          {unread && <Badge tone="info">Unread</Badge>}
        </div>
        {n.body && <p className="break-words">{n.body}</p>}
        <p className="text-xs text-muted">
          {notificationTypeLabels[n.type]}
          {n.projectName && <> · {n.projectName}</>} · <RelativeTime iso={n.createdAt} />
        </p>
      </div>
      {showMarkRead && unread && (
        <Button
          variant="ghost"
          className="shrink-0 self-start"
          aria-label={`Mark "${n.title}" as read`}
          pending={markRead.isPending}
          pendingLabel="Marking…"
          onClick={markAsRead}
        >
          Mark as read
        </Button>
      )}
    </li>
  )
}
