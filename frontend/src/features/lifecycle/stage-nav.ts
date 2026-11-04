import { allPhases, phases } from '../projects/labels'
import type { Phase } from '../shell/nav-config'

/**
 * The stage before / after `stage`, or null at either end. `analysis`: the version infers its stack, so Analysis
 * (FR-182) is the first stage; otherwise it isn't one, and Refinement comes after it.
 */
export function adjacentStages(stage: Phase, { analysis = false }: { analysis?: boolean } = {}): { previous: Phase | null; next: Phase | null } {
  const stages: readonly Phase[] = analysis ? allPhases : phases
  const index = stages.indexOf(stage)
  if (index === -1) {
    return { previous: null, next: stage === 'analysis' ? phases[0] : null }
  }
  return { previous: stages[index - 1] ?? null, next: stages[index + 1] ?? null }
}

/** The stage page `pathname` shows (`/projects/:id/<stage>`, including Analysis), if any. */
export function stageFromPath(pathname: string): Phase | undefined {
  const segment = /^\/projects\/[^/]+\/([^/?#]+)/.exec(pathname)?.[1]
  return allPhases.find((p) => p === segment)
}
