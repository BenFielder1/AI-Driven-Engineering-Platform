import type { Schemas } from '../../api/client'
import { nextId } from './auth'
import type { StoredProject } from './projects'

type Run = Schemas['PipelineRunDto']
type Stage = Schemas['PipelineStageDto']
type Item = Schemas['PipelineTimelineItemDto']
type Gate = Schemas['PipelineGateDto']
type Definition = Schemas['PipelineDefinitionDto']

export interface StoredArtifactVersion {
  summary: Schemas['PipelineArtifactVersionSummaryDto']
  content: string
  intact: boolean
}

/** A pipeline run with everything the engine endpoints read (FR-300–FR-303). Timeline items carry the replay fields. */
export interface StoredPipelineRun {
  run: Run
  stages: Stage[]
  timeline: Item[]
  /** By key, versions oldest first. */
  artifacts: Map<string, StoredArtifactVersion[]>
  gates: Gate[]
}

export const pipelinesDb = {
  runs: [] as StoredPipelineRun[],
  promptStats: [] as (Schemas['PromptStatsDto'] & { pipelineId: string; projectId: string })[],
  /** FR-301 test knob: runs whose pinned definition this build can't run (decisions answer DEFINITION_UNAVAILABLE). */
  unavailableRuns: new Set<string>(),
}

export function resetPipelinesDb() {
  pipelinesDb.runs = []
  pipelinesDb.promptStats = []
  pipelinesDb.unavailableRuns = new Set()
}

let clock = Date.parse('2026-11-01T09:00:00Z')
/** Each seeded run starts an hour after the previous, so the last seeded is the newest. */
const nextStart = () => (clock += 3_600_000)
const at = (start: number, minutes: number) => new Date(start + minutes * 60_000).toISOString()

/** Builds a run from its definition: stage states from `current`, a timeline, artifacts and gates. */
export interface RunScript {
  definition: Definition
  current: string
  packStatus: string
  status?: Run['status']
  checkpoint?: number
  completedStages: string[]
  /** Timeline entries in order, a minute apart; ids and times are filled in. */
  timeline: Omit<Item, 'id' | 'at'>[]
  artifacts: { key: string; versions: { status: Schemas['ArtifactStatus']; content: string; intact?: boolean }[] }[]
  gates?: {
    type: string
    status?: Gate['status']
    artifactKey?: string
    artifactVersion?: number
    /** Defaults to the type in words ("Spec approval"). */
    title?: string
    overdue?: boolean
    /** Defaults to just after the run's last timeline entry. */
    createdAt?: string
  }[]
}

