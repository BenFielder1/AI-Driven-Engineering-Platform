import { describe, expect, it } from 'vitest'

import { describeActivity } from './activity-summary'

const d = (type: string, payload: Record<string, unknown>) => describeActivity({ type, payload, summary: 'server summary' })

describe('FR-259: activity summaries of the build check events', () => {
  it('FR259_BUILD_CHECK_STARTED names the attempt and commit', () => {
    expect(d('BUILD_CHECK_STARTED', { attempt: 0, headSha: '9b1c2d3e4f5a' })).toBe('Build check (initial) started for commit 9b1c2d3')
    expect(d('BUILD_CHECK_STARTED', { attempt: 2 })).toBe('Build check attempt 2 started')
  })

  it('FR259_BUILD_CHECK_COMPLETED says how it ended', () => {
    expect(d('BUILD_CHECK_COMPLETED', { attempt: 2, status: 'passed', failureIds: [] })).toBe('Build check attempt 2 passed')
    expect(d('BUILD_CHECK_COMPLETED', { attempt: 0, status: 'skipped', failureIds: [] })).toBe(
      'Build check (initial) skipped: no preview target or required checks',
    )
    expect(d('BUILD_CHECK_COMPLETED', { attempt: 1, status: 'failed', failureIds: ['failure:preview:vercel'] })).toBe(
      'Build check attempt 1 failed (1 failure)',
    )
    expect(d('BUILD_CHECK_COMPLETED', { attempt: 5, status: 'exhausted', failureIds: ['a', 'b'] })).toBe(
      'Build check attempt 5: build still failing (2 failures); review will include the failures',
    )
    expect(d('BUILD_CHECK_COMPLETED', { attempt: 1, status: 'cancelled' })).toBe('Build check attempt 1 cancelled')
  })

  it('FR259_BUILD_FIX_PLANNED lists the step numbers', () => {
    expect(d('BUILD_FIX_PLANNED', { attempt: 0, stepNumbers: [4, 5] })).toBe('Build fix steps 4 and 5 planned')
    expect(d('BUILD_FIX_PLANNED', { attempt: 1, stepNumbers: [6] })).toBe('Build fix step 6 planned')
    expect(d('BUILD_FIX_PLANNED', {})).toBe('Build fix steps planned')
  })
})
