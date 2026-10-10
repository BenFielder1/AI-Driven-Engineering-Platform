import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query'

import { ApiError } from './errors'

export interface QueryClientHooks {
  /** Called when any request reports 401 UNAUTHENTICATED (the session ended): sign the UI out. */
  onUnauthenticated?: () => void
}

function isSessionEnded(error: unknown): boolean {
  return error instanceof ApiError && error.errorCode === 'UNAUTHENTICATED'
}

export function createQueryClient(hooks: QueryClientHooks = {}): QueryClient {
  const onError = (error: unknown) => {
    if (isSessionEnded(error)) {
      hooks.onUnauthenticated?.()
    }
  }
  return new QueryClient({
    queryCache: new QueryCache({ onError }),
    mutationCache: new MutationCache({ onError }),
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        retry: (failureCount, error) =>
          failureCount < 1 && !(error instanceof ApiError && error.status >= 400 && error.status < 500),
        refetchOnWindowFocus: false,
      },
    },
  })
}
