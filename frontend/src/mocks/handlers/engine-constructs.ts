import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { currentUser, nextId, unauthenticated } from './auth'
import { addPipelineGate, pipelinesDb, seedPipelineRun, type StoredPipelineRun } from './pipeline-store'
import { problem } from './problem'
import { access } from './projects'
import type { StoredProject } from './projects'

/*
 * 0.8.0 FR-363, FR-364: the construct fixtures of backend tests/Tevrin.Engine.Tests/Fixtures (fixture-parallel,
 * fixture-qa, fixture-variants, fixture-callsim) as GET /definitions/{hash} returns them, seeded runs of each, the map
 * item drill-down (ListPipelineRunMapItems) and engine-raised gates with payloads (tool_approval, external_action,
 * variant_select).
 */

type Definition = Schemas['PipelineDefinitionDto']
type Stage = Schemas['PipelineStageDefinitionDto']
type Item = Omit<Schemas['PipelineTimelineItemDto'], 'id' | 'at'>
type MapItem = Schemas['PipelineMapItemDto']
type Gate = Schemas['PipelineGateDto']

const stage = (s: Partial<Stage> & Pick<Stage, 'id' | 'title' | 'status'>): Stage => ({
  kind: 'agent',
  agentKey: null,
  inputs: [],
  output: null,
  gate: null,
  transitions: [],
  ...s,
})

const next = (to: string): Stage['transitions'][number] => ({ kind: 'next', to, on: null, title: null, maxIterations: null, maxIterationsSetting: null })

const base = (pipelineId: string, title: string, hashSeed: string): Omit<Definition, 'stages' | 'artifacts' | 'statuses' | 'start'> => ({
  pipelineId,
  title,
  version: 1,
  hash: hashSeed.repeat(64).slice(0, 64),
  packId: 'fixtures',
  runControl: 'engine',
  approvalModes: ['strict', 'advisory', 'autonomous'],
  defaultApprovalMode: 'advisory',
})

/** fixture-parallel (FR-343): a bull branch (two stages) and a bear branch, then Decide. */
export const fixtureParallelDefinition: Definition = {
  ...base('fixture-parallel', 'Fixture parallel', 'b1'),
  start: ['analysts'],
  stages: [
    stage({
      id: 'analysts',
      title: 'Analysts',
      status: 'analysing',
      inputs: ['ticker'],
      transitions: [next('decide')],
      branches: [
        {
          id: '0',
          stages: [
            stage({ id: 'bull', title: 'Bull analyst', status: 'analysing', agentKey: 'bull-analyst', inputs: ['ticker'], output: 'bull_case' }),
            stage({ id: 'bull_review', title: 'Bull risks', status: 'analysing', agentKey: 'bull-reviewer', inputs: ['ticker', 'bull_case'], output: 'bull_risks' }),
          ],
        },
        { id: '1', stages: [stage({ id: 'bear', title: 'Bear analyst', status: 'analysing', agentKey: 'bear-analyst', inputs: ['ticker'], output: 'bear_case' })] },
      ],
    }),
    stage({ id: 'decide', title: 'Decide', status: 'deciding', agentKey: 'portfolio-manager', inputs: ['bull_case', 'bull_risks', 'bear_case'], output: 'decision' }),
  ],
  artifacts: [
    { key: 'ticker', title: 'Ticker', format: 'markdown' },
    { key: 'bull_case', title: 'Bull case', format: 'markdown' },
    { key: 'bull_risks', title: 'Bull risks', format: 'markdown' },
    { key: 'bear_case', title: 'Bear case', format: 'markdown' },
    { key: 'decision', title: 'Decision', format: 'markdown' },
  ],
  statuses: [
    { name: 'analysing', generic: 'active' },
    { name: 'deciding', generic: 'active' },
  ],
}

