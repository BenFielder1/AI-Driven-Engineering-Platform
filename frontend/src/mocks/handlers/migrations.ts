import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { nextId } from './auth'
import { problem } from './problem'
import { access, currentVersionOf, type StoredProject } from './projects'

/** FR-153: Supabase BaaS migration runs (`GET /api/projects/:id/migrations[/:runId]`), following the contract. */

type Run = Schemas['MigrationRunDto']

/** A migration run of the project (succeeded by default), stored on the project. */
export function seedMigrationRun(p: StoredProject, overrides: Partial<Run> = {}): Run {
  const run: Run = {
    id: nextId(),
    environment: 'production',
    managedDatabaseId: 'db-1',
    provider: 'supabase',
    releaseId: null,
    releaseVersionId: currentVersionOf(p)?.id ?? 'rv-1',
    gitSha: 'a1b2c3d4e5f60718293a4b5c6d7e8f9012345678',
    status: 'succeeded',
    files: [{ version: '20260901120000', name: 'orders', ms: 420 }],
    totalMs: 420,
    errorCode: null,
    errorMessage: null,
    advisorFindings: [],
    startedAt: '2026-09-02T10:00:00Z',
    finishedAt: '2026-09-02T10:00:01Z',
    ...overrides,
  }
  p.migrationRuns = [...(p.migrationRuns ?? []), run]
  return run
}

const newestFirst = (runs: readonly Run[]) => [...runs].sort((a, b) => b.startedAt.localeCompare(a.startedAt))

export const migrationHandlers = [
  http.get('*/api/projects/:id/migrations', ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const versionId = new URL(request.url).searchParams.get('releaseVersionId')
    const runs = (a.project.migrationRuns ?? []).filter((r) => !versionId || r.releaseVersionId === versionId)
    return HttpResponse.json<Run[]>(newestFirst(runs))
  }),

  http.get('*/api/projects/:id/migrations/:runId', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const run = (a.project.migrationRuns ?? []).find((r) => r.id === params.runId)
    return run ? HttpResponse.json<Run>(run) : problem(404, 'NOT_FOUND', 'The migration run was not found.')
  }),
]
