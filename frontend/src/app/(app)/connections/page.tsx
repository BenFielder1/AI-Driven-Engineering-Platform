'use client'

import { ConnectionsPage } from '../../../features/connections/connections-page'
import { parseConnectionsSearch } from '../../../features/connections/connections-search'
import { useSearch } from '../../../lib/navigation'

/**
 * FR-81: Connect GitHub comes back here (a failure carries `?error=GITHUB_AUTH_FAILED&reason=…`), and the GitHub App's
 * setup URL (via /account/connections) with `?github=installed`. FR-131: Connect GitLab comes back with
 * `?gitlab=connected`, or `?error=GIT_AUTH_FAILED&reason=…&provider=gitlab`. FR-134: Connect Bitbucket likewise with
 * `?bitbucket=connected` or `provider=bitbucket`.
 */
export default function ConnectionsRoute() {
  const { github, gitlab, bitbucket, error, reason, provider } = useSearch(parseConnectionsSearch)
  return (
    <ConnectionsPage
      githubError={error === 'GITHUB_AUTH_FAILED' ? { reason } : undefined}
      githubInstalled={github === 'installed'}
      gitlabError={error === 'GIT_AUTH_FAILED' && provider === 'gitlab' ? { reason } : undefined}
      // A failed OAuth connect comes back to the returnUrl with the error appended, so `connected` only counts without one.
      gitlabConnected={gitlab === 'connected' && !error}
      bitbucketError={error === 'GIT_AUTH_FAILED' && provider === 'bitbucket' ? { reason } : undefined}
      bitbucketConnected={bitbucket === 'connected' && !error}
    />
  )
}
