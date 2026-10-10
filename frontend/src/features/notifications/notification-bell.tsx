import { Link } from '@tanstack/react-router'
import { useEffect, useId, useRef, useState } from 'react'

import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage } from '../../lib/errors'
import { NotificationItem } from './notification-item'
import { useLatestNotifications, useMarkAllNotificationsRead, useUnreadCount } from './notifications-api'

function BellIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 24 24" className="size-5" fill="none" stroke="currentColor" strokeWidth="2">
      <path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M13.7 21a2 2 0 0 1-3.4 0" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

/** The badge caps at 99+; the accessible name always carries the exact count. */
const badgeText = (n: number) => (n > 99 ? '99+' : String(n))

/**
 * Header bell (FR-47): the unread count (polled), and a disclosure with the latest notifications, "Mark all as read"
 * and "View all". Accessible name: "Notifications, N unread".
 */
export function NotificationBell() {
  const [open, setOpen] = useState(false)
  const panelId = useId()
  const headingId = useId()
  const containerRef = useRef<HTMLDivElement>(null)
  const buttonRef = useRef<HTMLButtonElement>(null)
  const unread = useUnreadCount()
  const latest = useLatestNotifications(open)
  const markAll = useMarkAllNotificationsRead()
  const [actionError, setActionError] = useState<unknown>(null)
  const count = unread.data ?? 0

  useEffect(() => {
    if (!open) {
      return undefined
    }
    function onPointerDown(event: PointerEvent) {
      if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
        setOpen(false)
      }
    }
    document.addEventListener('pointerdown', onPointerDown)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown)
    }
  }, [open])

  function close() {
    setOpen(false)
  }

  return (
    <div
      ref={containerRef}
      className="relative"
      onKeyDown={(e) => {
        if (e.key === 'Escape' && open) {
          setOpen(false)
          buttonRef.current?.focus()
        }
      }}
    >
      <button
        ref={buttonRef}
        type="button"
        aria-expanded={open}
        aria-controls={panelId}
        aria-label={`Notifications, ${String(count)} unread`}
        onClick={() => {
          setActionError(null)
          setOpen((v) => !v)
        }}
        className="relative flex min-h-9 min-w-9 items-center justify-center rounded-md px-2 text-fg hover:bg-surface"
      >
        <BellIcon />
        {count > 0 && (
          <span
            aria-hidden="true"
            className="absolute -right-0.5 -top-0.5 min-w-5 rounded-full border border-canvas bg-danger px-1 text-center text-xs font-semibold leading-4 text-on-danger"
          >
            {badgeText(count)}
          </span>
        )}
      </button>

      {open && (
        <div
          id={panelId}
          role="region"
          aria-labelledby={headingId}
          className="absolute right-0 z-40 mt-2 w-96 max-w-[calc(100vw-2rem)] rounded-lg border border-border bg-surface-raised shadow-lg"
        >
          <div className="flex items-center justify-between gap-2 border-b border-border px-3 py-2">
            <h2 id={headingId} className="font-semibold">
              Notifications
            </h2>
            {count > 0 && (
              <Button
                variant="ghost"
                className="min-h-8 px-2"
                pending={markAll.isPending}
                pendingLabel="Marking…"
                onClick={() => {
                  setActionError(null)
                  markAll.mutate(undefined, { onError: setActionError })
                }}
              >
                Mark all as read
              </Button>
            )}
          </div>
          <div role="status" aria-live="polite" className="px-3">
            {actionError !== null && <p className="py-2 text-sm text-danger">{errorMessage(actionError, 'markNotificationRead')}</p>}
          </div>
          <div className="max-h-96 overflow-y-auto">
            {latest.isPending && (
              <div className="px-3">
                <LoadingState label="Loading notifications…" />
              </div>
            )}
            {latest.isError && (
              <div className="p-3">
                <ErrorState error={latest.error} onRetry={() => void latest.refetch()} title="Notifications couldn't load" />
              </div>
            )}
            {latest.isSuccess && latest.data.items.length === 0 && (
              <div className="p-3">
                <EmptyState title="You're all caught up">New notifications appear here.</EmptyState>
              </div>
            )}
            {latest.isSuccess && latest.data.items.length > 0 && (
              <ul aria-label="Latest notifications" className="divide-y divide-border">
                {latest.data.items.map((n) => (
                  <NotificationItem key={n.id} notification={n} onOpen={close} showMarkRead={false} onMarkReadError={setActionError} />
                ))}
              </ul>
            )}
          </div>
          <div className="border-t border-border p-2">
            <Link to="/notifications" onClick={close} className={buttonClasses('secondary', 'w-full')}>
              View all notifications
            </Link>
          </div>
        </div>
      )}
    </div>
  )
}
