'use client'

import { LoginPage } from '../../../features/auth/login-page'
import { parseLoginSearch } from '../../../features/auth/auth-search'
import { useSearch } from '../../../lib/navigation'

/**
 * /login (FR-1, FR-2, FR-131): `?returnUrl=`, `?error=GITHUB_AUTH_FAILED&reason=` from the GitHub OAuth flow,
 * `?error=GIT_AUTH_FAILED&reason=&provider=gitlab` from GitLab's, and `?link=gitlab` for a GitLab sign-in to link.
 */
export default function LoginRoute() {
  const { returnUrl, error, reason, provider, link } = useSearch(parseLoginSearch)
  return (
    <LoginPage
      returnUrl={returnUrl}
      githubError={error === 'GITHUB_AUTH_FAILED' ? { reason } : undefined}
      gitHostError={error === 'GIT_AUTH_FAILED' ? { reason, provider } : undefined}
      linkGitLab={link === 'gitlab'}
    />
  )
}
