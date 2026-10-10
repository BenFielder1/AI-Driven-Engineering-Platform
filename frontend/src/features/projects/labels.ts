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
  step_blocked: 'An execution step was rejected 4 times and is blocked',
  no_optimization_plan: 'Another cycle was needed but the review proposed no optimisation steps',
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
  continue:
    'Allow one more review cycle: execution re-enters to run the optimisation plan (once it is approved), then review runs again.',
  rollback: 'Return the project to an earlier version. Later artifacts are invalidated; history is kept.',
  accept:
    'Accept the risks and mark the project release-eligible as it is; the release still goes through the pre-release gate. Any comment is recorded with the decision.',
}

/**
 * FR-14, FR-33: the decisions offered for an escalation, with what each does. A blocked execution step (`step_blocked`)
 * can only be retried (continue) or rolled back: accept has no effect during execution, so it isn't offered.
 */
export function escalationOptionsFor(reason: Schemas['EscalationReason'] | undefined) {
  if (reason === 'step_blocked') {
    return [
      {
        value: 'continue' as const,
        label: escalationDecisionLabels.continue,
        description:
          'Retry the blocked step: it goes back to pending with fresh attempts (its violations stay as feedback) and execution carries on unless paused.',
      },
      {
        value: 'rollback' as const,
        label: escalationDecisionLabels.rollback,
        description: 'Return the project to an earlier version instead. Later artifacts and steps are invalidated; history is kept.',
      },
    ]
  }
  if (reason === 'no_optimization_plan') {
    // The review proposed nothing to execute, so continue starts a fresh review rather than re-entering execution.
    return escalationDecisions.map((d) => ({
      value: d,
      label: escalationDecisionLabels[d],
      description:
        d === 'continue'
          ? 'Allow one more review cycle: there is no optimisation plan to execute, so review runs again on the current code.'
          : escalationDecisionDescriptions[d],
    }))
  }
  return escalationDecisions.map((d) => ({
    value: d,
    label: escalationDecisionLabels[d],
    description: escalationDecisionDescriptions[d],
  }))
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

/** FR-30–FR-34: execution step status. */
export const stepStatusLabels: Record<Schemas['StepStatus'], string> = {
  pending: 'Pending',
  in_progress: 'In progress',
  completed: 'Completed',
  rejected: 'Rejected',
  blocked: 'Blocked',
  invalidated: 'Invalidated',
}

/** FR-31, FR-33, FR-37: the execution runner's overall status. */
export const executionRunStatusLabels: Record<Schemas['ExecutionRunStatus'], string> = {
  idle: 'Not running',
  running: 'Running',
  paused: 'Paused',
  completed: 'Completed',
  blocked: 'Blocked',
}

/** §4.6, FR-32: Execution Guardian checks. */
export const guardianCheckLabels: Record<Schemas['GuardianCheck'], string> = {
  single_step: 'Single step',
  file_limit: 'File limit',
  dependency_order: 'Dependency order',
  plan_alignment: 'Plan alignment',
  scope_creep: 'Scope creep',
  secret_scan: 'Secret scan',
}

export const gitProviderLabels: Record<Schemas['GitProvider'], string> = {
  github: 'GitHub',
  local: 'Local repository',
}

/** FR-42, FR-43: a release attempt's outcome. */
export const releaseStatusLabels: Record<Schemas['ReleaseStatus'], string> = {
  in_progress: 'Releasing',
  succeeded: 'Released',
  failed: 'Failed',
}

/** FR-44: why a project version can't be a rollback target. */
export const rollbackBlockLabels: Record<NonNullable<Schemas['RollbackTargetBlock']>, string> = {
  current_version: 'current version',
  release_phase: 'release versions can’t be restored',
}

/** FR-36: how a file differs between the default branch and the project branch. */
export const pullRequestFileStatusLabels: Record<Schemas['PullRequestFileStatus'], string> = {
  added: 'Added',
  modified: 'Modified',
  deleted: 'Deleted',
  renamed: 'Renamed',
  copied: 'Copied',
  changed: 'Changed',
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

/** In-app notification types (FR-47, FR-15): the channel events plus the 24-hour gate reminder. */
export const notificationTypes = [
  'gate_required',
  'gate_reminder',
  'gate_overdue',
  'artifact_ready',
  'step_blocked',
  'run_failed',
  'budget_exceeded',
  'review_completed',
  'release_completed',
  'release_failed',
  'rollback',
] as const satisfies readonly Schemas['NotificationType'][]

export const notificationTypeLabels: Record<Schemas['NotificationType'], string> = {
  ...notificationEventLabels,
  gate_reminder: 'Gate reminder (after 24 hours)',
}

/** FR-48: the decision a one-click approval link carries. */
export const approvalDecisionLabels: Record<Schemas['ApprovalLinkDecision'], string> = {
  approve: 'Approve',
  reject: 'Reject',
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

/** FR-23: the five agents (the contract's `AgentKind`). */
export const agentKinds = [
  'idea_refinement',
  'spec_generator',
  'plan_generator',
  'code_generator',
  'code_review',
] as const satisfies readonly Schemas['AgentKind'][]

export const agentKindLabels: Record<Schemas['AgentKind'], string> = {
  idea_refinement: 'Idea refinement',
  spec_generator: 'Spec generator',
  plan_generator: 'Plan generator',
  code_generator: 'Code generator',
  code_review: 'Code review',
}

/** FR-23, FR-25: agent run statuses. Running runs stream nothing in v1 (SPEC §8, §11 Q3). */
export const runStatusLabels: Record<Schemas['AgentRunStatus'], string> = {
  queued: 'Queued',
  running: 'Running…',
  succeeded: 'Succeeded',
  failed: 'Failed',
  cancelled: 'Cancelled',
}

/** "850 ms", "12.4 s", "3 min 05 s". */
export function formatDuration(ms: number): string {
  if (ms < 1000) {
    return `${String(Math.round(ms))} ms`
  }
  const seconds = ms / 1000
  if (seconds < 60) {
    return `${seconds.toFixed(1)} s`
  }
  const whole = Math.round(seconds)
  return `${String(Math.floor(whole / 60))} min ${String(whole % 60).padStart(2, '0')} s`
}

const costFormat = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 4 })
/** Estimated USD cost of a run (D15: an estimate from configured prices; unpriced models cost $0.00). */
export const formatCost = (usd: number) => costFormat.format(usd)

// ---- Review (FR-38–FR-41, §4.5, §4.7) ---------------------------------------------------------------------------

/** §4.7: compliance categories, in the spec's weight order. */
export const reviewCategories = [
  'feature_coverage',
  'api_contracts',
  'data_model',
  'architecture',
  'security',
  'testing',
] as const satisfies readonly Schemas['ReviewCategory'][]

export const reviewCategoryLabels: Record<Schemas['ReviewCategory'], string> = {
  feature_coverage: 'Feature coverage',
  api_contracts: 'API contracts',
  data_model: 'Data model',
  architecture: 'Architecture',
  security: 'Security',
  testing: 'Testing',
}

export const reviewDecisions = ['release_eligible', 'review_required', 'escalation_required'] as const satisfies readonly NonNullable<
  Schemas['ReviewDecision']
>[]

/** §4.5: the review decision. */
export const reviewDecisionLabels: Record<NonNullable<Schemas['ReviewDecision']>, string> = {
  release_eligible: 'Release eligible',
  review_required: 'Another cycle needed',
  escalation_required: 'Escalated to an Owner',
}

/** FR-39: what each decision leads to, in plain words. */
export const reviewDecisionDescriptions: Record<NonNullable<Schemas['ReviewDecision']>, string> = {
  release_eligible: 'The score meets the threshold. A pre-release gate asks an Owner to approve the release.',
  review_required:
    'The score is below the threshold. Execution re-enters with the optimisation plan once it is approved, then review runs again.',
  escalation_required: 'Review needs a human decision: an Owner continues, rolls back or accepts the risks in the escalation gate.',
}

/** §4.5: why a review escalated, as a sentence about the report (the gate labels are `escalationReasonLabels`). */
export const reviewEscalationReasonDescriptions: Record<
  'low_score' | 'severe_drift' | 'max_cycles' | 'no_improvement' | 'no_optimization_plan',
  string
> = {
  low_score: 'The compliance score is below 75%, too low for another automatic cycle.',
  severe_drift: 'The code has drifted severely from the specification.',
  max_cycles: 'Every allowed review cycle has been used without reaching the threshold.',
  no_improvement: 'The score did not improve on the previous review cycle.',
  no_optimization_plan:
    'The score is below the threshold, but even after a corrective re-run the review proposed no optimisation steps to execute.',
}

export const driftLevels = ['none', 'low', 'moderate', 'severe'] as const satisfies readonly NonNullable<Schemas['DriftLevel']>[]

/** §4.7: drift from the specification. */
export const driftLevelLabels: Record<NonNullable<Schemas['DriftLevel']>, string> = {
  none: 'No drift',
  low: 'Low drift',
  moderate: 'Moderate drift',
  severe: 'Severe drift',
}

export const findingSeverities = ['critical', 'major', 'minor'] as const satisfies readonly Schemas['Severity'][]

/** §4.7: review finding / drift area severity. */
export const findingSeverityLabels: Record<Schemas['Severity'], string> = {
  critical: 'Critical',
  major: 'Major',
  minor: 'Minor',
}

/** §4.7: where a violation came from. */
export const findingSourceLabels: Record<Schemas['FindingSource'], string> = {
  deterministic: 'Deterministic check',
  agent: 'Code review agent',
}

/** FR-38: the review job's state. */
export const reviewRunStatusLabels: Record<Schemas['ReviewRunStatus'], string> = {
  idle: 'Not running',
  running: 'Running',
  completed: 'Completed',
  failed: 'Failed',
}

const percentFormat = new Intl.NumberFormat(undefined, { style: 'percent', maximumFractionDigits: 1 })
/** A 0–1 score as a percentage ("82.5%"). */
export const formatPercent = (score: number) => percentFormat.format(score)
