import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterAll, afterEach, beforeAll } from 'vitest'

import { resetAuthDb } from '../mocks/handlers/auth'
import { resetProjectsDb } from '../mocks/handlers/projects'
import { server } from '../mocks/server'

// findBy*/waitFor default to 1 s, too short for the first render of lazy route chunks when the whole suite runs in
// parallel on a busy machine (NFR-TEST: no flaky tests).
configure({ asyncUtilTimeout: 5000 })

beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' })
})
afterEach(() => {
  cleanup()
  server.resetHandlers()
  resetAuthDb()
  resetProjectsDb()
  localStorage.clear()
})
afterAll(() => {
  server.close()
})
