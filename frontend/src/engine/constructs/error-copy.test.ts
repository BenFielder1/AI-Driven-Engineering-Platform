import { describe, expect, it } from 'vitest'

import { ApiError, errorMessage, toApiError } from '../../lib/errors'

describe('SOURCE_ARTIFACT_MISSING copy (FR-352, D-0.8.0-19)', () => {
  it('FR363_source_artifact_missing_fills_artifact: the problem reason names the artifact, and no placeholder reaches the screen', () => {
    const named = toApiError({ errorCode: 'SOURCE_ARTIFACT_MISSING', title: 'x', reason: 'Runbook' }, new Response(null, { status: 409 }))
    expect(errorMessage(named)).toBe('Lock a version of Runbook before starting a run.')
    const unnamed = new ApiError({ status: 409, errorCode: 'SOURCE_ARTIFACT_MISSING' })
    expect(errorMessage(unnamed)).toBe('Lock a version of the source artifact before starting a run.')
  })
})