/** fixture-qa (FR-355): the scorer maps over the transcripts (at most 8 at a time) and the summariser reduces. */
export const fixtureQaDefinition: Definition = {
  ...base('fixture-qa', 'Fixture QA', 'c2'),
  start: ['score'],
  stages: [
    stage({
      id: 'score',
      title: 'Score the transcripts',
      status: 'scoring',
      inputs: ['transcripts'],
      output: 'scores',
      map: {
        over: 'transcripts.items',
        agent: 'qa-scorer',
        handler: null,
        maxParallel: 8,
        maxItems: 1000,
        onItemFailure: 'skip',
        maxFailed: '5%',
        reduceAgent: 'qa-summariser',
        reduceHandler: null,
      },
    }),
  ],
  artifacts: [
    { key: 'transcripts', title: 'Transcripts', format: 'json' },
    { key: 'scores', title: 'Scores', format: 'json' },
  ],
  statuses: [{ name: 'scoring', generic: 'active' }],
}

/** fixture-variants (FR-349): three headline variants picked at a variant_select gate, then two judged taglines. */
export const fixtureVariantsDefinition: Definition = {
  ...base('fixture-variants', 'Fixture variants', 'd3'),
  start: ['headline'],
  stages: [
    stage({
      id: 'headline',
      title: 'Headline',
      status: 'drafting',
      agentKey: 'copywriter',
      inputs: ['product'],
      output: 'headline',
      transitions: [next('tagline')],
      variants: { count: 3, selectAgent: null, labels: ['v1', 'v2', 'v3'] },
    }),
    stage({
      id: 'tagline',
      title: 'Tagline',
      status: 'drafting',
      agentKey: 'tagliner',
      inputs: ['product', 'headline'],
      output: 'tagline',
      variants: { count: 2, selectAgent: 'tagline-judge', labels: ['v1', 'v2'] },
    }),
  ],
  artifacts: [
    { key: 'product', title: 'Product', format: 'markdown' },
    { key: 'headline', title: 'Headline', format: 'markdown' },
    { key: 'tagline', title: 'Tagline', format: 'markdown' },
  ],
  statuses: [{ name: 'drafting', generic: 'active' }],
}

/** fixture-callsim (FR-356): a customer simulator and a support simulator take turns; the transcript is the output. */
export const fixtureCallsimDefinition: Definition = {
  ...base('fixture-callsim', 'Fixture call simulation', 'e4'),
  start: ['call'],
  stages: [
    stage({
      id: 'call',
      title: 'Simulate the call',
      status: 'calling',
      inputs: ['scenario'],
      output: 'transcript',
      conversation: {
        participants: [
          { agent: 'customer-sim', role: 'customer' },
          { agent: 'support-sim', role: 'agent' },
        ],
        maxTurns: 6,
      },
    }),
    // FR-354: the follow-up stage, run 1 and 7 days after the run completes (never reached by the main flow).
    stage({ id: 'call_review', title: 'Review the call', status: 'calling', agentKey: 'call-reviewer', inputs: ['transcript'], output: 'call_review' }),
  ],
  artifacts: [
    { key: 'scenario', title: 'Scenario', format: 'markdown' },
    { key: 'transcript', title: 'Transcript', format: 'json' },
    { key: 'call_review', title: 'Call review', format: 'markdown' },
  ],
  statuses: [{ name: 'calling', generic: 'active' }],
  followUps: [{ stage: 'call_review', after: ['1d', '7d'] }],
}

export const constructDefinitions: readonly Definition[] = [
  fixtureParallelDefinition,
  fixtureQaDefinition,
  fixtureVariantsDefinition,
  fixtureCallsimDefinition,
]

/** Map items by map stage execution id (index order). Ids are fresh per seed, so stale entries never match. */
const mapItemsByExecution = new Map<string, { runId: string; items: MapItem[] }>()

const agentRun = (stageId: string, agentKey: string, extra: Partial<Item> = {}): Item => ({
  kind: 'agent_run',
  stageId,
  agentKey,
  agentRunStatus: 'succeeded',
  model: 'claude-opus-5-5',
  promptVersion: '1',
  inputTokens: 400,
  outputTokens: 200,
  cost: 0.01,
  durationMs: 4_000,
  latencyMs: 500,
  error: null,
  input: `[user]\nDo the ${stageId} work.`,
  inputTruncated: false,
  output: `Output of ${agentKey}.`,
  outputTruncated: false,
  ...extra,
})

