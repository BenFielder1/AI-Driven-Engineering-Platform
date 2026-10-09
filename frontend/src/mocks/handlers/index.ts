import { artifactHandlers } from './artifacts'
import { authHandlers } from './auth'
import { executionHandlers } from './execution'
import { refinementHandlers } from './refinement'
import { healthHandlers } from './health'
import { lifecycleHandlers } from './lifecycle'
import { projectHandlers } from './projects'
import { runHandlers } from './runs'

export const handlers = [
  ...healthHandlers,
  ...authHandlers,
  ...projectHandlers,
  ...lifecycleHandlers,
  ...artifactHandlers,
  ...runHandlers,
  ...refinementHandlers,
  ...executionHandlers,
]
