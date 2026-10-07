import { createFileRoute } from '@tanstack/react-router'

import { AccountPage } from '../../features/account/account-page'

export interface AccountSearch {
  error?: string
  reason?: string
}

const str = (v: unknown) => (typeof v === 'string' && v ? v : undefined)

export const Route = createFileRoute('/_app/account')({
  validateSearch: (search: Record<string, unknown>): AccountSearch => ({
    error: str(search.error),
    reason: str(search.reason),
  }),
  component: AccountRoute,
})

function AccountRoute() {
  const { error, reason } = Route.useSearch()
  return <AccountPage githubError={error === 'GITHUB_AUTH_FAILED' ? { reason } : undefined} />
}
