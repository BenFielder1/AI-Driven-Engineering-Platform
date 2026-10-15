import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router'
import { render, type RenderOptions } from '@testing-library/react'
import type { ReactElement } from 'react'

import { Toaster } from '../components/ui/sonner'
import { LiveConnectionContext } from '../features/live/live-connection'
import { createAppRouter } from '../router'
import { FakeLiveHub } from './fake-live'

export function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } },
  })
}

/**
 * Renders with a fresh, retry-free QueryClient per test, a fake live hub (no real SignalR connection) and the toast
 * region (async results are toasts, SPEC 7.1), like the root layout.
 */
export function renderWithProviders(ui: ReactElement, options?: RenderOptions) {
  const queryClient = createTestQueryClient()
  const live = new FakeLiveHub()
  return {
    queryClient,
    live,
    ...render(
      <QueryClientProvider client={queryClient}>
        <LiveConnectionContext value={live.factory}>
          {ui}
          <Toaster />
        </LiveConnectionContext>
      </QueryClientProvider>,
      options,
    ),
  }
}

/** Renders the whole app (real route tree, guards and shell) at `path`, with in-memory history and a fake live hub. */
export function renderRoute(path: string) {
  const queryClient = createTestQueryClient()
  const router = createAppRouter(queryClient, createMemoryHistory({ initialEntries: [path] }))
  const live = new FakeLiveHub()
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <LiveConnectionContext value={live.factory}>
        <RouterProvider router={router} />
      </LiveConnectionContext>
    </QueryClientProvider>,
  )
  return { router, queryClient, live, ...utils }
}

/** Current path + search of a test router, e.g. `/login?returnUrl=%2Faccount`. */
export function currentHref(router: ReturnType<typeof createAppRouter>): string {
  return router.state.location.href
}
