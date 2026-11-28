import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { describeError, validateJson } from '../../engine/schema-form/json-schema'
import { currentUser, nextId, unauthenticated } from './auth'
import { impulseCoachDefinition, pipelineDefinitions, sdlcDefinition } from './pipeline-definitions'
import { pipelinesDb } from './pipeline-store'
import { problem } from './problem'
import { access, type Access } from './projects'

type Definition = Schemas['PipelineDefinitionDto']
type Run = Schemas['PipelineRunDto']
type Feedback = Schemas['AgentFeedbackDto']
type Schedule = Schemas['PipelineScheduleDto']
type Webhook = Schemas['PipelineWebhookDto']

/** The Impulse coaching pack's session_log schema (backend src/Tevrin.Pack.Coach/Schemas/session_log.schema.json). */
export const sessionLogSchema = {
  $schema: 'https://json-schema.org/draft/2020-12/schema',
  title: 'Session log',
  type: 'object',
  required: ['sessions'],
  properties: {
    sessions: {
      type: 'array',
      items: {
        type: 'object',
        required: ['date', 'type'],
        properties: {
          date: { type: 'string', format: 'date' },
          type: { type: 'string', minLength: 1 },
          intensity: { enum: ['easy', 'moderate', 'hard', 'race'] },
          distanceKm: { type: 'number', minimum: 0 },
          durationMin: { type: 'number', minimum: 0 },
          rpe: { type: 'number', minimum: 1, maximum: 10 },
          notes: { type: 'string' },
        },
      },
    },
  },
}

/**
 * FR-362: the coach pipeline as the 0.8.0 definition DTO returns it (impulse-coach.pipeline.yaml): engine-controlled,
 * strict or advisory (strict by default), manual, webhook (`session_log`) and schedule triggers, and `session_log`
 * declared `input: true` with its schema.
 */
export const coachRunControlDefinition: Definition = {
  ...impulseCoachDefinition,
  runControl: 'engine',
  approvalModes: ['strict', 'advisory'],
  defaultApprovalMode: 'strict',
  triggers: [
    { kind: 'manual', input: null },
    { kind: 'webhook', input: 'session_log' },
    { kind: 'schedule', input: null },
  ],
  artifacts: impulseCoachDefinition.artifacts.map((a) => (a.key === 'session_log' ? { ...a, input: true, schema: sessionLogSchema } : a)),
}

/** A test pipeline whose one input has a flat object schema (the generated form, FR-362). */
export const intakeDefinition: Definition = {
  pipelineId: 'fixture-intake',
  title: 'Fixture intake',
  version: 1,
  hash: 'f1f2f3f4f5f6f7f8f9fafbfcfdfeff00112233445566778899aabbccddeeff00',
  packId: 'fixtures',
  start: ['triage'],
  stages: [
    {
      id: 'triage',
      title: 'Triage',
      kind: 'agent',
      status: 'triaging',
      agentKey: 'triager',
      inputs: ['request'],
      output: 'triage',
      gate: null,
      transitions: [],
    },
  ],
  artifacts: [
    {
      key: 'request',
      title: 'Request',
      format: 'json',
      scope: 'run',
      input: true,
      schema: {
        type: 'object',
        required: ['summary', 'priority'],
        properties: {
          summary: { type: 'string', title: 'Summary', minLength: 1, maxLength: 200 },
          priority: { enum: ['low', 'normal', 'high'], title: 'Priority' },
          due: { type: 'string', format: 'date', title: 'Due date' },
          estimate: { type: 'number', minimum: 0, title: 'Estimate (hours)' },
          urgent: { type: 'boolean', title: 'Urgent' },
        },
      },
    },
    { key: 'triage', title: 'Triage', format: 'json', scope: 'run' },
  ],
  statuses: [
    { name: 'triaging', generic: 'active' },
    { name: 'done', generic: 'completed' },
  ],
  runControl: 'engine',
  approvalModes: [],
  defaultApprovalMode: null,
  triggers: [{ kind: 'manual', input: null }],
}

export const runControlDb = {
  /** Definitions served by `runControlDefinitionHandlers` (with the 0.7.0 ones), and the ones runs start from. */
  definitions: [coachRunControlDefinition, intakeDefinition] as Definition[],
  feedback: [] as (Feedback & { projectId: string; deleted: boolean })[],
  schedules: [] as (Schedule & { projectId: string })[],
  webhooks: [] as (Webhook & { projectId: string; secret: string })[],
  /** What the schedule preview answers (UTC), for any valid cron and timezone. */
  previewTimes: ['2026-12-01T12:00:00Z', '2026-12-02T12:00:00Z', '2026-12-03T12:00:00Z'],
  /** The last request bodies, for assertions. */
  lastStart: null as Schemas['StartPipelineRunRequest'] | null,
  lastSubmit: null as { runId: string; artifactKey: string; content: unknown } | null,
}

