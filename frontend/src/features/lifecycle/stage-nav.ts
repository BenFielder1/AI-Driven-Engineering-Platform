import { allPhases } from '../projects/labels'
import type { Phase } from '../shell/nav-config'
import { fallbackSdlcStages, type SdlcStage } from './sdlc-stages'

/**
 * The stage before / after `stage`, or null at either end. `analysis`: the version infers its stack, so Analysis
 * (FR-182) is a stage; otherwise it isn't one, and the first other stage comes after it. `stages`: the order, from
 * the SDLC definition (FR-308); the hard-coded fallback list when absent.
 */
export function adjacentStages(
  stage: Phase,
  { analysis = false, stages = fallbackSdlcStages }: { analysis?: boolean; stages?: readonly SdlcStage[] } = {},
): { previous: Phase | null; next: Phase | null } {
  const order = stages.map((s) => s.stage).filter((s) => analysis || s !== 'analysis')
  const index = order.indexOf(stage)
  if (index === -1) {
    return { previous: null, next: stage === 'analysis' ? (order[0] ?? null) : null }
  }
  return { previous: order[index - 1] ?? null, next: order[index + 1] ?? null }
}

/** The stage page `pathname` shows (`/projects/:id/<stage>`, including Analysis), if any. */
export function stageFromPath(pathname: string): Phase | undefined {
  const segment = /^\/projects\/[^/]+\/([^/?#]+)/.exec(pathname)?.[1]
  return allPhases.find((p) => p === segment)
}
