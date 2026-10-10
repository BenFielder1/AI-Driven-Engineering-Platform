import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { antiforgeryFailure, authDb, nextId } from './auth'
import { problem } from './problem'
import { access, ownerMutation, record, stateDto, type Access, type StoredProject } from './projects'

type Phase = Schemas['Phase']
type ProjectStatus = Schemas['ProjectStatus']
type Gate = Schemas['GateDto']
type GateType = Schemas['GateType']
type Actor = Schemas['ActorDto']
type ActivityItem = Schemas['ActivityItemDto']
type EventCategory = NonNullable<Schemas['EventCategory']>

const phases: readonly Phase[] = ['refinement', 'specification', 'planning', 'execution', 'review', 'release']

/** SPEC §4.1: status follows phase. */
const statusFor: Record<Phase, ProjectStatus> = {
  refinement: 'active',
  specification: 'active',
  planning: 'active',
  execution: 'building',
  review: 'reviewing',
  release: 'releasing',
}

const systemActor: Actor = { id: null, name: 'aide-system', isSystem: true }

function actorFor(userId: string): Actor {
  const user = authDb.users.find((u) => u.id === userId)
  return { id: userId, name: user?.name ?? 'Unknown', isSystem: false }
}

// ---- Seeding helpers (tests) ------------------------------------------------------------------------------------

let eventClock = Date.parse('2026-09-01T10:00:00Z')

/** Appends an event to the project's feed (newest is last). Summaries and payloads mirror the server's (raw wire values). */
export function addEvent(
  project: StoredProject,
  init: {
    type: string
    category: EventCategory | null
    summary: string
    payload?: ActivityItem['payload']
    actor?: Actor
    occurredAt?: string
  },
): ActivityItem {
  eventClock += 60_000
  const item: ActivityItem = {
    id: nextId(),
    type: init.type,
    category: init.category,
    occurredAt: init.occurredAt ?? new Date(Math.max(eventClock, Date.now())).toISOString(),
    actor: init.actor ?? systemActor,
    summary: init.summary,
    payload: init.payload ?? {},
  }
  project.events.push(item)
  return item
}

/** Seeds `count` events, oldest first, numbered from 1 (`Event 1` … `Event N`). */
export function seedActivity(
  project: StoredProject,
  count: number,
  init: { category?: EventCategory; type?: string; label?: string } = {},
) {
  const base = Date.parse('2026-09-01T00:00:00Z')
  for (let i = 1; i <= count; i += 1) {
    addEvent(project, {
      type: init.type ?? 'PHASE_CHANGED',
      category: init.category ?? 'transition',
      summary: `${init.label ?? 'Event'} ${String(i)}`,
      occurredAt: new Date(base + i * 60_000).toISOString(),
    })
  }
}

/** Seeds a gate (pending unless overridden). */
export function seedGate(project: StoredProject, overrides: Partial<Gate> = {}): Gate {
  const gate: Gate = {
    id: nextId(),
    type: 'spec_approval',
    status: 'pending',
    artifactId: null,
    artifactType: null,
    artifactVersion: null,
    escalationReason: null,
    reason: null,
    createdAt: '2026-09-01T09:00:00Z',
    decidedAt: null,
    decidedBy: null,
    comment: null,
    decision: null,
    targetVersion: null,
    overdue: false,
    remindedAt: null,
    executionStepId: null,
    stepNumber: null,
    ...overrides,
  }
  if (gate.type === 'spec_approval' && overrides.artifactType === undefined) {
    gate.artifactId = nextId()
    gate.artifactType = 'technical_specification'
    gate.artifactVersion = 1
  }
  project.gates.unshift(gate)
  return gate
}

/** Marks an artifact type approved/locked at `version` (to satisfy §4.1 entry conditions). */
export function lockArtifact(project: StoredProject, type: Schemas['ArtifactType'], version = 1) {
  const existing = project.state.artifacts.find((a) => a.type === type)
  const locked = { type, latestVersion: version, latestStatus: 'locked' as const, currentLockedVersion: version }
  if (existing) {
    Object.assign(existing, locked)
  } else {
    project.state.artifacts.push(locked)
  }
}