export function resetRunControlDb() {
  runControlDb.definitions = [coachRunControlDefinition, intakeDefinition]
  runControlDb.feedback = []
  runControlDb.schedules = []
  runControlDb.webhooks = []
  runControlDb.previewTimes = ['2026-12-01T12:00:00Z', '2026-12-02T12:00:00Z', '2026-12-03T12:00:00Z']
  runControlDb.lastStart = null
  runControlDb.lastSubmit = null
}

const allDefinitions = (): Definition[] => [
  ...pipelineDefinitions.filter((d) => !runControlDb.definitions.some((r) => r.pipelineId === d.pipelineId)),
  ...runControlDb.definitions,
]

/**
 * The 0.8.0 registry and definition reads (run control, schemas, triggers) for tests that need them: pass to
 * `server.use(...)`. The SDLC stays host-controlled.
 */
export const runControlDefinitionHandlers = [
  http.get('*/api/pipelines', () =>
    currentUser()
      ? HttpResponse.json<Schemas['PipelineSummaryDto'][]>(
          allDefinitions().map((d) => ({
            id: d.pipelineId,
            title: d.title,
            version: d.version,
            hash: d.hash,
            packId: d.packId,
            runControl: d.pipelineId === sdlcDefinition.pipelineId ? 'host' : (d.runControl ?? 'host'),
          })),
        )
      : unauthenticated(),
  ),
  http.get('*/api/pipelines/:pipelineId/definitions/:hash', ({ params }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const def = allDefinitions().find((d) => d.pipelineId === params.pipelineId && d.hash === params.hash)
    return def ? HttpResponse.json<Definition>(def) : problem(404, 'NOT_FOUND', 'The pipeline definition was not found.')
  }),
]

type Granted = Exclude<Access, Response>
type StoredRun = (typeof pipelinesDb.runs)[number]

const forbidden = () => problem(403, 'FORBIDDEN', 'You do not have permission to do that.')
const runNotFound = () => problem(404, 'NOT_FOUND', 'The pipeline run was not found.')

/** Contributor and up (an Admin counts as Owner), SPEC §2. */
function asWriter(id: unknown): Granted | Response {
  const a = access(id)
  return a instanceof Response ? a : a.level === 'viewer' ? forbidden() : a
}

function asOwner(id: unknown): Granted | Response {
  const a = access(id)
  return a instanceof Response ? a : a.level === 'owner' || a.level === 'admin' ? a : forbidden()
}

const isFinal = (status: Run['status']) => status === 'completed' || status === 'failed' || status === 'cancelled'

/** INPUT_INVALID with the failing paths, as the API's schema check answers. */
function inputErrors(def: Definition, key: string, content: unknown): Response | null {
  const schema = def.artifacts.find((x) => x.key === key)?.schema
  if (typeof schema !== 'object' || schema === null) {
    return null
  }
  const errors = validateJson(schema, content)
  return errors.length === 0
    ? null
    : problem(400, 'INPUT_INVALID', 'The input does not match its schema.', { details: errors.map((e) => `${key}: ${describeError(e)}`) })
}

function storeInput(stored: StoredRun, key: string, content: unknown) {
  const versions = stored.artifacts.get(key) ?? []
  const summary: Schemas['PipelineArtifactVersionSummaryDto'] = {
    id: nextId(),
    version: versions.length + 1,
    status: 'locked',
    hash: `sha256:${nextId()}`,
    createdAt: new Date().toISOString(),
  }
  versions.push({ summary, content: JSON.stringify(content, null, 2), intact: true })
  stored.artifacts.set(key, versions)
  return summary
}

const definitionOf = (pipelineId: unknown) => allDefinitions().find((d) => d.pipelineId === pipelineId)

function scheduleDto(s: Schedule & { projectId: string }): Schedule {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const { projectId, ...dto } = s
  return dto
}

function webhookDto(w: Webhook & { projectId: string; secret: string }): Webhook {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const { projectId, secret, ...dto } = w
  return dto
}

function feedbackDto(f: (typeof runControlDb.feedback)[number]): Feedback {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const { projectId, deleted, ...dto } = f
  return dto
}

