import { createFileRoute, redirect } from '@tanstack/react-router'

/** `/account/connections` → /connections (SPEC §7.2 alias). */
export const Route = createFileRoute('/_app/account_/connections')({
  beforeLoad: () => {
    // eslint-disable-next-line @typescript-eslint/only-throw-error -- TanStack Router redirects are thrown
    throw redirect({ to: '/connections', replace: true })
  },
})
