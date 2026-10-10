import type { Schemas } from '../../api/client'

/** User-facing names for the admin pages (FR-51, FR-3). */

export const eventOutcomes = ['ok', 'failure'] as const satisfies readonly Schemas['EventOutcome'][]

export const eventOutcomeLabels: Record<Schemas['EventOutcome'], string> = {
  ok: 'OK',
  failure: 'Failure',
}

export const runStatuses = ['queued', 'running', 'succeeded', 'failed', 'cancelled'] as const satisfies
  readonly Schemas['AgentRunStatus'][]

export const llmProviders = ['anthropic', 'openai', 'ollama', 'fake'] as const satisfies readonly Schemas['LlmProvider'][]

/** Job types of the work queue (backend Aide.Domain/Jobs/JobTypes.cs). */
export const jobTypes = [
  'idea_refinement',
  'spec_generation',
  'plan_generation',
  'execution',
  'pull_request',
  'review',
  'release',
  'notification',
] as const

export type JobType = (typeof jobTypes)[number]

export const jobTypeLabels: Record<JobType, string> = {
  idea_refinement: 'Idea refinement',
  spec_generation: 'Specification generation',
  plan_generation: 'Plan generation',
  execution: 'Execution',
  pull_request: 'Pull request',
  review: 'Review',
  release: 'Release',
  notification: 'Notification',
}

/** A job type's label, or the raw type for one this UI doesn't know yet. */
export function jobTypeLabel(type: string): string {
  return (jobTypes as readonly string[]).includes(type) ? jobTypeLabels[type as JobType] : type
}

export const systemRoleLabels: Record<Schemas['SystemRole'], string> = {
  admin: 'Admin',
  user: 'User',
}

export const deadLetterStatusLabels: Partial<Record<Schemas['JobStatus'], string>> = {
  dead: 'Dead',
  retried: 'Retried',
}
