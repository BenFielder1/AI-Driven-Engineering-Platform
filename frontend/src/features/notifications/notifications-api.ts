import { infiniteQueryOptions, queryOptions, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { toApiError } from '../../lib/errors'
import { unwrap } from '../projects/projects-api'

export type Notification = Schemas['NotificationDto']
export type NotificationPage = Schemas['NotificationPageDto']
export type NotificationType = Schemas['NotificationType']
export type NotificationPreferences = Schemas['NotificationPreferencesDto']

/** Page size of /notifications; the bell shows the latest few. */
export const PAGE_SIZE = 20
export const BELL_SIZE = 5

/**
 * There is no per-user live channel (the hub is per project, FR-46), so the unread count is polled. TanStack Query
 * pauses `refetchInterval` while the tab is hidden and refetches on focus.
 */
export const UNREAD_POLL_MS = 30_000

export const notificationKeys = {
  all: ['me', 'notifications'] as const,
  unreadCount: () => [...notificationKeys.all, 'unread-count'] as const,
  lists: () => [...notificationKeys.all, 'list'] as const,
  list: (unreadOnly: boolean) => [...notificationKeys.lists(), { unreadOnly }] as const,
  latest: () => [...notificationKeys.all, 'latest'] as const,
  preferences: () => ['me', 'notification-preferences'] as const,
}

/** FR-47: the bell's unread count, polled every 30 s while the page is visible. */
export function useUnreadCount() {
  return useQuery({
    queryKey: notificationKeys.unreadCount(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/me/notifications/unread-count', { signal })).unreadCount,
    refetchInterval: UNREAD_POLL_MS,
    refetchIntervalInBackground: false,
  })
}

/** FR-47: the latest few notifications for the bell's dropdown (fetched while it's open). */
export function useLatestNotifications(enabled: boolean) {
  return useQuery({
    queryKey: notificationKeys.latest(),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/me/notifications', { params: { query: { limit: BELL_SIZE } }, signal })),
    enabled,
  })
}

export const notificationsQueryOptions = (unreadOnly: boolean) =>
  infiniteQueryOptions({
    queryKey: notificationKeys.list(unreadOnly),
    queryFn: async ({ signal, pageParam }) =>
      unwrap(
        await api.GET('/api/me/notifications', {
          params: {
            query: { limit: PAGE_SIZE, ...(unreadOnly ? { unreadOnly: true } : {}), ...(pageParam ? { before: pageParam } : {}) },
          },
          signal,
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextBefore ?? undefined,
  })

/** FR-47: /notifications, newest first; "Load more" follows `nextBefore`. */
export function useNotifications(unreadOnly: boolean) {
  return useInfiniteQuery(notificationsQueryOptions(unreadOnly))
}

function useInvalidateNotifications() {
  const queryClient = useQueryClient()
  return () => queryClient.invalidateQueries({ queryKey: notificationKeys.all })
}

/** FR-47: marks one notification read (idempotent on the server). */
export function useMarkNotificationRead() {
  const invalidate = useInvalidateNotifications()
  return useMutation({
    mutationFn: async (id: string) => {
      const { error, response } = await api.PATCH('/api/me/notifications/{id}/read', { params: { path: { id } } })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: invalidate,
  })
}

/** FR-47: marks every unread notification read. */
export function useMarkAllNotificationsRead() {
  const invalidate = useInvalidateNotifications()
  return useMutation({
    mutationFn: async () => {
      const { error, response } = await api.POST('/api/me/notifications/read-all')
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: invalidate,
  })
}

export const preferencesQueryOptions = queryOptions({
  queryKey: notificationKeys.preferences(),
  queryFn: async ({ signal }) => unwrap(await api.GET('/api/me/notification-preferences', { signal })),
})

/** §2: the user's in-app notification preferences (muted types). */
export function useNotificationPreferences() {
  return useQuery(preferencesQueryOptions)
}

export function useUpdateNotificationPreferences() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: Schemas['UpdateNotificationPreferencesRequest']) =>
      unwrap(await api.PUT('/api/me/notification-preferences', { body })),
    onSuccess: (prefs) => {
      queryClient.setQueryData(notificationKeys.preferences(), prefs)
    },
  })
}
