import { createFileRoute, redirect } from '@tanstack/react-router'

import type { ConnectionsSearch } from './connections'

const str = (v: unknown) => (typeof v === 'string' && v ? v : undefined)

/**
 * `/account/connections` → /connections (SPEC §7.2 alias). The query is kept: the GitHub App's setup URL lands here
 * with `?github=installed`, and Connect GitHub failures with `?error=GITHUB_AUTH_FAILED&reason=…` (FR-81).
 */
export const Route = createFileRoute('/_app/account_/connections')({
  validateSearch: (search: Record<string, unknown>): ConnectionsSearch => ({
    github: search.github === 'installed' ? 'installed' : undefined,
    error: str(search.error),
    reason: str(search.reason),
  }),
  beforeLoad: ({ search }) => {
    // eslint-disable-next-line @typescript-eslint/only-throw-error -- TanStack Router redirects are thrown
    throw redirect({ to: '/connections', search, replace: true })
  },
})
