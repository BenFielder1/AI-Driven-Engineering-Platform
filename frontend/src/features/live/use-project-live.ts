import { useQueryClient } from '@tanstack/react-query'
import { useContext, useEffect, useState } from 'react'

import { notificationKeys } from '../notifications/notifications-api'
import { projectKeys } from '../projects/projects-api'
import { LiveConnectionContext } from './live-connection'
import { invalidateForEvent } from './live-invalidation'

/**
 * `connecting` until joined; `live` while receiving events; `reconnecting` while SignalR retries; `offline` when the
 * connection or the join failed (the page still works, it just doesn't refresh by itself).
 */
export type LiveStatus = 'connecting' | 'live' | 'reconnecting' | 'offline'

/**
 * FR-47: the event types that raise a notification (mirrors `NotificationRules.ForEvent` in Aide.Domain). Other events
 * never change the bell, so they don't refresh it.
 */
export const NOTIFYING_EVENT_TYPES: ReadonlySet<string> = new Set([
  'GATE_CREATED',
  'GATE_OVERDUE',
  'ARTIFACT_CREATED',
  'STEP_BLOCKED',
  'RUN_FAILED',
  'BUDGET_EXCEEDED',
  'REVIEW_COMPLETED',
  'RELEASE_COMPLETED',
  'RELEASE_FAILED',
  'ROLLBACK_COMPLETED',
])

/** Trailing debounce for the notification refresh, so a burst of events (a draft, an execution run) refetches once. */
export const NOTIFICATION_REFRESH_DEBOUNCE_MS = 500

/**
 * FR-46: while a project page is open, receive its events over the projects hub and refresh the affected queries.
 * Joins the project's group on connect and after every automatic reconnect (refetching what may have been missed),
 * leaves it and closes the connection on unmount.
 */
export function useProjectLive(projectId: string): LiveStatus {
  const factory = useContext(LiveConnectionContext)
  const queryClient = useQueryClient()
  const [status, setStatus] = useState<LiveStatus>('connecting')

  useEffect(() => {
    let disposed = false
    let notificationRefresh: ReturnType<typeof setTimeout> | undefined
    const connection = factory()
    const update = (next: LiveStatus) => {
      if (!disposed) {
        setStatus(next)
      }
    }
    const join = async () => {
      try {
        await connection.invoke('Join', projectId)
        update('live')
      } catch {
        // FORBIDDEN (no longer a member) or the hub is unavailable: stay usable without live updates.
        update('offline')
      }
    }

    connection.onProjectEvent((message) => {
      if (!disposed && message.projectId === projectId) {
        void invalidateForEvent(queryClient, projectId, message.category)
        // FR-47: an event that may notify someone refreshes the bell's unread count and the notification lists soon rather
        // than at the next 30 s poll (which stays as the fallback). Debounced, and without cancelling an in-flight fetch,
        // so a burst of events doesn't abort and restart the same requests over and over.
        if (NOTIFYING_EVENT_TYPES.has(message.type)) {
          clearTimeout(notificationRefresh)
          notificationRefresh = setTimeout(() => {
            notificationRefresh = undefined
            if (!disposed) {
              void queryClient.invalidateQueries({ queryKey: notificationKeys.all }, { cancelRefetch: false })
            }
          }, NOTIFICATION_REFRESH_DEBOUNCE_MS)
        }
      }
    })
    connection.onReconnecting(() => {
      update('reconnecting')
    })
    connection.onReconnected(() => {
      if (disposed) {
        return
      }
      // Events sent while we were disconnected are lost: refresh everything about the project, then rejoin.
      void queryClient.invalidateQueries({ queryKey: projectKeys.detail(projectId) })
      void join()
    })
    connection.onClose(() => {
      update('offline')
    })

    void connection
      .start()
      .then(() => (disposed ? undefined : join()))
      .catch(() => {
        update('offline')
      })

    return () => {
      disposed = true
      clearTimeout(notificationRefresh)
      void connection
        .invoke('Leave', projectId)
        .catch(() => undefined)
        .finally(() => void connection.stop().catch(() => undefined))
    }
  }, [factory, projectId, queryClient])

  return status
}
