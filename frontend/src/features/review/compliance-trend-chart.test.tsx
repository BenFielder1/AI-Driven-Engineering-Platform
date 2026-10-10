import { render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { ComplianceTrendChart } from './compliance-trend-chart'

describe('ComplianceTrendChart (NFR-A11Y)', () => {
  it('NFR_A11Y_trend_table_scrolls_in_a_focusable_region_not_the_page', () => {
    render(
      <ComplianceTrendChart
        trend={{
          threshold: 0.8,
          series: [
            {
              cycle: 1,
              projectVersion: 1,
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
    const region = screen.getByRole('region', {
      name: 'Compliance score table',
    })
    expect(region).toHaveAttribute('tabindex', '0')
    expect(region).toHaveClass('overflow-x-auto')
    expect(within(region).getByRole('table')).toBeInTheDocument()
  })
})