// ---- Domain simulation (mirrors SPEC §4.1, §4.2, §4.8) -----------------------------------------------------------

const lockedVersion = (p: StoredProject, type: Schemas['ArtifactType']) =>
  p.state.artifacts.find((a) => a.type === type)?.currentLockedVersion ?? null

/** §4.1 entry conditions that aren't met (gate approval for release is handled by the gate itself). */
function unmetConditions(p: StoredProject, entering: Phase): string[] {
  const unmet: string[] = []
  switch (entering) {
    case 'specification':
      if (lockedVersion(p, 'project_request') === null) {
        unmet.push('The project request must be approved.')
      }
      break
    case 'planning':
      if (lockedVersion(p, 'technical_specification') === null) {
        unmet.push('The technical specification must be approved.')
      }
      break
    case 'execution':
      if (lockedVersion(p, 'technical_specification') === null) {
        unmet.push('The technical specification must be approved.')
      }
      if (lockedVersion(p, 'implementation_plan') === null) {
        unmet.push('The implementation plan must be approved.')
      }
      if (p.state.execution.total < 1) {
        unmet.push('The plan must have at least one step.')
      }
      break
    case 'review':
      if (p.state.execution.total === 0 || p.state.execution.completed < p.state.execution.total) {
        unmet.push('All steps of the current plan must be completed.')
      }
      break
    case 'release':
      if (p.state.review.latestDecision !== 'release_eligible') {
        unmet.push('The latest review decision must be release eligible.')
      }
      break
    case 'refinement':
      break
  }
  return unmet
}

/** §4.2: the gate the approval mode requires before entering `entering`, if any. */
function gateRequired(p: StoredProject, entering: Phase): GateType | null {
  if (entering === 'planning') {
    return p.approvalMode === 'autonomous' ? null : 'spec_approval'
  }
  if (entering === 'execution') {
    return p.approvalMode === 'strict' ? 'plan_approval' : null
  }
  return entering === 'release' ? 'pre_release' : null
}

const gatedArtifact: Partial<Record<GateType, Schemas['ArtifactType']>> = {
  spec_approval: 'technical_specification',
  plan_approval: 'implementation_plan',
}

const gateEntering: Record<Exclude<GateType, 'escalation'>, Phase> = {
  spec_approval: 'planning',
  plan_approval: 'execution',
  pre_release: 'release',
}

function moveTo(p: StoredProject, to: Phase, actor: Actor, kind: 'forward' | 'cycle' | 'rollback', version?: number) {
  const from = p.currentPhase
  p.currentPhase = to
  if (kind === 'forward') {
    p.projectVersion += 1
    p.status = statusFor[to]
  } else if (kind === 'rollback') {
    p.projectVersion = version ?? p.projectVersion
    p.status = 'rolled_back'
  } else {
    p.status = statusFor[to]
  }
  p.phaseAtVersion[p.projectVersion] = to
  p.updatedAt = new Date().toISOString()
  addEvent(p, {
    type: 'PHASE_CHANGED',
    category: 'transition',
    summary: `Phase changed from ${from} to ${to} (version ${String(p.projectVersion)})`,
    payload: { from, to, status: p.status, projectVersion: p.projectVersion },
    actor,
  })
}

/** Writer (Contributor/Owner/Admin) access for lifecycle mutations, including antiforgery. */
function writerMutation(request: Request, id: unknown): Access {
  const a = access(id)
  if (a instanceof Response) {
    return a
  }
  const rejected = antiforgeryFailure(request)
  if (rejected) {
    return rejected
  }
  return a.level === 'viewer' ? problem(403, 'FORBIDDEN', 'Viewers can’t change the project.') : a
}

const invalidDecision = (title: string) => problem(400, 'INVALID_DECISION', title)

function findPendingGate(p: StoredProject, gateId: unknown): Gate | Response {
  const gate = p.gates.find((g) => g.id === gateId)
  if (!gate) {
    return problem(404, 'NOT_FOUND', 'The gate was not found.')
  }
  if (gate.status !== 'pending') {
    return problem(409, 'GATE_NOT_PENDING', 'Only pending gates can be decided.')
  }
  return gate
}

