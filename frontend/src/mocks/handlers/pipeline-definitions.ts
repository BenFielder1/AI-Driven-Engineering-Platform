import type { Schemas } from '../../api/client'

type Definition = Schemas['PipelineDefinitionDto']
type Stage = Schemas['PipelineStageDefinitionDto']

const stage = (s: Partial<Stage> & Pick<Stage, 'id' | 'title' | 'status'>): Stage => ({
  kind: 'agent',
  agentKey: null,
  inputs: [],
  output: null,
  gate: null,
  transitions: [],
  ...s,
})

/** The SDLC pack's definition (backend src/Tevrin.Pack.Sdlc/Definitions/sdlc.pipeline.yaml), as GET /definitions/{hash} returns it. */
export const sdlcDefinition: Definition = {
  pipelineId: 'sdlc',
  title: 'Software delivery',
  version: 1,
  hash: 'a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90',
  packId: 'sdlc',
  start: ['refinement', 'analysis'],
  stages: [
    stage({ id: 'analysis', title: 'Analysis', status: 'active', agentKey: 'stack_analyst', output: 'stack_profile', transitions: [{ kind: 'next', to: 'refinement', on: null, title: null, maxIterations: null, maxIterationsSetting: null }] }),
    stage({ id: 'refinement', title: 'Refinement', status: 'active', agentKey: 'idea_refinement', output: 'project_request', transitions: [{ kind: 'next', to: 'specification', on: null, title: null, maxIterations: null, maxIterationsSetting: null }] }),
    stage({
      id: 'specification',
      title: 'Specification',
      status: 'active',
      agentKey: 'spec_generator',
      inputs: ['project_request'],
      output: 'technical_specification',
      gate: { type: 'spec_approval', modes: ['strict', 'advisory'], approves: 'technical_specification' },
      transitions: [{ kind: 'next', to: 'planning', on: null, title: null, maxIterations: null, maxIterationsSetting: null }],
    }),
    stage({
      id: 'planning',
      title: 'Planning',
      status: 'active',
      agentKey: 'plan_generator',
      inputs: ['technical_specification'],
      output: 'implementation_plan',
      gate: { type: 'plan_approval', modes: ['strict'], approves: 'implementation_plan' },
      transitions: [{ kind: 'next', to: 'execution', on: null, title: null, maxIterations: null, maxIterationsSetting: null }],
    }),
    stage({ id: 'execution', title: 'Execution', status: 'building', kind: 'code', inputs: ['implementation_plan'], transitions: [{ kind: 'next', to: 'review', on: null, title: null, maxIterations: null, maxIterationsSetting: null }] }),
    stage({
      id: 'review',
      title: 'Review',
      status: 'reviewing',
      agentKey: 'code_review',
      output: 'optimization_plan',
      transitions: [{ kind: 'loop', to: 'execution', on: 'review_required', title: 'Review cycle', maxIterations: null, maxIterationsSetting: 'review_cycles' }],
    }),
    stage({ id: 'release', title: 'Release', status: 'releasing', kind: 'code', gate: { type: 'pre_release', modes: ['strict', 'advisory', 'autonomous'], approves: null } }),
  ],
  artifacts: [
    { key: 'project_request', title: 'Project request', format: 'markdown' },
    { key: 'technical_specification', title: 'Technical specification', format: 'markdown' },
    { key: 'implementation_plan', title: 'Implementation plan', format: 'markdown' },
    { key: 'optimization_plan', title: 'Optimization plan', format: 'markdown' },
    { key: 'codebase_summary', title: 'Codebase summary', format: 'markdown' },
    { key: 'source_items', title: 'Source items', format: 'markdown' },
    { key: 'stack_profile', title: 'Stack profile', format: 'markdown' },
  ],
  statuses: [
    { name: 'active', generic: 'active' },
    { name: 'building', generic: 'active' },
    { name: 'reviewing', generic: 'active' },
    { name: 'releasing', generic: 'active' },
    { name: 'rolled_back', generic: 'active' },
    { name: 'awaiting_merge', generic: 'active' },
    { name: 'released', generic: 'completed' },
  ],
}

/**
 * The engine's fixture pipeline (backend tests/Tevrin.Engine.Tests/Fixtures/fixture-review.pipeline.yaml): draft →
 * check (validated, looping back to draft) → summarise, whose `report_approval` gate approves the check report. FR-305:
 * the engine UI renders it with no change to src/engine.
 */
