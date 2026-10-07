import { createFileRoute, Outlet, redirect } from '@tanstack/react-router'

import { meQueryOptions } from '../features/auth/auth-api'
import { PublicShell } from '../features/shell/app-shell'
import { safeReturnUrl } from '../lib/return-url'

/** Layout for /login and /register. Signed-in users are sent on (to returnUrl, or /projects). */
export const Route = createFileRoute('/_public')({
  beforeLoad: async ({ context, location }) => {
    const user = await context.queryClient.query({ ...meQueryOptions, staleTime: 'static' })
    if (!user) {
      return
    }
    const search = location.search as Record<string, unknown>
    // A GitHub *link* failure redirects a signed-in user to /login?error=…: show it on the account page instead.
    if (search.error === 'GITHUB_AUTH_FAILED') {
      // eslint-disable-next-line @typescript-eslint/only-throw-error -- TanStack Router redirects are thrown
      throw redirect({
        to: '/account',
        search: { error: 'GITHUB_AUTH_FAILED', reason: typeof search.reason === 'string' ? search.reason : undefined },
      })
    }
    // eslint-disable-next-line @typescript-eslint/only-throw-error -- TanStack Router redirects are thrown
    throw redirect({ href: safeReturnUrl(search.returnUrl) })
  },
  component: PublicLayout,
})

function PublicLayout() {
  return (
    <PublicShell>
      <Outlet />
    </PublicShell>
  )
}
