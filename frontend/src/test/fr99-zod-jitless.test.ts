import { afterEach, describe, expect, it, vi } from 'vitest'
import { z } from 'zod'

describe('FR-99: Zod never probes eval under the strict CSP', () => {
  afterEach(() => {
    z.config({ jitless: true })
  })

  it('FR-99: instrumentation-client turns on jitless before any app module runs', async () => {
    z.config({ jitless: false })
    vi.resetModules()
    await import('../instrumentation-client')
    expect(z.config().jitless).toBe(true)
  })
})
