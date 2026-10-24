import { describe, expect, it } from 'vitest'

import { appPaths, toAppRouterPattern } from '../lib/app-paths'
import { matchAppRoute, pageFiles, routeFromFile } from './app-routes'

/**
 * FR-95 route parity: every v2 route pattern (v2 SPEC §7.2 and the v2 TanStack route tree, listed in
 * src/lib/app-paths.ts) has a matching src/app page, at the same URL.
 */
describe('FR-95 route parity', () => {
  it.each(appPaths.map((p) => [p]))('FR-95: %s has a src/app page', (path) => {
    const pattern = toAppRouterPattern(path)
    expect(matchAppRoute(pattern)?.route.pattern, `no src/app page serves ${path}`).toBe(pattern)
  })

  it('FR-95: every src/app page is a v2 route (no new URLs)', () => {
    const known = new Set<string>(appPaths.map(toAppRouterPattern))
    for (const file of pageFiles) {
      expect(known, file).toContain(routeFromFile(file).pattern)
    }
  })
})
