import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { ComplianceTrendChart } from './compliance-trend-chart'

describe('ComplianceTrendChart (NFR-A11Y)', () => {
  it('NFR_A11Y_trend_table_scrolls_in_a_focusable_region_not_the_page', async () => {
    render(
      <ComplianceTrendChart
        trend={{
          threshold: 0.8,
          series: [
            {
              cycle: 1,
              checkpoint: 1,
              overallScore: 0.9,
              driftLevel: 'none',
              decision: 'release_eligible',
              createdAt: '2026-01-01T00:00:00Z',
              threshold: 0.8,
            },
          ],
        }}
      />,
    )
    // FR53_trend_chart: a ChartCard with "View as table".
    expect(screen.getByRole('img', { name: /Compliance score by review cycle Cycle 1: 90%/ })).toBeInTheDocument()
    await userEvent.setup().click(screen.getByRole('button', { name: 'View as table' }))
    const region = screen.getByRole('region', {
      name: 'Compliance trend table',
    })
    expect(region).toHaveAttribute('tabindex', '0')
    expect(region).toHaveClass('overflow-auto')
    expect(within(region).getByRole('table')).toBeInTheDocument()
  })
})
