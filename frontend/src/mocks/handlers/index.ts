import { artifactHandlers } from './artifacts'
import { authHandlers } from './auth'
import { healthHandlers } from './health'
import { lifecycleHandlers } from './lifecycle'
import { projectHandlers } from './projects'

export const handlers = [...healthHandlers, ...authHandlers, ...projectHandlers, ...lifecycleHandlers, ...artifactHandlers]
