import { adminHandlers } from './admin'
import { artifactHandlers } from './artifacts'
import { authHandlers } from './auth'
import { executionHandlers } from './execution'
import { refinementHandlers } from './refinement'
import { releaseHandlers } from './release'
import { healthHandlers } from './health'
import { lifecycleHandlers } from './lifecycle'
import { notificationHandlers } from './notifications'
import { projectHandlers } from './projects'
import { reviewHandlers } from './review'
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
  ...reviewHandlers,
  ...releaseHandlers,
  ...notificationHandlers,
  ...adminHandlers,
]
