import type { Schemas } from '../../api/client'
import { fixtureReviewDefinition, impulseCoachDefinition, sdlcDefinition } from './pipeline-definitions'
import { seedPipelineRun, seedPromptStats, type StoredPipelineRun } from './pipeline-store'
import type { StoredProject } from './projects'

type Run = Schemas['PipelineRunDto']
type Item = Omit<Schemas['PipelineTimelineItemDto'], 'id' | 'at'>

const agentRun = (stageId: string, agentKey: string, extra: Partial<Item> = {}): Item => ({
  kind: 'agent_run',
  stageId,
  agentKey,
  agentRunStatus: 'succeeded',
  model: 'claude-opus-5-5',
  promptVersion: '1',
  inputTokens: 1_200,
  outputTokens: 800,
  cost: 0.05,
  durationMs: 12_000,
  latencyMs: 900,
  error: null,
  input: `[system]\nYou are the ${agentKey}.\n\n[user]\nDo the ${stageId} work.`,
  inputTruncated: false,
  output: `Output of ${agentKey}.`,
  outputTruncated: false,
  ...extra,
})

const entered = (stageId: string, iteration = 1, exitedAt: string | null = null): Item => ({
  kind: 'stage_entered',
  stageId,
  iteration,
  checkpoint: 1,
  exitedAt,
  outcome: null,
})
const transition = (fromStage: string, toStage: string, checkpoint: number): Item => ({
  kind: 'transition',
  fromStage,
  toStage,
  transitionKind: 'next',
  checkpoint,
  reason: null,
})
const version = (artifactKey: string, artifactVersion: number, artifactStatus: Schemas['ArtifactStatus']): Item => ({
  kind: 'artifact_version',
  artifactKey,
  artifactVersion,
  artifactStatus,
  contentHash: `${artifactKey}-${String(artifactVersion)}`.padEnd(64, '0'),
})

/** (a) An SDLC run in Specification, waiting on its specification approval gate. */
export function seedSdlcRun(project: StoredProject, overrides: Partial<Run> = {}): StoredPipelineRun {
  const stored = seedPipelineRun(
    project,
    {
      definition: sdlcDefinition,
      current: 'specification',
      packStatus: 'active',
      status: 'waiting_on_gate',
      checkpoint: 2,
      completedStages: ['refinement'],
      timeline: [
        entered('refinement', 1, '2026-11-01T10:05:00.000Z'),
        agentRun('refinement', 'idea_refinement'),
        version('project_request', 1, 'locked'),
        transition('refinement', 'specification', 2),
        entered('specification'),
        agentRun('specification', 'spec_generator', { inputTokens: 3_400, outputTokens: 9_800, cost: 0.21 }),
        version('technical_specification', 1, 'superseded'),
        agentRun('specification', 'spec_generator', { inputTokens: 3_500, outputTokens: 9_900, cost: 0.22 }),
        version('technical_specification', 2, 'draft'),
        { kind: 'gate_created', gateType: 'spec_approval', gateStatus: 'pending' },
      ],
      artifacts: [
        { key: 'project_request', versions: [{ status: 'locked', content: '# Inventory tracker\n\nTrack stock levels.' }] },
        {
          key: 'technical_specification',
          versions: [
            { status: 'superseded', content: '# Technical specification\n\n## Overview\nTrack stock.' },
            { status: 'draft', content: '# Technical specification\n\n## Overview\nTrack stock levels per warehouse.' },
          ],
        },
      ],
      gates: [{ type: 'spec_approval', artifactKey: 'technical_specification', artifactVersion: 2 }],
    },
    overrides,
  )
  seedPromptStats(project, 'sdlc', [
    { agentKey: 'spec_generator', promptVersion: '1', runs: 2, avgInputTokens: 3_450, avgOutputTokens: 9_850, avgCost: 0.215, avgLatencyMs: 900, avgDurationMs: 12_000, failureRate: 0 },
  ])
  return stored
}

/**
 * (b) A run of the fixture pipeline (`fixture-review`): drafted, checked twice (the first report rejected by the
 * guard and retried), now in Summarise with its `report_approval` gate pending.
 */
