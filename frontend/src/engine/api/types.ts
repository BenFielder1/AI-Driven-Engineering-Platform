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

/** FR-363 live: a branch stage execution was entered or left (ids and state only). */
export type StageBranchUpdate = Schemas['PipelineStageBranchUpdatedMessage']
/** FR-363 live: a map execution's progress (throttled to one a second per map, then a trailing one). */
export type MapProgressUpdate = Schemas['PipelineMapProgressMessage']
/** Live hub messages (FR-302, FR-363) on the project's group: each carries its DTO or message. */
export type PipelineLiveEvent =
  | { type: 'pipelineRunUpdated'; run: PipelineRun }
  | { type: 'stageUpdated'; stage: RunStage }
  | { type: 'gateUpdated'; gate: RunGate }
  | { type: 'stageBranchUpdated'; update: StageBranchUpdate }
  | { type: 'mapProgress'; update: MapProgressUpdate }
/** FR-302: a pending gate in the inbox, with its project and pipeline, and a page of them. */
export type GateInboxItem = Schemas['PipelineGateInboxItemDto']
export type GateInboxPage = Schemas['PipelineGateInboxPageDto']

/** FR-363: the constructs a stage of a definition may declare (lanes, map, variants, conversation). */
export type BranchDefinition = Schemas['PipelineBranchDefinitionDto']
export type MapProgress = Schemas['PipelineMapProgressDto']
export type MapItem = Schemas['PipelineMapItemDto']
export type MapItemStatus = Schemas['MapItemStatus']
export type MapItemPage = Schemas['PipelineMapItemPageDto']
export type RunFollowUp = Schemas['PipelineRunFollowUpDto']
/** FR-364: what a gate asks the decider to approve (`kind`: variant_select, tool_call or external_handler). */
export type GatePayload = Schemas['PipelineGatePayloadDto']
export type GateVariant = Schemas['PipelineGateVariantDto']
/** FR-356, FR-363: one turn of a conversation stage's transcript (its output artifact `{ turns: [...] }`). */
export interface TranscriptTurn {
  role: string
  agentKey: string
  text: string
  agentRunId: string | null
}
