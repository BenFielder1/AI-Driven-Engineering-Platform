import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { nextId } from './auth'
import { problem } from './problem'
import { access, type StoredProject } from './projects'

type Run = Schemas['AgentRunDetailDto']
type AgentKind = Schemas['AgentKind']

const agents: readonly AgentKind[] = ['idea_refinement', 'spec_generator', 'plan_generator', 'code_generator', 'code_review']

/** Mirrors RunEndpoints.MaxOutputLength. */
export const maxRunOutputLength = 200_000

/** Mirrors ProjectLimits.MaxPageLimit. */
const maxPageLimit = 100

/** Prompt template version per agent (the `version:` front matter of Agents/Prompts/*.md). */
const promptVersions: Record<AgentKind, string> = {
  idea_refinement: '1',
  spec_generator: '1',
  plan_generator: '1',
  code_generator: '1',
  code_review: '1',
}

/** Which phase each agent runs in (SPEC §4.1). */
const phaseOf: Record<AgentKind, Schemas['Phase']> = {
  idea_refinement: 'refinement',
  spec_generator: 'specification',
  plan_generator: 'planning',
  code_generator: 'execution',
  code_review: 'review',
}

let runClock = Date.parse('2026-09-01T10:00:00Z')

/**
 * Seeds an agent run (a succeeded Spec Generator run on claude-opus-5-5 unless overridden). Each seeded run is a
 * minute newer than the previous one, so the last seeded is listed first.
 */
export function seedRun(project: StoredProject, overrides: Partial<Run> = {}): Run {
  runClock += 60_000
  const agent = overrides.agent ?? 'spec_generator'
  const createdAt = new Date(runClock).toISOString()
  const status = overrides.status ?? 'succeeded'
  const finished = status === 'succeeded' || status === 'failed' || status === 'cancelled'
  const run: Run = {
    id: nextId(),
    agent,
    phase: phaseOf[agent],
    stepId: null,
    provider: 'anthropic',
    model: 'claude-opus-5-5',
    promptVersion: promptVersions[agent],
    inputArtifacts: agent === 'spec_generator' ? [{ type: 'project_request', version: 2 }] : [],
    userFeedback: null,
    status,
    attempts: 1,
    inputTokens: 3_412,
    outputTokens: 9_876,
    costEstimate: 0.211,
    durationMs: 48_250,
    error: null,
    createdAt,
    completedAt: finished ? new Date(runClock + 48_250).toISOString() : null,
    output: status === 'succeeded' ? '# Inventory tracker: technical specification\n\n## 1. Overview\nTrack stock levels.' : null,
    outputTruncated: false,
    ...overrides,
  }
  project.runs.push(run)
  return run
}

/** The list item omits the output (RunEndpoints.ListRuns doesn't load it). */
function toListDto(run: Run): Schemas['AgentRunDto'] {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const { output, outputTruncated, ...dto } = run
  return dto
}

function positiveInt(value: string | null, fallback: number): number | null {
  if (value === null) {
    return fallback
  }
  const n = Number(value)
  return Number.isInteger(n) && n >= 1 ? n : null
}

export const runHandlers = [
  // FR-23: newest first, paged, optionally filtered by agent (members).
  http.get('*/api/projects/:id/runs', ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const q = new URL(request.url).searchParams
    const agent = q.get('agent')
    const page = positiveInt(q.get('page'), 1)
    const limit = positiveInt(q.get('limit'), 20)
    const errors: Record<string, string[]> = {}
    if (agent !== null && !agents.includes(agent as AgentKind)) {
      errors.agent = [`agent must be one of ${agents.join(', ')}.`]
    }
    if (page === null) {
      errors.page = ['page must be at least 1.']
    }
    if (limit === null || limit > maxPageLimit) {
      errors.limit = [`limit must be between 1 and ${String(maxPageLimit)}.`]
    }
    if (page === null || limit === null || Object.keys(errors).length > 0) {
      return problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors })
    }
    const items = a.project.runs
      .filter((r) => agent === null || r.agent === agent)
      .sort((x, y) => y.createdAt.localeCompare(x.createdAt) || y.id.localeCompare(x.id))
    return HttpResponse.json<Schemas['PagedResultOfAgentRunDto']>({
      items: items.slice((page - 1) * limit, page * limit).map(toListDto),
      page,
      limit,
      total: items.length,
    })
  }),

  // FR-23: one run with its output; 404 for a run of another project.
  http.get('*/api/projects/:id/runs/:runId', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const run = a.project.runs.find((r) => r.id === params.runId)
    if (!run) {
      return problem(404, 'NOT_FOUND', 'The agent run was not found.')
    }
    const text = run.output
    const truncated = text !== null && text.length > maxRunOutputLength
    return HttpResponse.json<Run>({
      ...run,
      output: truncated ? text.slice(0, maxRunOutputLength) : text,
      outputTruncated: truncated || run.outputTruncated,
    })
  }),
]
