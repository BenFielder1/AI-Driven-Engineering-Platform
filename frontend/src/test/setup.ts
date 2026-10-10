import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterAll, afterEach, beforeAll } from 'vitest'

import { resetAuthDb } from '../mocks/handlers/auth'
import { resetNotificationsDb } from '../mocks/handlers/notifications'
import { resetProjectsDb } from '../mocks/handlers/projects'
import { server } from '../mocks/server'

// findBy*/waitFor default to 1 s, too short for whole-app renders (MSW round trips, route loaders) when the whole
// suite runs in parallel on a busy machine (NFR-TEST: no flaky tests). Route chunks aren't lazy under Vitest
// (vite.config.ts), so nothing here waits on a module transform.
configure({ asyncUtilTimeout: 5000 })

beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' })
})
afterEach(() => {
  cleanup()
  server.resetHandlers()
  resetAuthDb()
  resetProjectsDb()
  resetNotificationsDb()
  localStorage.clear()
})
afterAll(() => {
  server.close()
})
