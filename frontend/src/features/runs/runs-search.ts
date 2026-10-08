import { agentKinds } from '../projects/labels'
import { oneOf } from '../projects/projects-search'
import type { AgentKind } from './runs-api'

/** /projects/:id/runs search params (FR-23). Defaults are omitted from the URL; `run` opens the detail drawer. */
export interface RunsSearch {
  agent?: AgentKind
  page?: number
  run?: string
}

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** Parses untrusted URL search params into RunsSearch (invalid values are dropped). */
export function parseRunsSearch(search: Record<string, unknown>): RunsSearch {
  const page = typeof search.page === 'number' ? search.page : Number(search.page)
  return {
    agent: oneOf(agentKinds, search.agent),
    page: Number.isInteger(page) && page > 1 ? page : undefined,
    run: typeof search.run === 'string' && uuid.test(search.run) ? search.run : undefined,
  }
}
