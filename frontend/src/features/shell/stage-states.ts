import type { Schemas } from '../../api/client'
import { phases } from '../projects/labels'

type Phase = Schemas['Phase']
type ProjectState = Schemas['ProjectStateDto']
type GateType = Schemas['GateType']
type ArtifactType = Schemas['ArtifactType']

/** FR-54: done ✓, current (pulsing dot) or upcoming (muted, still navigable). */
export type StageState = 'done' | 'current' | 'upcoming'

export interface StageStatus {
  stage: Phase
  state: StageState
  /** Pending gates plus drafts awaiting approval that belong to this stage. */
  needsAction: number
}

/** The stage a pending gate belongs to. Escalations come from the running stage (execution or review). */
const gateStage: Record<Exclude<GateType, 'escalation'>, Phase> = {
  spec_approval: 'specification',
  plan_approval: 'planning',
  pre_release: 'release',
}

/** The stage that produces each artifact type (a draft of it is that stage's pending work). */
const artifactStage: Record<ArtifactType, Phase> = {
  project_request: 'refinement',
  technical_specification: 'specification',
  implementation_plan: 'planning',
  optimization_plan: 'review',
}

/** The gate that already stands for a draft of this type (so the draft isn't counted twice). */
const artifactGate: Partial<Record<ArtifactType, GateType>> = {
  technical_specification: 'spec_approval',
  implementation_plan: 'plan_approval',
}

/**
 * FR-54: each lifecycle stage's state and needs-action count, from GET /state (phase, status, pending gates and the
 * latest artifact versions). Stages before the current phase are done; a released project has every stage done.
 */
export function stageStatuses(state: Pick<ProjectState, 'phase' | 'status' | 'pendingGates' | 'artifacts'>): StageStatus[] {
  const currentIndex = phases.indexOf(state.phase)
  const released = state.status === 'released'
  const counts = new Map<Phase, number>()
  const bump = (stage: Phase) => counts.set(stage, (counts.get(stage) ?? 0) + 1)

  const pendingTypes = new Set<GateType>()
  for (const gate of state.pendingGates) {
    pendingTypes.add(gate.type)
    bump(gate.type === 'escalation' ? state.phase : gateStage[gate.type])
  }
  for (const artifact of state.artifacts) {
    const gate = artifactGate[artifact.type]
    if (artifact.latestStatus === 'draft' && !(gate && pendingTypes.has(gate))) {
      bump(artifactStage[artifact.type])
    }
  }

  return phases.map((stage, index) => ({
    stage,
    state: released || index < currentIndex ? 'done' : index === currentIndex ? 'current' : 'upcoming',
    needsAction: counts.get(stage) ?? 0,
  }))
}
