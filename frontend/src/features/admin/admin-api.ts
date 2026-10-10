import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import type { paths } from '../../api/generated/schema'
import { authKeys } from '../auth/auth-api'
import { unwrap } from '../projects/projects-api'

export type AdminEvent = Schemas['AdminEventDto']
export type AdminRun = Schemas['AdminAgentRunDto']
export type DeadLetter = Schemas['DeadLetterDto']
export type AdminUser = Schemas['AdminUserDto']
export type SystemRole = Schemas['SystemRole']
export type Job = Schemas['JobDto']

export type AdminEventsQuery = NonNullable<paths['/api/admin/events']['get']['parameters']['query']>
export type AdminRunsQuery = NonNullable<paths['/api/admin/runs']['get']['parameters']['query']>
export type DeadLettersQuery = NonNullable<paths['/api/admin/dead-letters']['get']['parameters']['query']>
export type AdminUsersQuery = NonNullable<paths['/api/admin/users']['get']['parameters']['query']>

/** Rows per page on every admin list (the API default). */
export const ADMIN_PAGE_SIZE = 20

/** Query keys for the admin pages (FR-51, FR-3). */
export const adminKeys = {
  all: ['admin'] as const,
  events: (q: AdminEventsQuery) => [...adminKeys.all, 'events', q] as const,
  runs: (q: AdminRunsQuery) => [...adminKeys.all, 'runs', q] as const,
  deadLetters: () => [...adminKeys.all, 'dead-letters'] as const,
  deadLetterList: (q: DeadLettersQuery) => [...adminKeys.deadLetters(), q] as const,
  users: () => [...adminKeys.all, 'users'] as const,
  userList: (q: AdminUsersQuery) => [...adminKeys.users(), q] as const,
}

/** FR-51: audit events of every project, newest first. */
export function useAdminEvents(query: AdminEventsQuery, enabled = true) {
  return useQuery({
    queryKey: adminKeys.events(query),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/admin/events', { params: { query }, signal })),
    placeholderData: keepPreviousData,
    enabled,
  })
}

/** FR-51, FR-23: agent runs of every project, newest first. */
export function useAdminRuns(query: AdminRunsQuery) {
  return useQuery({
    queryKey: adminKeys.runs(query),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/admin/runs', { params: { query }, signal })),
    placeholderData: keepPreviousData,
  })
}

/** FR-51: dead-lettered jobs, most recently dead-lettered first. */
export function useDeadLetters(query: DeadLettersQuery) {
  return useQuery({
    queryKey: adminKeys.deadLetterList(query),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/admin/dead-letters', { params: { query }, signal })),
    placeholderData: keepPreviousData,
  })
}

/** FR-51: re-enqueues a dead letter's job; refreshes the dead-letter lists and the event log. */
export function useRetryDeadLetter() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (jobId: string) =>
      unwrap(await api.POST('/api/admin/dead-letters/{jobId}/retry', { params: { path: { jobId } } })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: adminKeys.all }),
  })
}

/** FR-3: every user, newest first, optionally searched by email or name. */
export function useAdminUsers(query: AdminUsersQuery) {
  return useQuery({
    queryKey: adminKeys.userList(query),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/admin/users', { params: { query }, signal })),
    placeholderData: keepPreviousData,
  })
}

/** FR-3: changes a user's system role. Changing your own role refreshes `me` (the admin area may close). */
export function useChangeSystemRole() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, systemRole }: { id: string; systemRole: SystemRole }) =>
      unwrap(await api.PATCH('/api/admin/users/{id}/role', { params: { path: { id } }, body: { systemRole } })),
    onSettled: async () => {
      await queryClient.invalidateQueries({ queryKey: adminKeys.users() })
      await queryClient.invalidateQueries({ queryKey: authKeys.me() })
    },
  })
}
