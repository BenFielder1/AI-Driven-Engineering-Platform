import { describe, expect, it } from 'vitest'

import { projectKpis } from './project-kpis'

describe('Projects KPI row (SPEC §7.2)', () => {
  it('FR53_projects_kpis: counts active, in review and released from the list; gates are not reported yet', () => {
    const items = [
      { status: 'active' as const },
      { status: 'building' as const },
      { status: 'reviewing' as const },
      { status: 'rolled_back' as const },
      { status: 'released' as const },
    ]
    expect(projectKpis(items, 5)).toEqual({ active: 4, inReview: 1, awaitingGates: null, released: 1, partial: false })
  })

  it('FR53_projects_kpis: says when the list holds more projects than were read', () => {
    expect(projectKpis([{ status: 'active' }], 150).partial).toBe(true)
  })
})
