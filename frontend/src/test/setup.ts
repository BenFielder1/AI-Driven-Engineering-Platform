import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterAll, afterEach, beforeAll } from 'vitest'

import { resetAuthDb } from '../mocks/handlers/auth'
import { resetProjectsDb } from '../mocks/handlers/projects'
import { server } from '../mocks/server'

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
