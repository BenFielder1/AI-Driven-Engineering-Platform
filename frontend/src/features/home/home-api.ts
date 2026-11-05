import { useQuery } from '@tanstack/react-query'

import { api } from '../../api/client'
import { useAuthProviders, type User } from '../auth/auth-api'
import { useBitbucketConnections } from '../bitbucket/bitbucket-api'
import { useLlmConnections } from '../connections/connections-api'
import { usePlatformConnections } from '../connections/platform-connections-api'
import { useGitHubConnection } from '../github/github-api'
import { useGitLabConnections } from '../gitlab/gitlab-api'
import { notificationKeys } from '../notifications/notifications-api'
import { unwrap, useProjects } from '../projects/projects-api'
import { checklist, type Checklist } from './checklist'

export const RECENT_PROJECTS = 6
export const UNREAD_NOTIFICATIONS = 5

/** FR-207 Recent projects: up to 6 of the caller's projects, last updated first (GET /api/projects). */
export function useRecentProjects() {
  return useProjects({ page: 1, limit: RECENT_PROJECTS, sortBy: 'updated', sortOrder: 'desc' })
}

/** FR-207 Unread notifications: the newest 5 unread (GET /api/me/notifications?unreadOnly=true&limit=5). */
export function useUnreadNotifications() {
  return useQuery({
    // Under notificationKeys.all, so marking one read refreshes it.
    queryKey: [...notificationKeys.all, 'home-unread'] as const,
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/me/notifications', { params: { query: { unreadOnly: true, limit: UNREAD_NOTIFICATIONS } }, signal })),
  })
}

/**
 * FR-207 / §4.30: the checklist from existing endpoints only: the user's Git connections (GitHub when the App is
 * registered, GitLab, Bitbucket; `githubLinked` for OAuth sign-in), LLM connections, platform connections and projects.
 */
export function useHomeChecklist(user: User): {
  data: Checklist | undefined
  isPending: boolean
  isError: boolean
  error: unknown
  refetch: () => void
} {
  const providers = useAuthProviders()
  const appMode = providers.data?.githubMode === 'app'
  const github = useGitHubConnection(appMode)
  const gitlab = useGitLabConnections()
  const bitbucket = useBitbucketConnections()
  const llm = useLlmConnections()
  const platforms = usePlatformConnections({ includeTrackers: true })
  const projects = useRecentProjects()

  const queries = [providers, gitlab, bitbucket, llm, platforms, projects, ...(appMode ? [github] : [])]
  const failed = queries.find((q) => q.isError)
  const refetch = () => {
    for (const q of queries) {
      if (q.isError) {
        void q.refetch()
      }
    }
  }
  if (failed) {
    return { data: undefined, isPending: false, isError: true, error: failed.error, refetch }
  }
  if (
    !providers.data ||
    !gitlab.data ||
    !bitbucket.data ||
    !llm.data ||
    !platforms.data ||
    !projects.data ||
    (appMode && !github.data)
  ) {
    return { data: undefined, isPending: true, isError: false, error: null, refetch }
  }
  return {
    data: checklist(
      { githubLinked: user.githubLinked, github: appMode ? (github.data ?? null) : null, gitlab: gitlab.data, bitbucket: bitbucket.data },
      llm.data,
      platforms.data,
      projects.data.items,
    ),
    isPending: false,
    isError: false,
    error: null,
    refetch,
  }
}