const entered = (stageId: string, extra: Partial<Item> = {}): Item => ({
  kind: 'stage_entered',
  stageId,
  iteration: 1,
  checkpoint: 1,
  exitedAt: null,
  outcome: null,
  ...extra,
})

const DONE = '2026-11-01T12:00:00.000Z'

function stageOf(stored: StoredPipelineRun, id: string) {
  const found = stored.stages.find((s) => s.id === id)
  if (!found) {
    throw new Error(`No stage ${id}`)
  }
  return found
}

/** Sets construct labels (variant, item index) on a seeded run's artifact versions, oldest first. */
function labelVersions(stored: StoredPipelineRun, key: string, labels: { variant?: string | null; itemIndex?: number | null }[]) {
  stored.artifacts.get(key)?.forEach((v, i) => {
    const l = labels[i]
    if (l) {
      v.summary.variant = l.variant ?? null
      v.summary.itemIndex = l.itemIndex ?? null
    }
  })
}

/** fixture-parallel, in the Analysts group: the bull branch on its second stage, the bear branch done. */
export function seedParallelRun(project: StoredProject): StoredPipelineRun {
  return seedPipelineRun(project, {
    definition: fixtureParallelDefinition,
    current: 'analysts',
    packStatus: 'analysing',
    completedStages: [],
    timeline: [
      entered('analysts'),
      entered('bull', { branch: '0', exitedAt: DONE, outcome: 'completed' }),
      entered('bear', { branch: '1', exitedAt: DONE, outcome: 'completed' }),
      agentRun('bull', 'bull-analyst'),
      agentRun('bear', 'bear-analyst'),
      entered('bull_review', { branch: '0' }),
    ],
    artifacts: [
      { key: 'ticker', versions: [{ status: 'locked', content: 'ACME' }] },
      { key: 'bull_case', versions: [{ status: 'locked', content: 'Growth is strong.' }] },
      { key: 'bear_case', versions: [{ status: 'locked', content: 'Margins are thin.' }] },
    ],
  })
}

/**
 * fixture-qa, mapping over `total` transcripts: `failed` failed, the next `open` still open, the rest completed. The
 * item drill-down pages them (ListPipelineRunMapItems).
 */
export function seedQaRun(
  project: StoredProject,
  { total = 200, failed = 3, open = 47 }: { total?: number; failed?: number; open?: number } = {},
): StoredPipelineRun {
  const stored = seedPipelineRun(project, {
    definition: fixtureQaDefinition,
    current: 'score',
    packStatus: 'scoring',
    completedStages: [],
    timeline: [entered('score'), agentRun('score', 'qa-scorer', { itemIndex: 0 })],
    artifacts: [{ key: 'transcripts', versions: [{ status: 'locked', content: '{"items":[]}' }] }],
  })
  const executionId = stored.timeline[0]?.id ?? nextId()
  const items: MapItem[] = Array.from({ length: total }, (_, i) => {
    const status: MapItem['status'] = i < failed ? 'failed' : i < failed + open ? 'open' : 'completed'
    return {
      executionId: nextId(),
      itemIndex: i,
      status,
      enteredAt: stored.run.startedAt,
      exitedAt: status === 'open' ? null : DONE,
      agentRunId: nextId(),
      agentRunStatus: status === 'failed' ? 'failed' : status === 'open' ? 'running' : 'succeeded',
      error: status === 'failed' ? `The transcript of item ${String(i)} was empty.` : null,
      artifactId: null,
      artifactKey: status === 'completed' ? 'scores' : null,
      artifactVersion: status === 'completed' ? 1 : null,
    }
  })
  mapItemsByExecution.set(executionId, { runId: stored.run.id, items })
  stageOf(stored, 'score').map = { executionId, total, completed: total - failed - open, failed }
  return stored
}

