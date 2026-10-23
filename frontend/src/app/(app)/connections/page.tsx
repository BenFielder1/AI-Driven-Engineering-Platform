'use client'

import { ConnectionsPage } from '../../../features/connections/connections-page'
import { parseConnectionsSearch } from '../../../features/connections/connections-search'
import { useSearch } from '../../../lib/navigation'

/**
 * FR-81: Connect GitHub comes back here (a failure carries `?error=GITHUB_AUTH_FAILED&reason=…`), and the GitHub App's
 * setup URL (via /account/connections) with `?github=installed`.
 */
export default function ConnectionsRoute() {
  const { github, error, reason } = useSearch(parseConnectionsSearch)
  return (
    <ConnectionsPage
      githubError={error === 'GITHUB_AUTH_FAILED' ? { reason } : undefined}
      githubInstalled={github === 'installed'}
    />
  )
}
