import type { Schemas } from '../../api/client'
import { useProjectAccess } from '../projects/project-context'
import { isUnfinished } from '../runs/run-format'
import { useRuns } from '../runs/runs-api'

/**
 * Whether one of the agent's newest runs (for the release version on screen) is queued or running, i.e. its artifact
 * draft is on its way. Shares `StageRuns`' query, so it costs no extra request.
 */
export function useProducingRunActive(agent: Schemas['AgentKind']): boolean {
  const { project, releaseVersionId } = useProjectAccess()
  const runs = useRuns(project.id, agent, 1, releaseVersionId)
  return runs.data?.items.some(isUnfinished) ?? false
}
