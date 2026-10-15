import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { stageStatuses } from './stage-states'

type State = Parameters<typeof stageStatuses>[0]

const artifacts = (overrides: Partial<Record<Schemas['ArtifactType'], Schemas['ArtifactStatus']>> = {}): State['artifacts'] =>
  (['project_request', 'technical_specification', 'implementation_plan'] as const).map((type) => ({
    type,
    latestVersion: overrides[type] ? 1 : null,
    latestStatus: overrides[type] ?? null,
    currentLockedVersion: null,
  }))

const gate = (type: Schemas['GateType']) => ({ id: `g-${type}`, type, createdAt: '2026-09-01T10:00:00Z', overdue: false })

const byStage = (state: State) => Object.fromEntries(stageStatuses(state).map((s) => [s.stage, s]))

describe('FR-54: lifecycle stage states', () => {
  it('FR54_stage_states: planning with a pending plan_approval gate → refinement and specification done, planning current with 1, later upcoming', () => {
    const s = byStage({
      phase: 'planning',
      status: 'active',
      pendingGates: [gate('plan_approval')],
      // The plan draft is what the gate approves: it isn't counted twice.
      artifacts: artifacts({ project_request: 'locked', technical_specification: 'locked', implementation_plan: 'draft' }),
    })
    expect(s.refinement).toMatchObject({ state: 'done', needsAction: 0 })
    expect(s.specification).toMatchObject({ state: 'done', needsAction: 0 })
    expect(s.planning).toMatchObject({ state: 'current', needsAction: 1 })
    expect(s.execution).toMatchObject({ state: 'upcoming', needsAction: 0 })
    expect(s.review).toMatchObject({ state: 'upcoming', needsAction: 0 })
    expect(s.release).toMatchObject({ state: 'upcoming', needsAction: 0 })
  })

  it('FR54_stage_states: a draft awaiting approval without a gate counts for its stage', () => {
    const s = byStage({ phase: 'refinement', status: 'active', pendingGates: [], artifacts: artifacts({ project_request: 'draft' }) })
    expect(s.refinement).toMatchObject({ state: 'current', needsAction: 1 })
  })

  it('FR54_stage_states: a spec draft counts once there is no spec gate (advisory author approval)', () => {
    const s = byStage({
      phase: 'specification',
      status: 'active',
      pendingGates: [],
      artifacts: artifacts({ project_request: 'locked', technical_specification: 'draft' }),
    })
    expect(s.specification).toMatchObject({ state: 'current', needsAction: 1 })
  })

  it('FR54_stage_states: escalations count for the running stage; the pre-release gate for Release', () => {
    expect(byStage({ phase: 'execution', status: 'building', pendingGates: [gate('escalation')], artifacts: [] }).execution).toMatchObject({
      state: 'current',
      needsAction: 1,
    })
    expect(byStage({ phase: 'release', status: 'releasing', pendingGates: [gate('pre_release')], artifacts: [] }).release).toMatchObject({
      state: 'current',
      needsAction: 1,
    })
  })

  it('FR54_stage_states: a released project has every stage done', () => {
    const states = stageStatuses({ phase: 'release', status: 'released', pendingGates: [], artifacts: [] }).map((s) => s.state)
    expect(states).toEqual(['done', 'done', 'done', 'done', 'done', 'done'])
  })
})