export function seedFixtureRun(project: StoredProject, overrides: Partial<Run> = {}): StoredPipelineRun {
  const stored = seedPipelineRun(
    project,
    {
      definition: fixtureReviewDefinition,
      current: 'summarise',
      packStatus: 'summarising',
      status: 'waiting_on_gate',
      checkpoint: 3,
      completedStages: ['draft', 'check'],
      timeline: [
        entered('draft', 1, '2026-11-01T10:02:00.000Z'),
        agentRun('draft', 'drafter', { output: '{"title":"Q3 summary","points":["a","b"]}' }),
        version('draft', 1, 'locked'),
        transition('draft', 'check', 2),
        entered('check', 1, '2026-11-01T10:08:00.000Z'),
        agentRun('check', 'checker', { error: 'validation_rejected: /points: too few', output: '{"ok":false}' }),
        version('report', 1, 'superseded'),
        agentRun('check', 'checker', { promptVersion: '2', output: '{"ok":true,"notes":"Consistent"}' }),
        version('report', 2, 'draft'),
        transition('check', 'summarise', 3),
        entered('summarise'),
        { kind: 'gate_created', gateType: 'report_approval', gateStatus: 'pending' },
      ],
      artifacts: [
        { key: 'draft', versions: [{ status: 'locked', content: '{"title":"Q3 summary","points":["a","b"]}' }] },
        {
          key: 'report',
          versions: [
            { status: 'superseded', content: '{\n  "ok": false\n}' },
            { status: 'draft', content: '{\n  "ok": true,\n  "notes": "Consistent"\n}' },
          ],
        },
      ],
      gates: [{ type: 'report_approval', artifactKey: 'report', artifactVersion: 2 }],
    },
    overrides,
  )
  seedPromptStats(project, 'fixture-review', [
    { agentKey: 'drafter', promptVersion: '1', runs: 4, avgInputTokens: 1_200, avgOutputTokens: 800, avgCost: 0.05, avgLatencyMs: 900, avgDurationMs: 12_000, failureRate: 0 },
    { agentKey: 'checker', promptVersion: '1', runs: 3, avgInputTokens: 1_500.5, avgOutputTokens: 420, avgCost: 0.0312, avgLatencyMs: null, avgDurationMs: 8_250, failureRate: 0.3333 },
    { agentKey: 'checker', promptVersion: '2', runs: 2, avgInputTokens: 1_480, avgOutputTokens: 400, avgCost: 0.03, avgLatencyMs: 750, avgDurationMs: 7_900, failureRate: 0 },
  ])
  return stored
}

/**
 * (c) FR-339: a run of the Impulse coaching pipeline (`impulse-coach`): the session log analysed, the plan drafted twice
 * (the first draft rejected by the Velocity load-rules guard and retried with feedback), now in Review with its
 * `plan_approval` gate pending on the second draft.
 */
export function seedCoachRun(project: StoredProject, overrides: Partial<Run> = {}): StoredPipelineRun {
  const sessionLog = '{"date":"2026-11-02","sessions":[{"type":"tempo","distanceKm":10,"rpe":7}]}'
  const performance = '{"load":"rising","summary":"Tempo held at RPE 7"}'
  return seedPipelineRun(
    project,
    {
      definition: impulseCoachDefinition,
      current: 'review',
      packStatus: 'reviewing',
      status: 'waiting_on_gate',
      checkpoint: 3,
      completedStages: ['analyse', 'plan'],
      timeline: [
        version('session_log', 1, 'locked'),
        entered('analyse', 1, '2026-11-02T10:04:00.000Z'),
        agentRun('analyse', 'performance-analyst', { output: performance }),
        version('performance', 1, 'locked'),
        transition('analyse', 'plan', 2),
        entered('plan', 1, '2026-11-02T10:10:00.000Z'),
        agentRun('plan', 'programme-planner', { error: 'validation_rejected: /weeks/0: weekly load rises more than 10%', output: '{"weeks":[{"loadKm":60}]}' }),
        version('plan_draft', 1, 'superseded'),
        agentRun('plan', 'programme-planner', { output: '{"weeks":[{"loadKm":44}]}' }),
        version('plan_draft', 2, 'draft'),
        transition('plan', 'review', 3),
        entered('review'),
        { kind: 'gate_created', gateType: 'plan_approval', gateStatus: 'pending' },
      ],
      artifacts: [
        { key: 'session_log', versions: [{ status: 'locked', content: sessionLog }] },
        { key: 'performance', versions: [{ status: 'locked', content: performance }] },
        {
          key: 'plan_draft',
          versions: [
            { status: 'superseded', content: '{\n  "weeks": [{ "loadKm": 60 }]\n}' },
            { status: 'draft', content: '{\n  "weeks": [{ "loadKm": 44 }]\n}' },
          ],
        },
      ],
      gates: [{ type: 'plan_approval', artifactKey: 'plan_draft', artifactVersion: 2 }],
    },
    overrides,
  )
}