/** fixture-variants: three headline variants (v2 chosen and promoted), now in Tagline. */
export function seedVariantsRun(project: StoredProject): StoredPipelineRun {
  const stored = seedPipelineRun(project, {
    definition: fixtureVariantsDefinition,
    current: 'tagline',
    packStatus: 'drafting',
    completedStages: ['headline'],
    timeline: [
      entered('headline', { exitedAt: DONE, outcome: 'completed' }),
      entered('headline', { branch: 'v1', variant: 'v1', exitedAt: DONE, outcome: 'completed' }),
      agentRun('headline', 'copywriter', { output: 'Fast and fresh' }),
      { kind: 'artifact_version', artifactKey: 'headline', artifactVersion: 1, artifactStatus: 'draft', variant: 'v1', contentHash: 'a'.repeat(64) },
      entered('tagline'),
    ],
    artifacts: [
      { key: 'product', versions: [{ status: 'locked', content: 'A juice bar.' }] },
      {
        key: 'headline',
        versions: [
          { status: 'superseded', content: 'Fast and fresh' },
          { status: 'superseded', content: 'Squeezed this morning' },
          { status: 'superseded', content: 'Juice, done right' },
          { status: 'locked', content: 'Squeezed this morning' },
        ],
      },
    ],
  })
  labelVersions(stored, 'headline', [{ variant: 'v1' }, { variant: 'v2' }, { variant: 'v3' }, {}])
  stageOf(stored, 'headline').chosenVariant = 'v2'
  return stored
}

/** The transcript fixture-callsim stores as its output (FR-356). */
export const callsimTurns = [
  { role: 'customer', agentKey: 'customer-sim', text: 'My order never arrived.', agentRunId: 'turn-1' },
  { role: 'agent', agentKey: 'support-sim', text: 'Sorry about that. I have sent a replacement.', agentRunId: 'turn-2' },
  { role: 'customer', agentKey: 'customer-sim', text: 'Thanks, goodbye.', agentRunId: 'turn-3' },
]

/** fixture-callsim, completed: three turns and the transcript; its follow-ups due 1 and 7 days later (the first ran). */
export function seedCallsimRun(project: StoredProject): StoredPipelineRun {
  const stored = seedPipelineRun(project, {
    definition: fixtureCallsimDefinition,
    current: 'call',
    packStatus: 'calling',
    status: 'completed',
    completedStages: [],
    timeline: [
      entered('call', { exitedAt: DONE, outcome: 'completed' }),
      ...callsimTurns.map((t, i) => agentRun('call', t.agentKey, { turnIndex: i + 1, turnRole: t.role, output: t.text })),
      { kind: 'artifact_version', artifactKey: 'transcript', artifactVersion: 1, artifactStatus: 'locked', contentHash: 'b'.repeat(64) },
    ],
    artifacts: [
      { key: 'scenario', versions: [{ status: 'locked', content: 'A lost parcel.' }] },
      { key: 'transcript', versions: [{ status: 'locked', content: JSON.stringify({ turns: callsimTurns }) }] },
    ],
  })
  stored.run.completedAt = DONE
  stored.run.followUps = [
    {
      stage: 'call_review',
      label: 'followup:1d',
      dueAt: '2026-11-02T12:00:00.000Z',
      stageExecutionId: nextId(),
      enteredAt: '2026-11-02T12:00:05.000Z',
      exitedAt: '2026-11-02T12:01:00.000Z',
      outcome: 'completed',
    },
    { stage: 'call_review', label: 'followup:7d', dueAt: '2026-11-08T12:00:00.000Z', stageExecutionId: null, enteredAt: null, exitedAt: null, outcome: null },
  ]
  return stored
}

/** Adds timeline entries to a run (ids and times after its last entry). */
export function addTimelineItems(stored: StoredPipelineRun, items: Item[]) {
  const last = stored.timeline.at(-1)
  const at = last ? Date.parse(last.at) : Date.parse(stored.run.startedAt)
  items.forEach((item, i) => {
    stored.timeline.push({ ...item, id: nextId(), at: new Date(at + (i + 1) * 1000).toISOString() })
  })
}

