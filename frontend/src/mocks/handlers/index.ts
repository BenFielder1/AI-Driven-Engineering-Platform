import { authHandlers } from './auth'
import { healthHandlers } from './health'
import { projectHandlers } from './projects'

export const handlers = [...healthHandlers, ...authHandlers, ...projectHandlers]
