import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterAll, afterEach, beforeAll, vi } from 'vitest'

import { resetAdminDb } from '../mocks/handlers/admin'
import { resetAuthDb } from '../mocks/handlers/auth'
import { resetBitbucketDb } from '../mocks/handlers/bitbucket'
import { resetConnectionsDb } from '../mocks/handlers/connections'
import { resetDeploymentDb } from '../mocks/handlers/deployments'
import { resetPlatformDb } from '../mocks/handlers/platform-connections'
import { resetGitHubDb } from '../mocks/handlers/github'
import { resetGitLabDb } from '../mocks/handlers/gitlab'
import { resetLlmDb } from '../mocks/handlers/llm'
import { resetNotificationsDb } from '../mocks/handlers/notifications'
import { resetProjectsDb } from '../mocks/handlers/projects'
import { resetStackDb } from '../mocks/handlers/stack'
import { server } from '../mocks/server'
import { router } from './memory-router'

// Next.js routing runs on an in-memory router in tests (src/test/memory-router.ts): `next/navigation` hooks and
// `next/link` read and drive it. `renderRoute` (src/test/render.tsx) renders the src/app tree for its URL.
vi.mock('next/navigation', async () => (await import('./next-mocks')).navigationMock)
vi.mock('next/link', async () => ({ default: (await import('./next-mocks')).LinkMock }))

// findBy*/waitFor default to 1 s, too short for whole-app renders (MSW round trips, route loaders) when the whole
// suite runs in parallel on a busy machine (NFR-TEST: no flaky tests).
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
  resetLlmDb()
  resetDeploymentDb()
  resetPlatformDb()
  resetGitHubDb()
  resetGitLabDb()
  resetBitbucketDb()
  resetStackDb()
  localStorage.clear()
  sessionStorage.clear()
  router.reset('/')
})
afterAll(() => {
  server.close()
})
