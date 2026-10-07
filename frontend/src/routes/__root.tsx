import type { QueryClient } from '@tanstack/react-query'
import { createRootRouteWithContext, Link, Outlet, type ErrorComponentProps } from '@tanstack/react-router'

import { buttonClasses } from '../components/ui/button-classes'
import { ErrorState } from '../components/ui/states'

export interface RouterContext {
  queryClient: QueryClient
}

export const Route = createRootRouteWithContext<RouterContext>()({
  component: Outlet,
  errorComponent: RootError,
  notFoundComponent: NotFound,
})

function RootError({ error, reset }: ErrorComponentProps) {
  return (
    <main id="main" className="mx-auto max-w-xl px-4 py-12">
      <h1 className="mb-4 text-2xl font-semibold tracking-tight">Something went wrong</h1>
      <ErrorState error={error} onRetry={reset} title="The page couldn't load" />
    </main>
  )
}

function NotFound() {
  return (
    <main id="main" className="mx-auto max-w-xl px-4 py-12">
      <h1 className="text-2xl font-semibold tracking-tight">Page not found</h1>
      <p className="mt-2 text-muted">The page you&apos;re looking for doesn&apos;t exist.</p>
      <Link to="/projects" className={buttonClasses('secondary', 'mt-4')}>
        Go to projects
      </Link>
    </main>
  )
}