export const fixtureReviewDefinition: Definition = {
  pipelineId: 'fixture-review',
  title: 'Fixture review',
  version: 1,
  hash: 'f1e2d3c4b5a6978877665544332211f0e1d2c3b4a5968778695a4b3c2d1e0f9a',
  packId: 'fixture',
  start: ['draft'],
  stages: [
    stage({ id: 'draft', title: 'Draft', status: 'drafting', agentKey: 'drafter', output: 'draft', transitions: [{ kind: 'next', to: 'check', on: null, title: null, maxIterations: null, maxIterationsSetting: null }] }),
    stage({
      id: 'check',
      title: 'Check',
      status: 'checking',
      agentKey: 'checker',
      inputs: ['draft'],
      output: 'report',
      transitions: [
        { kind: 'next', to: 'summarise', on: null, title: null, maxIterations: null, maxIterationsSetting: null },
        { kind: 'loop', to: 'draft', on: 'needs_redraft', title: null, maxIterations: null, maxIterationsSetting: 'redraft_cycles' },
      ],
    }),
    stage({
      id: 'summarise',
      title: 'Summarise',
      status: 'summarising',
      agentKey: 'summariser',
      inputs: ['draft', 'report'],
      output: 'summary',
      gate: { type: 'report_approval', modes: ['strict', 'advisory'], approves: 'report' },
    }),
  ],
  artifacts: [
    { key: 'draft', title: 'Draft', format: 'json' },
    { key: 'report', title: 'Check report', format: 'json' },
    { key: 'summary', title: 'Summary', format: 'markdown' },
  ],
  statuses: [
    { name: 'drafting', generic: 'active' },
    { name: 'checking', generic: 'active' },
    { name: 'summarising', generic: 'active' },
    { name: 'published', generic: 'completed' },
    { name: 'rolled_back', generic: 'active' },
  ],
}

const next = (to: string): Stage['transitions'][number] => ({ kind: 'next', to, on: null, title: null, maxIterations: null, maxIterationsSetting: null })

/**
 * FR-339: the Impulse coaching pack's definition (backend src/Tevrin.Pack.Coach/Definitions/impulse-coach.pipeline.yaml),
 * as GET /definitions/{hash} returns it (PipelineDtoMapper: implicit `next` transitions, artifact `scope`). analyse →
 * plan (Velocity-guarded, retry then escalate) → review (`plan_approval` gate on the plan draft) → write. The engine UI
 * renders it with no coach-specific code in src/engine.
 */
export const impulseCoachDefinition = {
  pipelineId: 'impulse-coach',
  title: 'Impulse coaching',
  version: 1,
  hash: 'c0a1c2c3c4c5c6c7c8c9cacbcccdcecf00112233445566778899aabbccddeeff',
  packId: 'coach',
  start: ['analyse'],
  stages: [
    stage({ id: 'analyse', title: 'Analyse', status: 'analysing', agentKey: 'performance-analyst', inputs: ['session_log', 'athlete_profile'], output: 'performance', transitions: [next('plan')] }),
    stage({ id: 'plan', title: 'Plan', status: 'planning', agentKey: 'programme-planner', inputs: ['performance', 'athlete_profile'], output: 'plan_draft', transitions: [next('review')] }),
    stage({
      id: 'review',
      title: 'Review',
      status: 'reviewing',
      kind: 'manual',
      gate: { type: 'plan_approval', modes: ['strict', 'advisory'], approves: 'plan_draft' },
      transitions: [next('write')],
    }),
    stage({ id: 'write', title: 'Write', status: 'writing', agentKey: 'coach-voice', inputs: ['plan_draft'], output: 'coach_message' }),
  ],
  artifacts: [
    { key: 'athlete_profile', title: 'Athlete profile', format: 'json', scope: 'project' },
    { key: 'session_log', title: 'Session log', format: 'json', scope: 'run' },
    { key: 'performance', title: 'Performance', format: 'json', scope: 'run' },
    { key: 'plan_draft', title: 'Plan draft', format: 'json', scope: 'run' },
    { key: 'coach_message', title: 'Coach message', format: 'json', scope: 'run' },
  ],
  statuses: [
    { name: 'analysing', generic: 'active' },
    { name: 'planning', generic: 'active' },
    { name: 'reviewing', generic: 'active' },
    { name: 'writing', generic: 'active' },
    { name: 'coached', generic: 'completed' },
  ],
} satisfies Definition

export const pipelineDefinitions: readonly Definition[] = [sdlcDefinition, fixtureReviewDefinition, impulseCoachDefinition]
