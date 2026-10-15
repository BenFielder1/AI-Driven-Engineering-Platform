import { phases } from '../projects/labels'
import type { Phase } from '../shell/nav-config'

/** The stage before / after `stage`, or null at either end. */
export function adjacentStages(stage: Phase): { previous: Phase | null; next: Phase | null } {
  const index = phases.indexOf(stage)
  return { previous: phases[index - 1] ?? null, next: phases[index + 1] ?? null }
}

/** The stage page `pathname` shows (`/projects/:id/<stage>`), if any. */
export function stageFromPath(pathname: string): Phase | undefined {
  const segment = /^\/projects\/[^/]+\/([^/?#]+)/.exec(pathname)?.[1]
  return phases.find((p) => p === segment)
}
