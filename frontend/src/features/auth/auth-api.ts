import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { toApiError } from '../../lib/errors'

export type User = Schemas['UserDto']

export const authKeys = {
  all: ['auth'] as const,
  me: () => [...authKeys.all, 'me'] as const,
  providers: () => [...authKeys.all, 'providers'] as const,
}

/** GET /api/auth/me: the signed-in user, or `null` when signed out (401). */
export const meQueryOptions = queryOptions({
  queryKey: authKeys.me(),
  queryFn: async ({ signal }): Promise<User | null> => {
    const { data, error, response } = await api.GET('/api/auth/me', { signal })
    if (data) {
      return data
    }
    if (response.status === 401) {
      return null
    }
    throw toApiError(error, response)
  },
  staleTime: 60_000,
  retry: false,
})

export function useMe() {
  return useQuery(meQueryOptions)
}

/** The signed-in user inside the authenticated shell (the route guard guarantees it is loaded). */
export function useCurrentUser(): User | null {
  return useQuery(meQueryOptions).data ?? null
}

export const providersQueryOptions = queryOptions({
  queryKey: authKeys.providers(),
  queryFn: async ({ signal }): Promise<Schemas['AuthProvidersDto']> => {
    const { data, error, response } = await api.GET('/api/auth/providers', { signal })
    if (data) {
      return data
    }
    throw toApiError(error, response)
  },
  staleTime: 5 * 60_000,
})

export function useAuthProviders() {
  return useQuery(providersQueryOptions)
}

/** FR-1 sign-in. On success the session cookie is set; `me` is refetched so guards see the user. */
export function useLogin() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: Schemas['LoginRequest']) => {
      const { error, response } = await api.POST('/api/auth/login', { body })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSuccess: () => queryClient.query({ ...meQueryOptions, staleTime: 0 }),
  })
}

/** FR-1 registration: 201 signs the user in, so the returned user seeds `me`. */
export function useRegister() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: Schemas['RegisterRequest']) => {
      const { data, error, response } = await api.POST('/api/auth/register', { body })
      if (!data) {
        throw toApiError(error, response)
      }
      return data
    },
    onSuccess: (user) => {
      queryClient.setQueryData(authKeys.me(), user)
    },
  })
}

/** FR-1 sign-out. Clears every cached query so no previous user's data survives. */
export function useLogout() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => {
      const { error, response } = await api.POST('/api/auth/logout')
      // 401 means the session had already ended: treat as signed out.
      if (!response.ok && response.status !== 401) {
        throw toApiError(error, response)
      }
    },
    onSuccess: () => {
      queryClient.clear()
      queryClient.setQueryData(authKeys.me(), null)
    },
  })
}

/** Full-page navigation targets for the GitHub OAuth flow (FR-2); handled by the API, not the SPA router. */
export function githubSignInHref(returnUrl: string): string {
  return `/api/auth/github?returnUrl=${encodeURIComponent(returnUrl)}`
}

export function githubLinkHref(returnUrl = '/account'): string {
  return `/api/auth/github/link?returnUrl=${encodeURIComponent(returnUrl)}`
}
