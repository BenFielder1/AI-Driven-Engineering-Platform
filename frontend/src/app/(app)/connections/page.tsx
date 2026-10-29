'use client'

import { ConnectionsPage } from '../../../features/connections/connections-page'
import { parseConnectionsSearch } from '../../../features/connections/connections-search'
import { useSearch } from '../../../lib/navigation'

/**
 * FR-81: Connect GitHub comes back here (a failure carries `?error=GITHUB_AUTH_FAILED&reason=…`), and the GitHub App's
 * setup URL (via /account/connections) with `?github=installed`. FR-131: Connect GitLab comes back with
 * `?gitlab=connected`, or `?error=GIT_AUTH_FAILED&reason=…&provider=gitlab`.
 */
export default function ConnectionsRoute() {
  const { github, gitlab, error, reason, provider } = useSearch(parseConnectionsSearch)
  return (
    <ConnectionsPage
      githubError={error === 'GITHUB_AUTH_FAILED' ? { reason } : undefined}
      githubInstalled={github === 'installed'}
      gitlabError={error === 'GIT_AUTH_FAILED' && provider === 'gitlab' ? { reason } : undefined}
      gitlabConnected={gitlab === 'connected'}
    />
  )
}