function decide(gate: Gate, status: Gate['status'], userId: string, body: Schemas['DecideGateRequest'] | null) {
  gate.status = status
  gate.decidedAt = new Date().toISOString()
  gate.decidedBy = actorFor(userId)
  gate.comment = body?.comment?.trim() ? body.comment : null
  gate.overdue = false
}

export const lifecycleHandlers = [
  // FR-11, FR-12: advance to next phase.
  http.post('*/api/projects/:id/phase/advance', async ({ request, params }) => {
    const a = writerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    const p = a.project
    const entering = phases[phases.indexOf(p.currentPhase) + 1]
    if (!entering) {
      return problem(409, 'PHASE_PRECONDITION_FAILED', 'The project is already in its last phase.', {
        details: ['Release is the last phase.'],
      })
    }
    if (p.gates.some((g) => g.status === 'pending')) {
      return problem(409, 'GATE_PENDING', 'A gate is waiting for a decision.')
    }
    const unmet = unmetConditions(p, entering)
    if (unmet.length > 0) {
      return problem(409, 'PHASE_PRECONDITION_FAILED', `Can't enter ${entering}: ${unmet.join(' ')}`, { details: unmet })
    }
    const actor = actorFor(a.userId)
    const gateType = gateRequired(p, entering)
    if (gateType) {
      const artifactType = gatedArtifact[gateType] ?? null
      const artifactVersion = artifactType ? lockedVersion(p, artifactType) : null
      const gate = seedGate(p, {
        type: gateType,
        artifactType,
        artifactVersion,
        artifactId: artifactType ? nextId() : null,
        createdAt: new Date().toISOString(),
        reason: `Approval required to enter ${entering}.`,
      })
      addEvent(p, {
        type: 'GATE_CREATED',
        category: 'gate',
        summary: `${gateType} gate created`,
        payload: { gateId: gate.id, type: gateType, artifactId: gate.artifactId, projectVersion: p.projectVersion },
        actor,
      })
      return HttpResponse.json<Schemas['AdvanceResultDto']>({ state: stateDto(p), gate })
    }
    moveTo(p, entering, actor, 'forward')
    return HttpResponse.json<Schemas['AdvanceResultDto']>({ state: stateDto(p), gate: null })
  }),

  // FR-12–FR-15: gates, newest first, optional status filter.
  http.get('*/api/projects/:id/gates', ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const status = new URL(request.url).searchParams.get('status')
    return HttpResponse.json<Gate[]>(a.project.gates.filter((g) => !status || g.status === status))
  }),

  // FR-13, FR-14: approve (escalation gates take a decision).
  http.post('*/api/projects/:id/gates/:gateId/approve', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['DecideGateRequest'] | null
    const p = a.project
    const gate = findPendingGate(p, params.gateId)
    if (gate instanceof Response) {
      return gate
    }
    const actor = actorFor(a.userId)

    if (gate.type === 'escalation') {
      const decision = body?.decision ?? null
      if (!decision) {
        return invalidDecision('Escalation gates need a decision: continue, rollback or accept.')
      }
      if (decision === 'accept' && gate.escalationReason === 'step_blocked') {
        return invalidDecision('A blocked step can be continued (retried) or rolled back, not accepted.')
      }
      const target = body?.targetVersion ?? null
      if (decision === 'rollback') {
        if (target === null || target < 1 || target >= p.projectVersion) {
          return invalidDecision('A rollback needs a target version below the current version.')
        }
      } else if (target !== null) {
        return invalidDecision('Only a rollback takes a target version.')
      }
      decide(gate, 'approved', a.userId, body)
      gate.decision = decision
      gate.targetVersion = target
      addEvent(p, {
        type: 'GATE_APPROVED',
        category: 'gate',
        summary: `escalation gate decided: ${decision}`,
        payload: { gateId: gate.id, type: gate.type, decision, targetVersion: target },
        actor,
      })
      if (decision === 'continue' && p.currentPhase === 'execution') {
        // FR-14, FR-33: continue during execution retries the blocked steps with fresh attempts.
        for (const step of p.executionSteps.filter((s) => s.status === 'blocked')) {
          step.status = 'pending'
          step.attempt = 0
          addEvent(p, {
            type: 'STEP_UNBLOCKED',
            category: 'step',
            summary: `Step ${String(step.number)} unblocked`,
            payload: { stepId: step.id, stepNumber: step.number, gateId: gate.id },
            actor,
          })
        }
        p.state.execution.pending += p.state.execution.blocked
        p.state.execution.blocked = 0
        p.executionStarted = !p.executionPaused
      } else if (decision === 'continue') {
        p.state.review.maxCycles += 1
        moveTo(p, 'execution', actor, 'cycle')
      } else if (decision === 'rollback' && target !== null) {
        for (const other of p.gates.filter((g) => g.status === 'pending')) {
          other.status = 'cancelled'
        }
        const fromVersion = p.projectVersion
        moveTo(p, p.phaseAtVersion[target] ?? 'refinement', actor, 'rollback', target)
        addEvent(p, {
          type: 'ROLLBACK_COMPLETED',
          category: 'rollback',
          summary: `Rolled back from version ${String(fromVersion)} to version ${String(target)}`,
          payload: { fromVersion, targetVersion: target },
          actor,
        })
      } else {
        p.state.review.latestDecision = 'release_eligible'
      }
      return HttpResponse.json<Gate>(gate)
    }

    if (body?.decision != null || body?.targetVersion != null) {
      return invalidDecision('Only escalation gates take a decision.')
    }
    decide(gate, 'approved', a.userId, body)
    addEvent(p, {
      type: 'GATE_APPROVED',
      category: 'gate',
      summary: `${gate.type} gate approved`,
      payload: { gateId: gate.id, type: gate.type, decision: null, targetVersion: null },
      actor,
    })
    moveTo(p, gateEntering[gate.type], actor, 'forward')
    return HttpResponse.json<Gate>(gate)
  }),

  // FR-13: reject (not for escalation gates).
  http.post('*/api/projects/:id/gates/:gateId/reject', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['DecideGateRequest'] | null
    const p = a.project
    const gate = findPendingGate(p, params.gateId)
    if (gate instanceof Response) {
      return gate
    }
    if (gate.type === 'escalation' || body?.decision != null || body?.targetVersion != null) {
      return invalidDecision('Escalation gates are decided with approve; other gates take only a comment.')
    }
    decide(gate, 'rejected', a.userId, body)
    const actor = actorFor(a.userId)
    addEvent(p, {
      type: 'GATE_REJECTED',
      category: 'gate',
      summary: `${gate.type} gate rejected`,
      payload: { gateId: gate.id, type: gate.type },
      actor,
    })
    if (gate.type !== 'pre_release') {
      addEvent(p, {
        type: 'RUN_STARTED',
        category: 'run',
        summary: `${gate.type === 'spec_approval' ? 'Spec' : 'Plan'} generator re-running with feedback`,
      })
    }
    return HttpResponse.json<Gate>(gate)
  }),

  // FR-50: activity feed, newest first; `before` is the previous page's `nextBefore`.
  http.get('*/api/projects/:id/activity', ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const q = new URL(request.url).searchParams
    const type = q.get('type')
    const before = q.get('before')
    const limit = Number(q.get('limit') ?? 50)
    if (!Number.isInteger(limit) || limit < 1 || limit > 100) {
      return problem(400, 'VALIDATION', 'One or more fields are invalid.', { errors: { limit: ['Use 1 to 100.'] } })
    }
    const items = [...a.project.events].reverse().filter((e) => !type || e.category === type)
    let start = 0
    if (before) {
      const index = items.findIndex((e) => e.id === before)
      if (index < 0) {
        return problem(400, 'VALIDATION', 'One or more fields are invalid.', { errors: { before: ['Unknown cursor.'] } })
      }
      start = index + 1
    }
    const page = items.slice(start, start + limit)
    const last = page[page.length - 1]
    return HttpResponse.json<Schemas['ActivityPageDto']>({
      items: page,
      nextBefore: start + limit < items.length && last ? last.id : null,
    })
  }),
]
