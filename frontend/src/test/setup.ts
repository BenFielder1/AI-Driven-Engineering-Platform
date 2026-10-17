import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterAll, afterEach, beforeAll } from 'vitest'

import { resetAdminDb } from '../mocks/handlers/admin'
import { resetAuthDb } from '../mocks/handlers/auth'
import { resetConnectionsDb } from '../mocks/handlers/connections'
import { resetGitHubDb } from '../mocks/handlers/github'
import { resetNotificationsDb } from '../mocks/handlers/notifications'
import { resetPresetCatalog } from '../mocks/handlers/preset-catalog'
import { resetProjectsDb } from '../mocks/handlers/projects'
import { server } from '../mocks/server'

// findBy*/waitFor default to 1 s, too short for whole-app renders (MSW round trips, route loaders) when the whole
// suite runs in parallel on a busy machine (NFR-TEST: no flaky tests). Route chunks aren't lazy under Vitest
// (vite.config.ts), so nothing here waits on a module transform.
configure({ asyncUtilTimeout: 5000 })

// jsdom lacks APIs that radix-ui (menus, dialogs), cmdk and recharts call: minimal no-op stand-ins.
class ResizeObserverStub {
  observe() {
    // no layout in jsdom
  }
  unobserve() {
    // no layout in jsdom
  }
  disconnect() {
    // no layout in jsdom
  }
}
// Partial: jsdom declares some of these as properties without implementing them, so check the value.
const win = globalThis as Partial<typeof globalThis>
const proto = Element.prototype as Partial<Element>
if (typeof win.ResizeObserver !== 'function') {
  globalThis.ResizeObserver = ResizeObserverStub
}
if (typeof proto.scrollIntoView !== 'function') {
  Element.prototype.scrollIntoView = () => undefined
}
// No media queries match: below xl, system theme = dark (the dark-first default). Tests that need a match stub it.
if (typeof win.matchMedia !== 'function') {
  globalThis.matchMedia = (query: string): MediaQueryList => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
  })
}
if (typeof proto.hasPointerCapture !== 'function') {
  Element.prototype.hasPointerCapture = () => false
  Element.prototype.releasePointerCapture = () => undefined
}

beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' })
})
afterEach(() => {
  cleanup()
  server.resetHandlers()
  resetAuthDb()
  resetProjectsDb()
  resetNotificationsDb()
  resetAdminDb()
  resetConnectionsDb()
  resetGitHubDb()
  resetPresetCatalog()
  localStorage.clear()
})
afterAll(() => {
  server.close()
})
