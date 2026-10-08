import type { Schemas } from '../../api/client'
import type { ProjectRole } from '../../lib/permissions'

/** User-facing names for the contract's enums used by the projects feature. */

export const phases = ['refinement', 'specification', 'planning', 'execution', 'review', 'release'] as const satisfies
  readonly Schemas['Phase'][]

export const phaseLabels: Record<Schemas['Phase'], string> = {
  refinement: 'Refinement',
  specification: 'Specification',
  planning: 'Planning',
  execution: 'Execution',
  review: 'Review',
  release: 'Release',
}

export const statuses = ['active', 'building', 'reviewing', 'releasing', 'released', 'rolled_back'] as const satisfies
  readonly Schemas['ProjectStatus'][]

export const statusLabels: Record<Schemas['ProjectStatus'], string> = {
  active: 'Active',
  building: 'Building',
  reviewing: 'Reviewing',
  releasing: 'Releasing',
  released: 'Released',
  rolled_back: 'Rolled back',
}

export const approvalModes = ['strict', 'advisory', 'autonomous'] as const satisfies readonly Schemas['ApprovalMode'][]

export const approvalModeLabels: Record<Schemas['ApprovalMode'], string> = {
  strict: 'Strict',
  advisory: 'Advisory',
  autonomous: 'Autonomous',
}

/** One-line explanations from SPEC §4.2 (gates by approval mode). */
export const approvalModeDescriptions: Record<Schemas['ApprovalMode'], string> = {
  strict: 'An Owner approves the spec, the plan and the release before each phase starts.',
  advisory: 'An Owner approves the spec and the release; execution starts once the plan is approved.',
  autonomous: 'Only the release (and any escalation) needs an Owner; everything else moves on its own.',
}

export const roleLabels: Record<ProjectRole, string> = {
  owner: 'Owner',
  contributor: 'Contributor',
  viewer: 'Viewer',
}

export const projectRoles = ['owner', 'contributor', 'viewer'] as const satisfies readonly ProjectRole[]

export const gateTypes = ['spec_approval', 'plan_approval', 'pre_release', 'escalation'] as const satisfies
  readonly Schemas['GateType'][]

export const gateTypeLabels: Record<Schemas['GateType'], string> = {
  spec_approval: 'Specification approval',
  plan_approval: 'Plan approval',
  pre_release: 'Release approval',
  escalation: 'Escalation',
}

export const gateStatusLabels: Record<Schemas['GateStatus'], string> = {
  pending: 'Pending',
  approved: 'Approved',
  rejected: 'Rejected',
  cancelled: 'Cancelled',
}

/** SPEC §4.5: why review escalated to a human. */
export const escalationReasonLabels: Record<NonNullable<Schemas['EscalationReason']>, string> = {
  low_score: 'Compliance score below 0.75',
  severe_drift: 'Severe drift from the specification',
  max_cycles: 'Maximum review cycles reached',
  no_improvement: 'No improvement since the last review cycle',
}

/** FR-14: the three escalation decisions. */
export const escalationDecisions = ['continue', 'rollback', 'accept'] as const satisfies readonly NonNullable<
  Schemas['EscalationDecision']
>[]

export const escalationDecisionLabels: Record<NonNullable<Schemas['EscalationDecision']>, string> = {
  continue: 'Continue',
  rollback: 'Roll back',
  accept: 'Accept',
}

export const escalationDecisionDescriptions: Record<NonNullable<Schemas['EscalationDecision']>, string> = {
  continue: 'Start another review cycle: execution re-enters for an optimisation plan.',
  rollback: 'Return the project to an earlier version. Later artifacts are invalidated; history is kept.',
  accept: 'Mark the project release-eligible as it is. Any comment is recorded with the decision.',
}

/** FR-50: activity feed filter categories. */
export const eventCategories = [
  'transition',
  'gate',
  'artifact',
  'run',
  'step',
  'review',
  'release',
  'rollback',
  'project',
  'integrity',
] as const satisfies readonly NonNullable<Schemas['EventCategory']>[]

