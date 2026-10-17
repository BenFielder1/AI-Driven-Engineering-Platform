import { adminHandlers } from './admin'
import { artifactHandlers } from './artifacts'
import { authHandlers } from './auth'
import { connectionHandlers } from './connections'
import { executionHandlers } from './execution'
import { githubHandlers } from './github'
import { refinementHandlers } from './refinement'
import { releaseHandlers } from './release'
import { healthHandlers } from './health'
import { lifecycleHandlers } from './lifecycle'
import { notificationHandlers } from './notifications'
import { presetHandlers } from './presets'
import { projectHandlers } from './projects'
import { reviewHandlers } from './review'
import { runHandlers } from './runs'
import { versionHandlers } from './versions'

export const handlers = [
  ...healthHandlers,
  ...authHandlers,
  ...presetHandlers,
  ...projectHandlers,
  ...connectionHandlers,
  ...githubHandlers,
  ...lifecycleHandlers,
  ...artifactHandlers,
  ...runHandlers,
  ...refinementHandlers,
  ...executionHandlers,
  ...reviewHandlers,
  ...releaseHandlers,
  ...versionHandlers,
  ...notificationHandlers,
  ...adminHandlers,
]
