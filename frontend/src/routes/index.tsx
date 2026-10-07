import { createFileRoute, redirect } from '@tanstack/react-router'

/** `/` → /projects (the guard on /projects sends signed-out users to /login). */
export const Route = createFileRoute('/')({
  beforeLoad: () => {
    // eslint-disable-next-line @typescript-eslint/only-throw-error -- TanStack Router redirects are thrown
    throw redirect({ to: '/projects' })
  },
})