export function seedPipelineRun(project: StoredProject, script: RunScript, overrides: Partial<Run> = {}): StoredPipelineRun {
  const start = nextStart()
  const runId = nextId()
  const def = script.definition
  const timeline: Item[] = script.timeline.map((entry, i) => ({ ...entry, id: nextId(), at: at(start, i) }))
  const agentRuns = timeline.filter((t) => t.kind === 'agent_run')
  const inputTokens = agentRuns.reduce((n, t) => n + (t.inputTokens ?? 0), 0)
  const outputTokens = agentRuns.reduce((n, t) => n + (t.outputTokens ?? 0), 0)
  const currentStage = def.stages.find((s) => s.id === script.current)
  const run: Run = {
    id: runId,
    projectId: project.id,
    pipelineId: def.pipelineId,
    pipelineTitle: def.title,
    definitionHash: def.hash,
    hashShort: def.hash.slice(0, 12),
    currentStage: script.current,
    currentStageTitle: currentStage?.title ?? null,
    packStatus: script.packStatus,
    status: script.status ?? 'active',
    checkpoint: script.checkpoint ?? 1,
    startedAt: at(start, 0),
    completedAt: null,
    cost: Math.round(agentRuns.reduce((n, t) => n + (t.cost ?? 0), 0) * 1e6) / 1e6,
    inputTokens,
    outputTokens,
    tokens: inputTokens + outputTokens,
    agentRuns: agentRuns.length,
    ...overrides,
  }
  const currentIndex = def.stages.findIndex((s) => s.id === script.current)
  const stages: Stage[] = def.stages.map((s, i) => {
    const entered = timeline.filter((t) => t.kind === 'stage_entered' && t.stageId === s.id)
    const last = entered.at(-1)
    const state: Stage['state'] =
      i === currentIndex ? 'active' : i > currentIndex ? 'pending' : script.completedStages.includes(s.id) ? 'completed' : 'skipped'
    const exitedAt = state === 'completed' ? (last?.exitedAt ?? null) : null
    return {
      runId,
      id: s.id,
      title: s.title,
      state,
      iterations: entered.length,
      enteredAt: last?.at ?? null,
      exitedAt,
      durationMs: last && exitedAt ? Date.parse(exitedAt) - Date.parse(last.at) : null,
    }
  })
  const artifacts = new Map<string, StoredArtifactVersion[]>()
  for (const a of script.artifacts) {
    artifacts.set(
      a.key,
      a.versions.map((v, i) => ({
        summary: { id: nextId(), version: i + 1, status: v.status, hash: `${a.key.length.toString(16)}${String(i + 1)}`.padEnd(64, 'c'), createdAt: at(start, i + 2) },
        content: v.content,
        intact: v.intact ?? true,
      })),
    )
  }
  const gates: Gate[] = (script.gates ?? []).map((g) => {
    const declaring = def.stages.find((s) => s.gate?.type === g.type)
    const artifact = g.artifactKey ? artifacts.get(g.artifactKey)?.find((v) => v.summary.version === g.artifactVersion) : undefined
    return {
      id: nextId(),
      runId,
      projectId: project.id,
      type: g.type,
      title: g.title ?? g.type.charAt(0).toUpperCase() + g.type.slice(1).replaceAll('_', ' '),
      stageId: declaring?.id ?? null,
      status: g.status ?? 'pending',
      artifactId: artifact?.summary.id ?? null,
      artifactKey: g.artifactKey ?? null,
      artifactVersion: g.artifactVersion ?? null,
      createdAt: g.createdAt ?? at(start, timeline.length),
      decidedAt: null,
      decidedByUserId: null,
      comment: null,
      decision: null,
      targetCheckpoint: null,
      overdue: g.overdue ?? false,
    }
  })
  // Gate timeline entries point at the stored gates.
  for (const item of timeline) {
    if ((item.kind === 'gate_created' || item.kind === 'gate_decided') && !item.gateId) {
      item.gateId = gates.find((g) => g.type === item.gateType)?.id ?? null
    }
  }
  const stored: StoredPipelineRun = { run, stages, timeline, artifacts, gates }
  pipelinesDb.runs.push(stored)
  return stored
}

/** Adds `count` plain timeline entries (stage entries) to a run, for paging tests. */
export function addTimelineEntries(stored: StoredPipelineRun, count: number, stageId: string) {
  const last = stored.timeline.at(-1)
  const base = last ? Date.parse(last.at) : Date.parse(stored.run.startedAt)
  for (let i = 1; i <= count; i += 1) {
    stored.timeline.push({ id: nextId(), at: new Date(base + i * 1000).toISOString(), kind: 'stage_entered', stageId, iteration: i, checkpoint: stored.run.checkpoint })
  }
}

/** Records prompt comparison rows for a pipeline on a project (FR-298). */
export function seedPromptStats(project: StoredProject, pipelineId: string, rows: Schemas['PromptStatsDto'][]) {
  pipelinesDb.promptStats.push(...rows.map((r) => ({ ...r, pipelineId, projectId: project.id })))
}

/**
 * FR-307: adds a pending gate to a seeded run, e.g. an escalation raised by a guard (no stage declares its type, so
 * `stageId` is null). Returns the stored gate.
 */
export function addPipelineGate(stored: StoredPipelineRun, gate: Partial<Gate> & Pick<Gate, 'type' | 'title'>): Gate {
  const added: Gate = {
    id: nextId(),
    runId: stored.run.id,
    projectId: stored.run.projectId,
    stageId: null,
    status: 'pending',
    artifactId: null,
    artifactKey: null,
    artifactVersion: null,
    createdAt: new Date(Date.parse(stored.run.startedAt) + 3_600_000).toISOString(),
    decidedAt: null,
    decidedByUserId: null,
    comment: null,
    decision: null,
    targetCheckpoint: null,
    overdue: false,
    ...gate,
  }
  stored.gates.push(added)
  return added
}