/** FR-359: a pending tool_approval gate (engine-raised: no stage declares its type) for a destructive tool. */
export function addToolApprovalGate(stored: StoredPipelineRun, payload: Partial<NonNullable<Gate['payload']>> = {}): Gate {
  return addPipelineGate(stored, {
    type: 'tool_approval',
    title: 'Tool approval',
    payload: {
      kind: 'tool_call',
      tool: 'restart-pod',
      tier: 'destructive',
      arguments: '{"namespace":"payments","pod":"api-7f9c","token":"[redacted]"}',
      reason: 'The pod is crash-looping; a restart clears the stuck connection pool.',
      agentRunId: nextId(),
      ...payload,
    },
  })
}

/** FR-360: a pending external_action gate for an external handler, with the exact payload it sends. */
export function addExternalActionGate(stored: StoredPipelineRun, payload = '{"channel":"#ops","text":"Restarting api-7f9c"}'): Gate {
  return addPipelineGate(stored, {
    type: 'external_action',
    title: 'External action',
    payload: { kind: 'external_handler', handler: 'post-message', stage: stored.run.currentStage, payload },
  })
}

/** FR-349: a pending variant_select gate offering a run's variants of `key` (the versions labelled v1…). */
export function addVariantSelectGate(stored: StoredPipelineRun, key = 'headline', stageId = 'headline'): Gate {
  const versions = stored.artifacts.get(key) ?? []
  return addPipelineGate(stored, {
    type: 'variant_select',
    title: 'Pick a headline',
    stageId,
    payload: {
      kind: 'variant_select',
      stage: stageId,
      variants: versions
        .filter((v) => v.summary.variant)
        .map((v) => ({ label: v.summary.variant ?? '', artifactId: v.summary.id, artifactKey: key, artifactVersion: v.summary.version })),
      choice: null,
    },
  })
}

const mapItemStatuses: readonly string[] = ['open', 'completed', 'failed']

/** Cursor paging as the API does it for map items (limit 1–200, default 50; the cursor here is the next index). */
function mapItemsPage(request: Request, items: readonly MapItem[], progress: Schemas['PipelineMapProgressDto']): Response {
  const q = new URL(request.url).searchParams
  const limit = q.get('limit') === null ? 50 : Number(q.get('limit'))
  const start = q.get('cursor') === null ? 0 : Number(q.get('cursor'))
  const status = q.get('status')
  if (!Number.isInteger(limit) || limit < 1 || limit > 200 || !Number.isInteger(start) || start < 0 || (status !== null && !mapItemStatuses.includes(status))) {
    return problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors: { limit: ['limit must be between 1 and 200.'] } })
  }
  const filtered = status === null ? items : items.filter((i) => i.status === status)
  return HttpResponse.json<Schemas['PipelineMapItemPageDto']>({
    progress,
    items: filtered.slice(start, start + limit),
    next: start + limit < filtered.length ? String(start + limit) : null,
  })
}

export const engineConstructHandlers = [
  // The construct fixtures' definitions; other hashes fall through to the pipeline handlers.
  http.get('*/api/pipelines/:pipelineId/definitions/:hash', ({ params }) => {
    const def = constructDefinitions.find((d) => d.pipelineId === params.pipelineId && d.hash === params.hash)
    if (!def) {
      return undefined
    }
    return currentUser() ? HttpResponse.json<Definition>(def) : unauthenticated()
  }),
  // FR-363: a map execution's items, paged (project viewer; 404 for an unknown run or execution).
  http.get('*/api/pipeline-runs/:runId/stage-executions/:executionId/items', ({ request, params }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const stored = pipelinesDb.runs.find((r) => r.run.id === params.runId)
    const map = mapItemsByExecution.get(String(params.executionId))
    if (!stored || access(stored.run.projectId) instanceof Response || map?.runId !== stored.run.id) {
      return problem(404, 'NOT_FOUND', 'The map stage execution was not found.')
    }
    const progress = stored.stages.find((s) => s.map?.executionId === params.executionId)?.map ?? {
      executionId: String(params.executionId),
      total: map.items.length,
      completed: map.items.filter((i) => i.status === 'completed').length,
      failed: map.items.filter((i) => i.status === 'failed').length,
    }
    return mapItemsPage(request, map.items, progress)
  }),
]
