import type { Schemas } from '../../api/client'
import type { ProjectCapability } from '../../lib/permissions'
import { nextPhase, phaseLabels } from '../projects/labels'
import type { AppPath } from '../shell/nav-config'

type Phase = Schemas['Phase']
type GateType = Schemas['GateType']
type ArtifactType = Schemas['ArtifactType']
type ProjectState = Schemas['ProjectStateDto']

export type NextActionKey =
  | 'decide_gate'
  | 'approve_artifact'
  | 'refine'
  | 'advance'
  | 'start_execution'
  | 'resume_execution'
  | 'start_review'
  | 'retry_review'
  | 'retry_release'

/**
 * FR-54: the one thing the user can do next on a project, shown as the primary button in the project header and the
 * overview's next-action card. The button links to the page that holds the control (`to` + `hash`); an `advance` runs
 * in place. `disabledReason` is set when the next step belongs to someone else (e.g. a Contributor waiting for an
 * Owner's approval): the button shows disabled with that reason. The API enforces every rule anyway.
 */
export interface NextAction {
  key: NextActionKey
  label: string
  /** One sentence on what happens. */
  description: string
  to: AppPath
  /** Route params besides `projectId` (the artifact type for `/artifacts/$type`). */
  params?: { type: ArtifactType }
  hash?: string
  /** The capability the action needs. */
  capability: ProjectCapability
  disabledReason?: string
}

export type Can = (capability: ProjectCapability) => boolean

const gateAction: Record<GateType, { label: string; noun: string }> = {
  spec_approval: { label: 'Approve spec', noun: 'approve the specification' },
  plan_approval: { label: 'Approve plan', noun: 'approve the plan' },
  pre_release: { label: 'Approve release', noun: 'approve the release' },
  escalation: { label: 'Resolve escalation', noun: 'decide the escalation' },
}

/** The artifact each phase produces (and the approve label for its draft). */
/*
 * Each target page must hold the approve control: the request and the optimisation plan are approved on their
 * artifact pages until the M11 stage pages add approval (the Refinement and Review pages only link to the artifact).
 */
const phaseArtifact: Partial<Record<Phase, { type: ArtifactType; label: string; noun: string; to: AppPath; byType?: boolean }>> = {
  refinement: {
    type: 'project_request',
    label: 'Approve request',
    noun: 'approve the project request',
    to: '/projects/$projectId/artifacts/$type',
    byType: true,
  },
  specification: {
    type: 'technical_specification',
    label: 'Approve spec',
    noun: 'approve the specification',
    to: '/projects/$projectId/specification',
  },
  planning: {
    type: 'implementation_plan',
    label: 'Approve plan',
    noun: 'approve the plan',
    to: '/projects/$projectId/planning',
  },
  review: {
    type: 'optimization_plan',
    label: 'Approve optimisation plan',
    noun: 'approve the optimisation plan',
    to: '/projects/$projectId/artifacts/$type',
    byType: true,
  },
}

/** The gate a draft is already waiting on (so its approval reads as the gate, not the draft). */
const typeGate: Partial<Record<ArtifactType, GateType>> = {
  technical_specification: 'spec_approval',
  implementation_plan: 'plan_approval',
}


/** The concrete href of an action, e.g. `/projects/42/artifacts/project_request#…` without the hash. */
export function actionHref(action: Pick<NextAction, 'to' | 'params'>, projectId: string): string {
  return action.to.replace('$projectId', projectId).replace('$type', action.params?.type ?? '$type')
}

/** Gates are decided first, escalations before the rest (they block everything). */
const gateOrder: readonly GateType[] = ['escalation', 'pre_release', 'plan_approval', 'spec_approval']

function withPermission(action: Omit<NextAction, 'disabledReason'>, can: Can, reason: string): NextAction {
  return can(action.capability) ? action : { ...action, disabledReason: reason }
}

/**
 * FR-54: derives the next action from GET /state and what the user may do (SPEC §2 capability matrix). Returns null
 * when there is nothing for this user to do now (a Viewer, a released project, or agents are working).
 */