function scheduleInvalid(cron: unknown, timezone: unknown): Response | null {
  if (typeof cron !== 'string' || cron.trim().split(/\s+/).length !== 5) {
    return problem(400, 'SCHEDULE_INVALID', 'The cron expression must have five fields.')
  }
  if (typeof timezone !== 'string' || timezone === '') {
    return problem(400, 'SCHEDULE_INVALID', 'The timezone is required.')
  }
  try {
    new Intl.DateTimeFormat('en', { timeZone: timezone })
  } catch {
    return problem(400, 'SCHEDULE_INVALID', 'The timezone is not a known IANA timezone.')
  }
  return null
}

const declares = (def: Definition, kind: Schemas['TriggerKind']) => def.triggers?.some((t) => t.kind === kind) === true

/** Seeds a feedback entry (newest last), as an agent's stage or a person left it. */
export function seedAgentFeedback(
  projectId: string,
  entry: Pick<Feedback, 'pipelineId' | 'agentKey' | 'text'> & Partial<Feedback>,
): (typeof runControlDb.feedback)[number] {
  const stored = {
    id: nextId(),
    source: 'human' as const,
    runId: null,
    agentRunId: null,
    createdBy: null,
    createdAt: new Date(Date.parse('2026-11-20T09:00:00Z') + runControlDb.feedback.length * 60_000).toISOString(),
    ...entry,
    projectId,
    deleted: false,
  }
  runControlDb.feedback.push(stored)
  return stored
}

/** Seeds a project schedule (its next fire times from `runControlDb.previewTimes`). */
export function seedPipelineSchedule(projectId: string, schedule: Partial<Schedule> & Pick<Schedule, 'pipelineId' | 'cron' | 'timezone'>) {
  const stored = {
    id: nextId(),
    inputs: {},
    paused: false,
    nextFireAt: runControlDb.previewTimes[0] ?? '2026-12-01T12:00:00Z',
    lastFireAt: null,
    nextFireTimes: [...runControlDb.previewTimes],
    ...schedule,
    projectId,
  }
  runControlDb.schedules.push(stored)
  return stored
}

