import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router'
import { render, type RenderOptions } from '@testing-library/react'
import type { ReactElement } from 'react'

import { createAppRouter } from '../router'

export function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } },
  })
}

/** Renders with a fresh, retry-free QueryClient per test. */
export function renderWithProviders(ui: ReactElement, options?: RenderOptions) {
  const queryClient = createTestQueryClient()
  return {
    queryClient,
    ...render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>, options),
  }
}

/** Renders the whole app (real route tree, guards and shell) at `path`, with in-memory history. */
export function renderRoute(path: string) {
  const queryClient = createTestQueryClient()
  const router = createAppRouter(queryClient, createMemoryHistory({ initialEntries: [path] }))
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return { router, queryClient, ...utils }
}

/** Current path + search of a test router, e.g. `/login?returnUrl=%2Faccount`. */
export function currentHref(router: ReturnType<typeof createAppRouter>): string {
  return router.state.location.href
}