export const eventCategoryLabels: Record<NonNullable<Schemas['EventCategory']>, string> = {
  transition: 'Phase changes',
  gate: 'Gates',
  artifact: 'Artifacts',
  run: 'Agent runs',
  step: 'Execution steps',
  review: 'Reviews',
  release: 'Releases',
  rollback: 'Rollbacks',
  project: 'Project and members',
  integrity: 'Integrity alerts',
}

/** SPEC §4.1: the next phase forward, or null in the last phase. */
export function nextPhase(phase: Schemas['Phase']): Schemas['Phase'] | null {
  return phases[phases.indexOf(phase) + 1] ?? null
}

export const artifactTypes = [
  'project_request',
  'technical_specification',
  'implementation_plan',
  'optimization_plan',
] as const satisfies readonly Schemas['ArtifactType'][]

export const artifactTypeLabels: Record<Schemas['ArtifactType'], string> = {
  project_request: 'Project request',
  technical_specification: 'Technical specification',
  implementation_plan: 'Implementation plan',
  optimization_plan: 'Optimisation plan',
}

/** SPEC §4.3: artifact statuses. `locked` is what an approved version is stored as. */
export const artifactStatusLabels: Record<NonNullable<Schemas['ArtifactStatus']>, string> = {
  draft: 'Draft',
  locked: 'Approved',
  rejected: 'Rejected',
  superseded: 'Superseded',
  invalidated: 'Invalidated',
  corrupted: 'Corrupted',
}

export const gitProviderLabels: Record<Schemas['GitProvider'], string> = {
  github: 'GitHub',
  local: 'Local repository',
}

export const llmProviderLabels: Record<Schemas['LlmProvider'], string> = {
  anthropic: 'Anthropic',
  openai: 'OpenAI',
  ollama: 'Ollama',
  fake: 'Fake (testing)',
}

export const agentKeys = ['ideaRefinement', 'specGenerator', 'planGenerator', 'codeGenerator', 'codeReview'] as const satisfies
  readonly (keyof Schemas['AgentModelsDto'])[]

export const agentLabels: Record<(typeof agentKeys)[number], string> = {
  ideaRefinement: 'Idea refinement',
  specGenerator: 'Spec generator',
  planGenerator: 'Plan generator',
  codeGenerator: 'Code generator',
  codeReview: 'Code review',
}

export const channelTypes = ['email', 'slack', 'teams', 'webhook'] as const satisfies
  readonly Schemas['NotificationChannelType'][]

export const channelTypeLabels: Record<Schemas['NotificationChannelType'], string> = {
  email: 'Email',
  slack: 'Slack',
  teams: 'Microsoft Teams',
  webhook: 'Webhook',
}

export const notificationEvents = [
  'gate_required',
  'gate_overdue',
  'artifact_ready',
  'step_blocked',
  'run_failed',
  'budget_exceeded',
  'review_completed',
  'release_completed',
  'release_failed',
  'rollback',
] as const satisfies readonly Schemas['NotificationEvent'][]

export const notificationEventLabels: Record<Schemas['NotificationEvent'], string> = {
  gate_required: 'Gate needs a decision',
  gate_overdue: 'Gate overdue',
  artifact_ready: 'Artifact ready',
  step_blocked: 'Step blocked',
  run_failed: 'Agent run failed',
  budget_exceeded: 'Budget exceeded',
  review_completed: 'Review completed',
  release_completed: 'Release completed',
  release_failed: 'Release failed',
  rollback: 'Rollback',
}

const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })
export const formatDateTime = (iso: string) => dateTimeFormat.format(new Date(iso))

const relativeFormat = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
const relativeUnits: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 3600],
  ['month', 30 * 24 * 3600],
  ['week', 7 * 24 * 3600],
  ['day', 24 * 3600],
  ['hour', 3600],
  ['minute', 60],
]

/** "3 hours ago" style copy relative to `now` (pair it with the absolute time in a title). */
export function formatRelativeTime(iso: string, now: number = Date.now()): string {
  const seconds = Math.round((new Date(iso).getTime() - now) / 1000)
  for (const [unit, size] of relativeUnits) {
    if (Math.abs(seconds) >= size) {
      return relativeFormat.format(Math.trunc(seconds / size), unit)
    }
  }
  return 'just now'
}

const numberFormat = new Intl.NumberFormat()
export const formatNumber = (n: number) => numberFormat.format(n)
