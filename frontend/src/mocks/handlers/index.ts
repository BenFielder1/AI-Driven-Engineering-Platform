import { adminHandlers } from './admin'
import { analysisHandlers } from './analysis'
import { artifactHandlers } from './artifacts'
import { authHandlers } from './auth'
import { bitbucketHandlers } from './bitbucket'
import { connectionHandlers } from './connections'
import { deploymentHandlers } from './deployments'
import { executionHandlers } from './execution'
import { githubHandlers } from './github'
import { gitlabHandlers } from './gitlab'
import { refinementHandlers } from './refinement'
import { releaseHandlers } from './release'
import { healthHandlers } from './health'
import { lifecycleHandlers } from './lifecycle'
import { llmHandlers } from './llm'
import { migrationHandlers } from './migrations'
import { notificationHandlers } from './notifications'
import { platformConnectionHandlers } from './platform-connections'
import { projectHandlers } from './projects'
import { reviewHandlers } from './review'
import { runHandlers } from './runs'
import { stackHandlers } from './stack'
import { trackerHandlers } from './trackers'
import { versionHandlers } from './versions'

export const handlers = [
  ...healthHandlers,
  ...authHandlers,
  ...stackHandlers,
  ...projectHandlers,
  ...connectionHandlers,
  ...llmHandlers,
  ...platformConnectionHandlers,
  ...trackerHandlers,
  ...deploymentHandlers,
  ...migrationHandlers,
  ...githubHandlers,
  ...gitlabHandlers,
  ...bitbucketHandlers,
  ...lifecycleHandlers,
  // Before artifactHandlers: the stack_profile cases of the generic artifact routes (FR-183).
  ...analysisHandlers,
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
