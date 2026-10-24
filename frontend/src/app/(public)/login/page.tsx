'use client'

import { LoginPage } from '../../../features/auth/login-page'
import { parseLoginSearch } from '../../../features/auth/auth-search'
import { useSearch } from '../../../lib/navigation'

/** /login (FR-1, FR-2): `?returnUrl=`, and `?error=GITHUB_AUTH_FAILED&reason=` from the OAuth flow. */
export default function LoginRoute() {
  const { returnUrl, error, reason } = useSearch(parseLoginSearch)
  return <LoginPage returnUrl={returnUrl} githubError={error === 'GITHUB_AUTH_FAILED' ? { reason } : undefined} />
}
