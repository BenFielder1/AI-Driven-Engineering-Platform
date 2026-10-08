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

export const gateTypeLabels: Record<Schemas['GateType'], string> = {
  spec_approval: 'Specification approval',
  plan_approval: 'Plan approval',
  pre_release: 'Release approval',
  escalation: 'Escalation',
}

export const artifactTypeLabels: Record<Schemas['ArtifactType'], string> = {
  project_request: 'Project request',
  technical_specification: 'Technical specification',
  implementation_plan: 'Implementation plan',
  optimization_plan: 'Optimisation plan',
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

const numberFormat = new Intl.NumberFormat()
export const formatNumber = (n: number) => numberFormat.format(n)