export function nextAction(
  state: Pick<ProjectState, 'phase' | 'status' | 'pendingGates' | 'artifacts' | 'execution' | 'executionStatus' | 'review' | 'latestRelease'>,
  can: Can,
): NextAction | null {
  // A Viewer never acts (SPEC §2), and a released project has nothing left in this version.
  if ((!can('advancePhase') && !can('decideGates')) || state.status === 'released') {
    return null
  }

  // 1. A pending gate (FR-12–FR-15): Owners decide on the overview; others wait for an Owner.
  const gate = gateOrder.map((type) => state.pendingGates.find((g) => g.type === type)).find((g) => g !== undefined)
  if (gate) {
    const copy = gateAction[gate.type]
    return withPermission(
      {
        key: 'decide_gate',
        label: copy.label,
        description: `A gate is waiting: an Owner needs to ${copy.noun} before the project moves on.`,
        to: '/projects/$projectId',
        hash: 'pending-gates',
        capability: 'decideGates',
      },
      can,
      `Waiting for a project Owner to ${copy.noun}.`,
    )
  }

  // 2. A draft of this phase's artifact waiting for approval (FR-17).
  const produced = phaseArtifact[state.phase]
  const draft = produced && state.artifacts.find((a) => a.type === produced.type && a.latestStatus === 'draft')
  if (produced && draft && !(typeGate[produced.type] && state.pendingGates.some((g) => g.type === typeGate[produced.type]))) {
    return withPermission(
      {
        key: 'approve_artifact',
        label: produced.label,
        description:
          draft.latestVersion === null
            ? 'A draft is waiting for approval.'
            : `Version ${String(draft.latestVersion)} is a draft waiting for approval.`,
        to: produced.to,
        ...(produced.byType ? { params: { type: produced.type } } : {}),
        capability: 'approveArtifacts',
      },
      can,
      `Waiting for a project Owner to ${produced.noun}.`,
    )
  }

  const locked = (type: ArtifactType) => state.artifacts.some((a) => a.type === type && a.currentLockedVersion !== null)
  const advance = (): NextAction | null => {
    const next = nextPhase(state.phase)
    if (!next) {
      return null
    }
    return withPermission(
      {
        key: 'advance',
        // Same label as the Overview's advance button. Advancing may create a gate instead (approval mode).
        label: `Advance to ${phaseLabels[next]}`,
        description: `This stage is complete: move the project to ${phaseLabels[next]} (the approval mode may ask an Owner first).`,
        to: '/projects/$projectId',
        capability: 'advancePhase',
      },
      can,
      'Only Owners and Contributors can advance the project.',
    )
  }

  // 3. The phase's own next step.
  switch (state.phase) {
    case 'refinement':
      return locked('project_request')
        ? advance()
        : withPermission(
            {
              key: 'refine',
              label: 'Refine the idea',
              description: 'Answer the clarifying questions so the request can be approved.',
              to: '/projects/$projectId/refinement',
              capability: 'startAgents',
            },
            can,
            'Only Owners and Contributors can refine the idea.',
          )
    case 'specification':
      return locked('technical_specification') ? advance() : null
    case 'planning':
      return locked('implementation_plan') ? advance() : null
    case 'execution': {
      const { total, completed } = state.execution
      if (total > 0 && completed === total) {
        return withPermission(
          {
            key: 'advance',
            label: 'Advance to Review',
            description: 'Every step is complete: move the project to Review (the approval mode may ask an Owner first).',
            to: '/projects/$projectId',
            capability: 'advancePhase',
          },
          can,
          'Only Owners and Contributors can advance the project.',
        )
      }
      if (state.executionStatus === 'idle' || state.executionStatus === 'paused') {
        const resume = state.executionStatus === 'paused'
        return withPermission(
          {
            key: resume ? 'resume_execution' : 'start_execution',
            label: resume ? 'Resume execution' : 'Start execution',
            description: resume ? 'Execution is paused.' : 'Run the plan’s steps.',
            to: '/projects/$projectId/execution',
            capability: 'startExecution',
          },
          can,
          'Only Owners and Contributors can run execution.',
        )
      }
      return null
    }
    case 'review':
      if (state.review.status === 'idle' || state.review.status === 'failed') {
        const retry = state.review.status === 'failed'
        return withPermission(
          {
            key: retry ? 'retry_review' : 'start_review',
            label: retry ? 'Retry review' : 'Run review',
            description: retry ? 'The last review failed.' : 'Score the code against the specification.',
            to: '/projects/$projectId/review',
            capability: 'triggerReview',
          },
          can,
          'Only Owners and Contributors can run a review.',
        )
      }
      return null
    case 'release':
      if (state.latestRelease?.status === 'failed') {
        return withPermission(
          {
            key: 'retry_release',
            label: 'Retry release',
            description: 'The last release attempt failed.',
            to: '/projects/$projectId/release',
            capability: 'requestRelease',
          },
          can,
          'Waiting for a project Owner to retry the release.',
        )
      }
      return null
  }
}
