import { adminHandlers } from './admin'
import { analysisHandlers } from './analysis'
import { artifactHandlers } from './artifacts'
import { authHandlers } from './auth'
import { bitbucketHandlers } from './bitbucket'
import { connectionHandlers } from './connections'
import { deploymentHandlers } from './deployments'
import { engineRunControlHandlers } from './engine-run-control'
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
import { engineConstructHandlers } from './engine-constructs'
import { pipelineGateHandlers } from './pipeline-gates'
import { pipelineHandlers } from './pipelines'
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
  // 0.8.0 FR-363, FR-364: construct fixture definitions and map items (before pipelineHandlers, which 404 unknown hashes).
  ...engineConstructHandlers,
  // 0.7.0 FR-299–FR-303: the engine's pipeline routes.
  ...pipelineHandlers,
  // 0.7.0 FR-302, FR-307: the gate inbox and the engine's gate decisions.
  ...pipelineGateHandlers,
  // 0.8.0 FR-362, FR-365, FR-366: start run, submit input, agent feedback, schedules and webhooks.
  ...engineRunControlHandlers,
  ...refinementHandlers,
  ...executionHandlers,
  ...reviewHandlers,
  ...releaseHandlers,
  ...versionHandlers,
  ...notificationHandlers,
  ...adminHandlers,
]
