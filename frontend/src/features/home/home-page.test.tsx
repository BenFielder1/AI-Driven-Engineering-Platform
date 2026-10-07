import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { unhealthyReady } from '../../mocks/handlers/health'
import { server } from '../../mocks/server'
import { renderWithProviders } from '../../test/render'
import { HomePage } from './home-page'

describe('HomePage (NFR-OBS readiness smoke)', () => {
  it('shows the app name and a healthy API status', async () => {
    renderWithProviders(<HomePage />)

    expect(screen.getByRole('heading', { level: 1, name: 'AIDE Platform' })).toBeInTheDocument()
    expect(await screen.findByText('Healthy', { selector: 'span' })).toBeInTheDocument()
    expect(screen.getByText(/database: Healthy/)).toBeInTheDocument()
  })

  it('shows Unhealthy when /api/health/ready returns 503', async () => {
    server.use(
      http.get('*/api/health/ready', () =>
        HttpResponse.json<Schemas['HealthResponse']>(unhealthyReady, { status: 503 }),
      ),
    )

    renderWithProviders(<HomePage />)

    expect(await screen.findByText('Unhealthy', { selector: 'span' })).toBeInTheDocument()
  })

  it('shows API unreachable on a network error', async () => {
    server.use(http.get('*/api/health/ready', () => HttpResponse.error()))

    renderWithProviders(<HomePage />)

    expect(await screen.findByText('API unreachable')).toBeInTheDocument()
  })
})
