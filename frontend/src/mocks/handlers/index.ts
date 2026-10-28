import { adminHandlers } from './admin'
import { artifactHandlers } from './artifacts'
import { authHandlers } from './auth'
import { connectionHandlers } from './connections'
import { deploymentHandlers } from './deployments'
import { executionHandlers } from './execution'
import { githubHandlers } from './github'
import { refinementHandlers } from './refinement'
import { releaseHandlers } from './release'
import { healthHandlers } from './health'
import { lifecycleHandlers } from './lifecycle'
import { llmHandlers } from './llm'
import { notificationHandlers } from './notifications'
import { presetHandlers } from './presets'
import { projectHandlers } from './projects'
import { reviewHandlers } from './review'
import { runHandlers } from './runs'
import { stackHandlers } from './stack'
import { versionHandlers } from './versions'

export const handlers = [
  ...healthHandlers,
  ...authHandlers,
  ...presetHandlers,
  ...stackHandlers,
  ...projectHandlers,
  ...connectionHandlers,
  ...llmHandlers,
  ...deploymentHandlers,
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