export const engineRunControlHandlers = [
  // FR-331, FR-362: start an engine-controlled run (Contributor).
  http.post('*/api/projects/:id/pipeline-runs', async ({ request, params }) => {
    const a = asWriter(params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await request.json()) as Schemas['StartPipelineRunRequest']
    runControlDb.lastStart = body
    const def = definitionOf(body.pipelineId)
    if (!def) {
      return problem(404, 'NOT_FOUND', 'The pipeline was not found.')
    }
    if (def.runControl !== 'engine') {
      return problem(409, 'RUN_CONTROLLED_BY_HOST', 'This pipeline is run by its host.')
    }
    const inputs: Record<string, unknown> = body.inputs ?? {}
    const declared = def.artifacts.filter((x) => x.input === true).map((x) => x.key)
    for (const [key, content] of Object.entries(inputs)) {
      if (!declared.includes(key)) {
        return problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors: { inputs: [`${key} is not a declared input.`] } })
      }
      const invalid = inputErrors(def, key, content)
      if (invalid) {
        return invalid
      }
    }
    const latest = pipelinesDb.runs.reduce((max, r) => Math.max(max, Date.parse(r.run.startedAt)), Date.parse('2026-11-01T00:00:00Z'))
    const start = def.stages.find((s) => s.id === def.start[0])
    const run: Run = {
      id: nextId(),
      projectId: a.project.id,
      pipelineId: def.pipelineId,
      pipelineTitle: def.title,
      definitionHash: def.hash,
      hashShort: def.hash.slice(0, 12),
      currentStage: start?.id ?? '',
      currentStageTitle: start?.title ?? null,
      packStatus: start?.status ?? '',
      status: declared.every((k) => k in inputs) ? 'running' : 'waiting_on_input',
      checkpoint: 1,
      startedAt: new Date(latest + 3_600_000).toISOString(),
      completedAt: null,
      cost: 0,
      inputTokens: 0,
      outputTokens: 0,
      tokens: 0,
      agentRuns: 0,
    }
    const stored: StoredRun = { run, stages: [], timeline: [], artifacts: new Map(), gates: [] }
    pipelinesDb.runs.push(stored)
    for (const [key, content] of Object.entries(inputs)) {
      storeInput(stored, key, content)
    }
    return HttpResponse.json<Run>(run, { status: 201 })
  }),
  // FR-335, FR-362: submit an input artifact version (Contributor); a run waiting on input continues.
  http.post('*/api/pipeline-runs/:runId/inputs/:artifactKey', async ({ request, params }) => {
    const stored = pipelinesDb.runs.find((r) => r.run.id === params.runId)
    if (!stored) {
      return runNotFound()
    }
    const a = asWriter(stored.run.projectId)
    if (a instanceof Response) {
      return a
    }
    const def = definitionOf(stored.run.pipelineId)
    const key = String(params.artifactKey)
    if (!def?.artifacts.some((x) => x.key === key && x.input === true)) {
      return problem(404, 'NOT_FOUND', 'The input was not found.')
    }
    if (isFinal(stored.run.status)) {
      return problem(409, 'RUN_NOT_ACTIVE', 'The run is not active.')
    }
    const content: unknown = await request.json()
    runControlDb.lastSubmit = { runId: stored.run.id, artifactKey: key, content }
    const invalid = inputErrors(def, key, content)
    if (invalid) {
      return invalid
    }
    const summary = storeInput(stored, key, content)
    stored.run = { ...stored.run, status: 'running' }
    return HttpResponse.json<Schemas['PipelineArtifactVersionDto']>(
      {
        id: summary.id,
        key,
        title: def.artifacts.find((x) => x.key === key)?.title ?? key,
        version: summary.version,
        parentVersion: summary.version > 1 ? summary.version - 1 : null,
        status: summary.status,
        hash: summary.hash,
        intact: true,
        content: JSON.stringify(content, null, 2),
        agentRunId: null,
        createdAt: summary.createdAt,
        approvedAt: null,
      },
      { status: 201 },
    )
  }),
  // FR-351, FR-365: agent feedback (list: Viewer; add: Contributor; delete: Owner or the author).
  http.get('*/api/projects/:id/pipelines/:pipelineId/agents/:agentKey/feedback', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    return HttpResponse.json<Feedback[]>(
      runControlDb.feedback
        .filter((f) => !f.deleted && f.projectId === params.id && f.pipelineId === params.pipelineId && f.agentKey === params.agentKey)
        .sort((x, y) => y.createdAt.localeCompare(x.createdAt))
        .map(feedbackDto),
    )
  }),
  http.post('*/api/projects/:id/pipelines/:pipelineId/agents/:agentKey/feedback', async ({ request, params }) => {
    const a = asWriter(params.id)
    if (a instanceof Response) {
      return a
    }
    const { text } = (await request.json()) as Schemas['CreateAgentFeedbackRequest']
    const trimmed = (text ?? '').trim()
    if (trimmed === '' || trimmed.length > 2000) {
      return problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors: { text: ['Feedback must be 1 to 2,000 characters.'] } })
    }
    const entry = seedAgentFeedback(a.project.id, {
      pipelineId: String(params.pipelineId),
      agentKey: String(params.agentKey),
      text: trimmed,
      createdBy: a.userId,
      createdAt: new Date().toISOString(),
    })
    return HttpResponse.json<Feedback>(feedbackDto(entry), { status: 201 })
  }),
  http.delete('*/api/projects/:id/agent-feedback/:feedbackId', ({ params }) => {
    const a = asWriter(params.id)
    if (a instanceof Response) {
      return a
    }
    const entry = runControlDb.feedback.find((f) => f.id === params.feedbackId && f.projectId === params.id && !f.deleted)
    if (!entry) {
      return problem(404, 'NOT_FOUND', 'The feedback entry was not found.')
    }
    if (a.level !== 'owner' && a.level !== 'admin' && entry.createdBy !== a.userId) {
      return forbidden()
    }
    entry.deleted = true
    return new HttpResponse(null, { status: 204 })
  }),
  // FR-336, FR-366: schedules (list: Viewer; preview, create, change, delete: Owner).
  http.get('*/api/projects/:id/pipeline-schedules/preview', ({ request, params }) => {
    const a = asOwner(params.id)
    if (a instanceof Response) {
      return a
    }
    const q = new URL(request.url).searchParams
    const cron = q.get('cron') ?? ''
    const timezone = q.get('timezone') ?? ''
    const invalid = scheduleInvalid(cron, timezone)
    return (
      invalid ??
      HttpResponse.json<Schemas['PipelineSchedulePreviewDto']>({ cron, timezone, nextFireTimes: [...runControlDb.previewTimes] })
    )
  }),
  http.get('*/api/projects/:id/pipeline-schedules', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    return HttpResponse.json<Schedule[]>(runControlDb.schedules.filter((s) => s.projectId === params.id).map(scheduleDto))
  }),
  http.post('*/api/projects/:id/pipeline-schedules', async ({ request, params }) => {
    const a = asOwner(params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await request.json()) as Schemas['CreatePipelineScheduleRequest']
    const def = definitionOf(body.pipelineId)
    if (!def) {
      return problem(404, 'NOT_FOUND', 'The pipeline was not found.')
    }
    if (!declares(def, 'schedule')) {
      return problem(409, 'TRIGGER_NOT_SUPPORTED', 'The pipeline does not declare a schedule trigger.')
    }
    const invalid = scheduleInvalid(body.cron, body.timezone)
    if (invalid) {
      return invalid
    }
    const stored = seedPipelineSchedule(a.project.id, { pipelineId: def.pipelineId, cron: body.cron ?? '', timezone: body.timezone ?? '' })
    return HttpResponse.json<Schedule>(scheduleDto(stored), { status: 201 })
  }),
  http.patch('*/api/projects/:id/pipeline-schedules/:scheduleId', async ({ request, params }) => {
    const a = asOwner(params.id)
    if (a instanceof Response) {
      return a
    }
    const stored = runControlDb.schedules.find((s) => s.id === params.scheduleId && s.projectId === params.id)
    if (!stored) {
      return problem(404, 'NOT_FOUND', 'The schedule was not found.')
    }
    const body = (await request.json()) as Schemas['UpdatePipelineScheduleRequest']
    if (typeof body.paused === 'boolean') {
      stored.paused = body.paused
    }
    return HttpResponse.json<Schedule>(scheduleDto(stored))
  }),
  http.delete('*/api/projects/:id/pipeline-schedules/:scheduleId', ({ params }) => {
    const a = asOwner(params.id)
    if (a instanceof Response) {
      return a
    }
    const before = runControlDb.schedules.length
    runControlDb.schedules = runControlDb.schedules.filter((s) => !(s.id === params.scheduleId && s.projectId === params.id))
    return runControlDb.schedules.length === before ? problem(404, 'NOT_FOUND', 'The schedule was not found.') : new HttpResponse(null, { status: 204 })
  }),
  // FR-337, FR-366: webhooks (list without secrets: Viewer; create, rotate, delete: Owner; the secret only in create and rotate).
  http.get('*/api/projects/:id/pipeline-webhooks', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    return HttpResponse.json<Webhook[]>(runControlDb.webhooks.filter((w) => w.projectId === params.id).map(webhookDto))
  }),
  http.post('*/api/projects/:id/pipeline-webhooks', async ({ request, params }) => {
    const a = asOwner(params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await request.json()) as Schemas['CreatePipelineWebhookRequest']
    const def = definitionOf(body.pipelineId)
    if (!def) {
      return problem(404, 'NOT_FOUND', 'The pipeline was not found.')
    }
    if (!declares(def, 'webhook')) {
      return problem(409, 'TRIGGER_NOT_SUPPORTED', 'The pipeline does not declare a webhook trigger.')
    }
    const id = nextId()
    const stored = { id, pipelineId: def.pipelineId, url: `/api/hooks/pipelines/${id}`, previousSecretExpiresAt: null, projectId: a.project.id, secret: `whsec_${nextId()}` }
    runControlDb.webhooks.push(stored)
    return HttpResponse.json<Schemas['CreatedPipelineWebhookDto']>({ id, url: stored.url, secret: stored.secret }, { status: 201 })
  }),
  http.post('*/api/projects/:id/pipeline-webhooks/:webhookId/rotate', ({ params }) => {
    const a = asOwner(params.id)
    if (a instanceof Response) {
      return a
    }
    const stored = runControlDb.webhooks.find((w) => w.id === params.webhookId && w.projectId === params.id)
    if (!stored) {
      return problem(404, 'NOT_FOUND', 'The webhook was not found.')
    }
    stored.secret = `whsec_${nextId()}`
    stored.previousSecretExpiresAt = new Date(Date.now() + 24 * 3_600_000).toISOString()
    return HttpResponse.json<Schemas['PipelineWebhookSecretDto']>({ secret: stored.secret })
  }),
  http.delete('*/api/projects/:id/pipeline-webhooks/:webhookId', ({ params }) => {
    const a = asOwner(params.id)
    if (a instanceof Response) {
      return a
    }
    const before = runControlDb.webhooks.length
    runControlDb.webhooks = runControlDb.webhooks.filter((w) => !(w.id === params.webhookId && w.projectId === params.id))
    return runControlDb.webhooks.length === before ? problem(404, 'NOT_FOUND', 'The webhook was not found.') : new HttpResponse(null, { status: 204 })
  }),
]
