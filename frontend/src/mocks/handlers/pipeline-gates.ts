import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { currentUser, unauthenticated } from './auth'
import { pipelineDefinitions } from './pipeline-definitions'
import { pipelinesDb } from './pipeline-store'
import { problem } from './problem'
import { access, isOwnerLevel, ownerMutation, record } from './projects'

type Gate = Schemas['PipelineGateDto']
type InboxItem = Schemas['PipelineGateInboxItemDto']

/** DecidePipelineGateRequest.CommentMaxLength. */
const COMMENT_MAX = 4000

/** FR-302: every pending gate on a project the caller can see, newest first (created, then id). */
function visiblePendingGates(pipelineId: string | null, gateType: string | null): InboxItem[] {
  const items: InboxItem[] = []
  for (const stored of pipelinesDb.runs) {
    const a = access(stored.run.projectId)
    if (a instanceof Response || (pipelineId !== null && stored.run.pipelineId !== pipelineId)) {
      continue
    }
    const title = pipelineDefinitions.find((d) => d.hash === stored.run.definitionHash)?.title ?? stored.run.pipelineId
    for (const gate of stored.gates) {
      if (gate.status === 'pending' && (gateType === null || gate.type === gateType)) {
        items.push({
          gate,
          projectName: a.project.name,
          pipelineId: stored.run.pipelineId,
          pipelineTitle: title,
          // The project gate rule: Owners (and Admins) decide.
          canDecide: isOwnerLevel(a),
        })
      }
    }
  }
  return items.sort((x, y) => y.gate.createdAt.localeCompare(x.gate.createdAt) || y.gate.id.localeCompare(x.gate.id))
}

/** FR-301: approve or reject through the engine route, with the project gate route's rules (Owners decide). */
function decide(status: 'approved' | 'rejected') {
  return async ({ request, params }: { request: Request; params: Record<string, string | readonly string[] | undefined> }) => {
    const body = (await record(request)) as Schemas['DecidePipelineGateRequest'] | null
    if ((body?.comment?.length ?? 0) > COMMENT_MAX) {
      return problem(400, 'VALIDATION', 'One or more validation errors occurred.', {
        errors: { comment: [`comment must be at most ${String(COMMENT_MAX)} characters.`] },
      })
    }
    const stored = pipelinesDb.runs.find((r) => r.run.id === params.runId)
    if (!stored) {
      return problem(404, 'NOT_FOUND', 'The pipeline run was not found.')
    }
    const a = ownerMutation(request, stored.run.projectId)
    if (a instanceof Response) {
      return a
    }
    if (pipelinesDb.unavailableRuns.has(stored.run.id)) {
      return problem(409, 'DEFINITION_UNAVAILABLE', 'The run’s pipeline definition is not available in this build.')
    }
    const gate = stored.gates.find((g) => g.id === params.gateId)
    if (!gate) {
      return problem(404, 'NOT_FOUND', 'The gate was not found.')
    }
    if (gate.status !== 'pending') {
      return problem(409, 'GATE_NOT_PENDING', 'The gate is not pending.')
    }
    const comment = body?.comment?.trim() ? body.comment.trim() : null
    const decided: Gate = { ...gate, status, decidedAt: new Date().toISOString(), decidedByUserId: a.userId, comment }
    stored.gates = stored.gates.map((g) => (g.id === gate.id ? decided : g))
    return HttpResponse.json<Gate>(decided)
  }
}

export const pipelineGateHandlers = [
  // FR-302: the gate inbox (signed in; only projects the caller can see). The cursor here is the next index.
  http.get('*/api/pipeline-gates', ({ request }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const q = new URL(request.url).searchParams
    const status = q.get('status') ?? 'pending'
    const limit = q.get('limit') === null ? 50 : Number(q.get('limit'))
    const start = q.get('cursor') === null ? 0 : Number(q.get('cursor'))
    if (status !== 'pending' || !Number.isInteger(limit) || limit < 1 || limit > 200 || !Number.isInteger(start) || start < 0) {
      return problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors: { limit: ['limit must be between 1 and 200.'] } })
    }
    const items = visiblePendingGates(q.get('pipelineId'), q.get('gateType'))
    return HttpResponse.json<Schemas['PipelineGateInboxPageDto']>({
      items: items.slice(start, start + limit),
      next: start + limit < items.length ? String(start + limit) : null,
    })
  }),
  http.post('*/api/pipeline-runs/:runId/gates/:gateId/approve', decide('approved')),
  http.post('*/api/pipeline-runs/:runId/gates/:gateId/reject', decide('rejected')),
]
