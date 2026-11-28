import type { PipelineDefinition, StageDefinition } from '../api/types'

function stageAgents(stage: StageDefinition, out: Set<string>): void {
  if (stage.agentKey) {
    out.add(stage.agentKey)
  }
  for (const agent of stage.fanOut?.agents ?? []) {
    out.add(agent)
  }
  for (const agent of [stage.variants?.selectAgent, stage.map?.agent, stage.map?.reduceAgent]) {
    if (agent) {
      out.add(agent)
    }
  }
  for (const participant of stage.conversation?.participants ?? []) {
    out.add(participant.agent)
  }
  for (const branch of stage.branches ?? []) {
    for (const child of branch.stages) {
      stageAgents(child, out)
    }
  }
}

/** Every agent key the definition's stages use (parallel branches, fan-outs, maps and conversations included), in order. */
export function definitionAgentKeys(definition: PipelineDefinition | undefined): string[] {
  const keys = new Set<string>()
  for (const stage of definition?.stages ?? []) {
    stageAgents(stage, keys)
  }
  return [...keys]
}
