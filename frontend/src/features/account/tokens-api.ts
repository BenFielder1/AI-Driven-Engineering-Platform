import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { toApiError } from '../../lib/errors'

export type ApiToken = Schemas['ApiTokenDto']
export type CreatedApiToken = Schemas['CreatedApiTokenDto']

export const tokenKeys = {
  all: ['me', 'tokens'] as const,
  list: () => [...tokenKeys.all, 'list'] as const,
}

export const tokensQueryOptions = queryOptions({
  queryKey: tokenKeys.list(),
  queryFn: async ({ signal }): Promise<ApiToken[]> => {
    const { data, error, response } = await api.GET('/api/me/tokens', { signal })
    if (data) {
      return data
    }
    throw toApiError(error, response)
  },
})

/** FR-4: the user's personal API tokens (never the token values). */
export function useApiTokens() {
  return useQuery(tokensQueryOptions)
}

/** FR-4: creates a token; the response carries the plain token, which is shown once and not cached. */
export function useCreateApiToken() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: Schemas['CreateApiTokenRequest']): Promise<CreatedApiToken> => {
      const { data, error, response } = await api.POST('/api/me/tokens', { body })
      if (!data) {
        throw toApiError(error, response)
      }
      return data
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: tokenKeys.all }),
  })
}

/** FR-4: revokes a token (it stops authenticating immediately). */
export function useRevokeApiToken() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => {
      const { error, response } = await api.DELETE('/api/me/tokens/{id}', { params: { path: { id } } })
      if (!response.ok) {
        throw toApiError(error, response)
      }
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: tokenKeys.all }),
  })
}
