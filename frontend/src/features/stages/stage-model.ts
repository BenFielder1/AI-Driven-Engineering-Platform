import type { Schemas } from '../../api/client'
import type { RestoreReason } from '../../lib/errors'
import { phaseIndex, phaseLabels } from '../projects/labels'

type Phase = Schemas['Phase']
type ArtifactSummary = Schemas['ArtifactSummaryDto']
type ArtifactType = Schemas['ArtifactType']
type AgentKind = Schemas['AgentKind']
type GateType = Schemas['GateType']

/** Where a stage stands for the release version on screen (FR-55). */
export type StageProgress = 'not_started' | 'current' | 'done'

/**
 * FR-55: a stage is done once the project moved past it (or the version is released), current while the project is
 * in it, and not started before. A past release version (FR-62) is released, so every stage reads as done.
 */
export function stageProgress(
  stage: Phase,
  project: Pick<Schemas['ProjectDto'], 'currentPhase' | 'status'>,
  readOnly: boolean,
): StageProgress {
  if (readOnly || project.status === 'released') {
    return 'done'
  }
  const at = phaseIndex(project.currentPhase)
  const index = phaseIndex(stage)
  return index < at ? 'done' : index === at ? 'current' : 'not_started'
}

/** §4.12: the phase that produces each artifact type (restore is allowed only there, FR-58). */
export const producingPhase: Record<ArtifactType, Phase> = {
  codebase_summary: 'refinement',
  source_items: 'refinement',
  stack_profile: 'analysis',
  project_request: 'refinement',
  technical_specification: 'specification',
  implementation_plan: 'planning',
  optimization_plan: 'review',
}

/** The artifact each stage page shows in full (FR-55). Execution and Release have none of their own. */
export const stageArtifact: Partial<Record<Phase, ArtifactType>> = {
  refinement: 'project_request',
  specification: 'technical_specification',
  planning: 'implementation_plan',
  review: 'optimization_plan',
}

/** The base agent whose runs each stage page lists (SPEC §4.1); Release runs no agent. */
export const stageAgent: Record<Phase, AgentKind | null> = {
  analysis: 'stack_analyst',
  refinement: 'idea_refinement',
  specification: 'spec_generator',
  planning: 'plan_generator',
  execution: 'code_generator',
  review: 'code_review',
  release: null,
}

/** The gates each stage page shows and decides in place (FR-12–FR-15). */
export const stageGates: Record<Phase, readonly GateType[]> = {
  analysis: [],
  refinement: [],
  specification: ['spec_approval'],
  planning: ['plan_approval'],
  execution: ['escalation'],
  review: ['escalation'],
  release: ['pre_release'],
}

/**
 * Why the UI hides restore: an API reason (RESTORE_NOT_ALLOWED), or `not_restorable` for a type AIDE generates and locks
 * itself: the Repository Analyst's codebase summary and an import's source items (the API never restores them).
 */
export type RestoreBlock = RestoreReason | 'not_restorable'

/** FR-70, FR-170: versions AIDE writes auto-locked. They can't be restored or edited into a new draft (rule 9). */
export const isAgentLockedType = (type: ArtifactType): boolean => type === 'codebase_summary' || type === 'source_items'

/**
 * FR-58: why a Writer can't restore `version` of `type`, or `null` when they can. Mirrors the API's checks (which
 * answer RESTORE_NOT_ALLOWED with the same reasons) so the action is hidden with an explanation; the API decides anyway.
 */
export function restoreBlockReason(input: {
  type: ArtifactType
  project: Pick<Schemas['ProjectDto'], 'currentPhase' | 'status' | 'developmentVersion'>
  /** A past release version is on screen (FR-62). */
  readOnly: boolean
  version: Pick<Schemas['ArtifactSummaryDto'], 'integrity' | 'releaseVersion'>
}): RestoreBlock | null {
  const { type, project, readOnly, version } = input
  if (isAgentLockedType(type)) {
    return 'not_restorable'
  }
  if (readOnly || project.developmentVersion === null || version.releaseVersion !== project.developmentVersion) {
    return 'other_release_version'
  }
  // FR-65: a release awaiting its merge is still releasing the version.
  if (project.status === 'releasing' || project.status === 'awaiting_merge') {
    return 'releasing'
  }
  const at = phaseIndex(project.currentPhase)
  const producing = phaseIndex(producingPhase[type])
  if (at < producing) {
    return 'stage_not_reached'
  }
  if (at > producing) {
    return 'stage_completed'
  }
  if (version.integrity === 'corrupted') {
    return 'corrupted'
  }
  return null
}

/** The stage name used in restore copy ("Specification is complete…"). */
export const producingStageLabel = (type: ArtifactType) => phaseLabels[producingPhase[type]]

/** The current version of a type: the highest locked one (FR-16, v1 §4.3). */
export const currentLockedVersion = (history: readonly Pick<ArtifactSummary, 'version' | 'status'>[]) =>
  history.find((a) => a.status === 'locked')?.version ?? null

/** What the stage badge says (FR-55 states: not started · running · awaiting approval · done · read-only). */
export type StageBadge = 'not_started' | 'in_progress' | 'awaiting_approval' | 'done' | 'read_only'

export function stageBadge(progress: StageProgress, readOnly: boolean, awaitingApproval: boolean): StageBadge {
  if (readOnly) {
    return 'read_only'
  }
  if (progress === 'current') {
    return awaitingApproval ? 'awaiting_approval' : 'in_progress'
  }
  return progress
}
