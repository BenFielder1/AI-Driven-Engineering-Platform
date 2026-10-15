import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { canInProject, type ProjectRole } from '../../lib/permissions'
import { actionHref, nextAction, type Can } from './next-action'

type State = Parameters<typeof nextAction>[0]
type ArtifactType = Schemas['ArtifactType']

const member: Pick<Schemas['UserDto'], 'systemRole'> = { systemRole: 'user' }
const as = (role: ProjectRole): Can => (capability) => canInProject(member, role, capability)

function artifacts(spec: Partial<Record<ArtifactType, { latest: Schemas['ArtifactStatus']; locked?: boolean }>> = {}): State['artifacts'] {
  return (['project_request', 'technical_specification', 'implementation_plan', 'optimization_plan'] as const).map((type) => {
    const a = spec[type]
    return {
      type,
      latestVersion: a ? 2 : null,
      latestStatus: a?.latest ?? null,
      currentLockedVersion: a?.locked || a?.latest === 'locked' ? 1 : null,
    }
  })
}

const gate = (type: Schemas['GateType']) => ({ id: `g-${type}`, type, createdAt: '2026-09-01T10:00:00Z', overdue: false })

function state(overrides: Partial<State> = {}): State {
  return {
    phase: 'refinement',
    status: 'active',
    pendingGates: [],
    artifacts: artifacts(),
    execution: { total: 0, pending: 0, inProgress: 0, completed: 0, rejected: 0, blocked: 0 },
    executionStatus: 'idle',
    review: { cycle: 0, maxCycles: 3, latestScore: null, latestDecision: null, driftLevel: null, status: 'idle' },
    latestRelease: null,
    ...overrides,
  }
}

const planningWithGate = state({
  phase: 'planning',
  pendingGates: [gate('plan_approval')],
  artifacts: artifacts({
    project_request: { latest: 'locked' },
    technical_specification: { latest: 'locked' },
    implementation_plan: { latest: 'draft' },
  }),
})

describe('FR-54: next action', () => {
  it('FR54_next_action: planning + pending plan_approval + Owner → "Approve plan" on the pending gates', () => {
    expect(nextAction(planningWithGate, as('owner'))).toMatchObject({
      key: 'decide_gate',
      label: 'Approve plan',
      to: '/projects/$projectId',
      hash: 'pending-gates',
    })
    expect(nextAction(planningWithGate, as('owner'))?.disabledReason).toBeUndefined()
  })

  it('FR54_next_action: a Viewer has no action', () => {
    expect(nextAction(planningWithGate, as('viewer'))).toBeNull()
    expect(nextAction(state(), as('viewer'))).toBeNull()
  })

  it('FR54_next_action: a Contributor sees the Owner-only step disabled with the reason', () => {
    expect(nextAction(planningWithGate, as('contributor'))).toMatchObject({
      label: 'Approve plan',
      disabledReason: 'Waiting for a project Owner to approve the plan.',
    })
  })

  it('FR54_next_action: an Admin who is not a member acts as an Owner', () => {
    const admin: Can = (c) => canInProject({ systemRole: 'admin' }, null, c)
    expect(nextAction(planningWithGate, admin)?.disabledReason).toBeUndefined()
  })

  it('FR54_next_action: escalations come before other gates', () => {
    const s = state({ phase: 'review', pendingGates: [gate('pre_release'), gate('escalation')] })
    expect(nextAction(s, as('owner'))?.label).toBe('Resolve escalation')
  })

  it('FR54_next_action: a spec draft without a gate → "Approve spec" on the specification page', () => {
    const s = state({
      phase: 'specification',
      artifacts: artifacts({ project_request: { latest: 'locked' }, technical_specification: { latest: 'draft' } }),
    })
    expect(nextAction(s, as('owner'))).toMatchObject({
      key: 'approve_artifact',
      label: 'Approve spec',
      to: '/projects/$projectId/specification',
    })
  })

  it('FR54_next_action: refinement → "Refine the idea", then "Advance to Specification" once the request is locked', () => {
    expect(nextAction(state(), as('contributor'))).toMatchObject({ key: 'refine', to: '/projects/$projectId/refinement' })
    const locked = state({ artifacts: artifacts({ project_request: { latest: 'locked' } }) })
    expect(nextAction(locked, as('contributor'))).toMatchObject({ key: 'advance', label: 'Advance to Specification' })
    // FR54_next_action: a request draft is approved on its artifact page (the Refinement page has no approve control).
    const draft = state({ artifacts: artifacts({ project_request: { latest: 'draft' } }) })
    const approve = nextAction(draft, as('owner'))
    expect(approve).toMatchObject({ key: 'approve_artifact', label: 'Approve request', params: { type: 'project_request' } })
    expect(approve && actionHref(approve, 'p1')).toBe('/projects/p1/artifacts/project_request')
  })

  it('FR54_next_action: a locked plan in planning → "Advance to Execution" (the same label as the Overview button)', () => {
    const s = state({
      phase: 'planning',
      artifacts: artifacts({
        project_request: { latest: 'locked' },
        technical_specification: { latest: 'locked' },
        implementation_plan: { latest: 'locked' },
      }),
    })
    expect(nextAction(s, as('owner'))).toMatchObject({ key: 'advance', label: 'Advance to Execution' })
  })

  it('FR54_next_action: execution idle → start, paused → resume, running → nothing, all steps done → start review', () => {
    const steps = { total: 4, pending: 2, inProgress: 0, completed: 2, rejected: 0, blocked: 0 }
    expect(nextAction(state({ phase: 'execution', execution: steps }), as('owner'))).toMatchObject({
      key: 'start_execution',
      to: '/projects/$projectId/execution',
    })
    expect(nextAction(state({ phase: 'execution', execution: steps, executionStatus: 'paused' }), as('owner'))?.label).toBe(
      'Resume execution',
    )
    expect(nextAction(state({ phase: 'execution', execution: steps, executionStatus: 'running' }), as('owner'))).toBeNull()
    const done = { ...steps, pending: 0, completed: 4 }
    expect(nextAction(state({ phase: 'execution', execution: done, executionStatus: 'completed' }), as('owner'))).toMatchObject({
      key: 'advance',
      label: 'Advance to Review',
    })
  })

  it('FR54_next_action: review idle → "Run review"; a failed release → "Retry release" (Owner only)', () => {
    expect(nextAction(state({ phase: 'review' }), as('contributor'))?.label).toBe('Run review')
    const optimisation = nextAction(
      state({ phase: 'review', review: { cycle: 1, maxCycles: 3, latestScore: 0.8, latestDecision: 'review_required', driftLevel: 'low', status: 'completed' }, artifacts: artifacts({ optimization_plan: { latest: 'draft' } }) }),
      as('owner'),
    )
    expect(optimisation && actionHref(optimisation, 'p1')).toBe('/projects/p1/artifacts/optimization_plan')
    const failed = state({
      phase: 'release',
      status: 'releasing',
      latestRelease: { id: 'r1', releaseVersionId: 'rv1', checkpoint: 6, status: 'failed', tag: 'v6', startedAt: '2026-09-01T10:00:00Z' },
    })
    expect(nextAction(failed, as('owner'))).toMatchObject({ key: 'retry_release', to: '/projects/$projectId/release' })
    expect(nextAction(failed, as('contributor'))?.disabledReason).toBe('Waiting for a project Owner to retry the release.')
  })

  it('FR54_next_action: a released project has no action', () => {
    expect(nextAction(state({ phase: 'release', status: 'released' }), as('owner'))).toBeNull()
  })
})
