import { createFileRoute } from '@tanstack/react-router'

import { ConnectionsPage } from '../../features/connections/connections-page'

/**
 * FR-81: Connect GitHub comes back here (a failure carries `?error=GITHUB_AUTH_FAILED&reason=…`), and the GitHub App's
 * setup URL (via /account/connections) with `?github=installed`.
 */
export interface ConnectionsSearch {
  github?: 'installed'
  error?: string
  reason?: string
}

const str = (v: unknown) => (typeof v === 'string' && v ? v : undefined)

export const Route = createFileRoute('/_app/connections')({
  validateSearch: (search: Record<string, unknown>): ConnectionsSearch => ({
    github: search.github === 'installed' ? 'installed' : undefined,
    error: str(search.error),
    reason: str(search.reason),
  }),
  component: ConnectionsRoute,
})

function ConnectionsRoute() {
  const { github, error, reason } = Route.useSearch()
  return (
    <ConnectionsPage
      githubError={error === 'GITHUB_AUTH_FAILED' ? { reason } : undefined}
      githubInstalled={github === 'installed'}
    />
  )
}
