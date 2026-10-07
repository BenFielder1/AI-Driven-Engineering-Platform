import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'

type HealthResponse = Schemas['HealthResponse']

export const healthyReady: HealthResponse = {
  status: 'Healthy',
  checks: [{ name: 'database', status: 'Healthy', description: 'Database reachable; migrations applied.' }],
}

export const unhealthyReady: HealthResponse = {
  status: 'Unhealthy',
  checks: [{ name: 'database', status: 'Unhealthy', description: 'Database has 1 pending migration(s).' }],
}

export const healthHandlers = [
  http.get('*/api/health/live', () => HttpResponse.json<HealthResponse>({ status: 'Healthy', checks: [] })),
  http.get('*/api/health/ready', () => HttpResponse.json<HealthResponse>(healthyReady)),
]
