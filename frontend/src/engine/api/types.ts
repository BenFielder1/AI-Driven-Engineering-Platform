import type { Schemas } from '../../api/client'

/** FR-299: a registered pipeline. */
export type PipelineSummary = Schemas['PipelineSummaryDto']
/** FR-299: a pinned definition (stages, artifacts, statuses): the source of every label the engine UI shows (FR-305). */
export type PipelineDefinition = Schemas['PipelineDefinitionDto']
export type StageDefinition = Schemas['PipelineStageDefinitionDto']
export type ArtifactDefinition = Schemas['PipelineArtifactDefinitionDto']
/** FR-300: a run, its stages and its timeline. */
export type PipelineRun = Schemas['PipelineRunDto']
export type GenericRunStatus = PipelineRun['status']
export type RunStage = Schemas['PipelineStageDto']
export type StageState = RunStage['state']
export type TimelineItem = Schemas['PipelineTimelineItemDto']
export type TimelineKind = Schemas['PipelineTimelineKind']
export type TimelinePage = Schemas['PipelineTimelinePageDto']
/** FR-301: a run's artifacts, versions and diffs, and its gates. */
export type RunArtifact = Schemas['PipelineArtifactDto']
export type RunArtifactVersionSummary = Schemas['PipelineArtifactVersionSummaryDto']
export type RunArtifactVersion = Schemas['PipelineArtifactVersionDto']
export type RunArtifactDiff = Schemas['PipelineArtifactDiffDto']
export type RunGate = Schemas['PipelineGateDto']
/** FR-298: one (agent key, prompt version) group. */
export type PromptStats = Schemas['PromptStatsDto']

/** Live hub messages (FR-302) on the project's group: each carries its DTO. */
export type PipelineLiveEvent =
  | { type: 'pipelineRunUpdated'; run: PipelineRun }
  | { type: 'stageUpdated'; stage: RunStage }
  | { type: 'gateUpdated'; gate: RunGate }
/** FR-302: a pending gate in the inbox, with its project and pipeline, and a page of them. */
export type GateInboxItem = Schemas['PipelineGateInboxItemDto']
export type GateInboxPage = Schemas['PipelineGateInboxPageDto']
