'use client'

import { AccountPage } from '../../../features/account/account-page'
import { useSearch } from '../../../lib/navigation'

interface AccountSearch {
  error?: string
  reason?: string
}

const str = (v: unknown) => (typeof v === 'string' && v ? v : undefined)

function parseAccountSearch(search: Record<string, unknown>): AccountSearch {
  return { error: str(search.error), reason: str(search.reason) }
}

/** /account: a GitHub link failure lands here with `?error=GITHUB_AUTH_FAILED&reason=…`. */
export default function AccountRoute() {
  const { error, reason } = useSearch(parseAccountSearch)
  return <AccountPage githubError={error === 'GITHUB_AUTH_FAILED' ? { reason } : undefined} />
}
