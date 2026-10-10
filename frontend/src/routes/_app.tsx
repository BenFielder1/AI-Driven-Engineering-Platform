import { createFileRoute, Outlet, redirect } from '@tanstack/react-router'

import { meQueryOptions, useCurrentUser } from '../features/auth/auth-api'
import { AppShell } from '../features/shell/app-shell'

/** Authenticated layout: signed-out users go to /login?returnUrl=<where they were going>. */
export const Route = createFileRoute('/_app')({
  beforeLoad: async ({ context, location }) => {
    const user = await context.queryClient.query({ ...meQueryOptions, staleTime: 'static' })
    if (!user) {
      // eslint-disable-next-line @typescript-eslint/only-throw-error -- TanStack Router redirects are thrown
      throw redirect({ to: '/login', search: { returnUrl: location.href } })
    }
    return { user }
  },
  component: AppLayout,
})

function AppLayout() {
  const { user: initialUser } = Route.useRouteContext()
  // Live value from the cache (e.g. after a profile change); falls back to the guard's value.
  const user = useCurrentUser() ?? initialUser
  return (
    <AppShell user={user}>
      <Outlet />
    </AppShell>
  )
}
