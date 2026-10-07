import { useQuery } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'

export const healthKeys = {
  all: ['health'] as const,
  ready: () => [...healthKeys.all, 'ready'] as const,
}

/** GET /api/health/ready. A 503 still carries a HealthResponse body, so both count as data. */
export function useApiReadiness() {
  return useQuery({
    queryKey: healthKeys.ready(),
    queryFn: async ({ signal }): Promise<Schemas['HealthResponse']> => {
      const { data, error, response } = await api.GET('/api/health/ready', { signal })
      if (data) {
        return data
      }
      if (response.status === 503) {
        return error
      }
      throw new Error(`Readiness check failed with HTTP ${String(response.status)}`)
    },
    retry: false,
  })
}
