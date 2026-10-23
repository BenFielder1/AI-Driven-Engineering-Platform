'use client'

import { parseConnectionsSearch } from '../../../../features/connections/connections-search'
import { Navigate, useSearch } from '../../../../lib/navigation'

/**
 * `/account/connections` → /connections (SPEC §7.2 alias). The query is kept: the GitHub App's setup URL lands here
 * with `?github=installed`, and Connect GitHub failures with `?error=GITHUB_AUTH_FAILED&reason=…` (FR-81).
 */
export default function AccountConnectionsRoute() {
  const search = useSearch(parseConnectionsSearch)
  return <Navigate to="/connections" search={search} replace />
}
