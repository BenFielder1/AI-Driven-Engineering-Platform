using System.ComponentModel.DataAnnotations;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Api;

/// <summary>FR-299: a registered pipeline: id, title, version, current definition hash and pack.</summary>
public sealed record PipelineSummaryDto(string Id, string Title, int Version, string Hash, string PackId);

/// <summary>FR-299: a pipeline definition by hash: its stages, artifacts and statuses.</summary>
public sealed record PipelineDefinitionDto(
    string PipelineId,
    string Title,
    int Version,
    string Hash,
    string PackId,
    IReadOnlyList<string> Start,
    IReadOnlyList<PipelineStageDefinitionDto> Stages,
    IReadOnlyList<PipelineArtifactDefinitionDto> Artifacts,
    IReadOnlyList<PipelineStatusDefinitionDto> Statuses);

/// <summary>A stage of a definition. <c>kind</c> is <c>agent</c>, <c>code</c> or <c>manual</c>; <c>transitions</c> lists
/// where it can go next.</summary>
public sealed record PipelineStageDefinitionDto(
    string Id,
    string Title,
    StageKind Kind,
    string Status,
    string? AgentKey,
    IReadOnlyList<string> Inputs,
    string? Output,
    PipelineStageGateDto? Gate,
    IReadOnlyList<PipelineStageTransitionDto> Transitions);

/// <summary>The gate a stage raises: type key, the approval modes it applies in and the artifact it approves.</summary>
public sealed record PipelineStageGateDto(string Type, IReadOnlyList<ApprovalMode> Modes, string? Approves);

/// <summary>A way out of a stage: <c>kind</c> is <c>next</c>, <c>on_success</c>, <c>on_reject</c> or <c>loop</c>.
/// <c>on</c> is a loop's outcome; <c>maxIterations</c> its fixed cap, or <c>maxIterationsSetting</c> the setting that
/// holds it.</summary>
public sealed record PipelineStageTransitionDto(string Kind, string To, string? On, string? Title, int? MaxIterations, string? MaxIterationsSetting);

/// <summary>An artifact a definition produces or reads.</summary>
public sealed record PipelineArtifactDefinitionDto(string Key, string Title, ArtifactFormat Format);

/// <summary>A pack status and the generic status it maps to (§4.57).</summary>
public sealed record PipelineStatusDefinitionDto(string Name, GenericRunStatus Generic);

/// <summary>FR-298: one (agent key, prompt version) group of a pipeline's agent runs.</summary>
public sealed record PromptStatsDto(
    string AgentKey,
    string PromptVersion,
    int Runs,
    double AvgInputTokens,
    double AvgOutputTokens,
    decimal AvgCost,
    double? AvgLatencyMs,
    double AvgDurationMs,
    double FailureRate);

/// <summary>
/// FR-300: a pipeline run. <c>status</c> is the generic status derived on read (§4.57); <c>packStatus</c> the pack's own.
/// <c>hashShort</c> is the first 12 characters of the pinned definition hash. <c>cost</c> and the token totals sum the
/// run's agent runs.
/// </summary>
public sealed record PipelineRunDto(
    Guid Id,
    Guid ProjectId,
    string PipelineId,
    string PipelineTitle,
    string DefinitionHash,
    string HashShort,
    string CurrentStage,
    string? CurrentStageTitle,
    string PackStatus,
    GenericRunStatus Status,
    int Checkpoint,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    decimal Cost,
    long InputTokens,
    long OutputTokens,
    long Tokens,
    int AgentRuns);

/// <summary>FR-300: a stage of a run (D-0.7.0-32: iterations count entries). <c>runId</c> identifies the run in live
/// <c>stageUpdated</c> messages.</summary>
public sealed record PipelineStageDto(
    Guid RunId,
    string Id,
    string Title,
    StageState State,
    int Iterations,
    DateTimeOffset? EnteredAt,
    DateTimeOffset? ExitedAt,
    long? DurationMs);

/// <summary>
/// FR-301: a gate of a run. <c>stageId</c> is the stage that declares the gate type (when one does);
/// <c>artifactKey</c>/<c>artifactVersion</c> name the gated artifact. Decided gates carry <c>decidedAt</c>,
/// <c>decidedByUserId</c>, <c>comment</c> and an escalation's <c>decision</c>.
/// </summary>
public sealed record PipelineGateDto(
    Guid Id,
    Guid RunId,
    Guid ProjectId,
    string Type,
    string Title,
    string? StageId,
    GateStatus Status,
    Guid? ArtifactId,
    string? ArtifactKey,
    int? ArtifactVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    Guid? DecidedByUserId,
    string? Comment,
    EscalationDecision? Decision,
    int? TargetCheckpoint,
    bool Overdue);

/// <summary>Approve or reject a gate through the engine (FR-301): the same fields and limits as the project gate endpoints.</summary>
public sealed record DecidePipelineGateRequest
{
    public const int CommentMaxLength = 4000;

    [MaxLength(CommentMaxLength)]
    public string? Comment { get; init; }

    public EscalationDecision? Decision { get; init; }

    [Range(1, int.MaxValue)]
    public int? TargetCheckpoint { get; init; }
}

/// <summary>FR-302: a pending gate in the inbox, with its project and pipeline.</summary>
public sealed record PipelineGateInboxItemDto(PipelineGateDto Gate, string ProjectName, string PipelineId, string PipelineTitle);

/// <summary>A page of the gate inbox; <c>next</c> is the cursor of the following page, or <c>null</c>.</summary>
public sealed record PipelineGateInboxPageDto(IReadOnlyList<PipelineGateInboxItemDto> Items, string? Next);

/// <summary>FR-301: an artifact key of a run with its versions (newest first).</summary>
public sealed record PipelineArtifactDto(string Key, string Title, IReadOnlyList<PipelineArtifactVersionSummaryDto> Versions);

/// <summary>One artifact version in a run's artifact list.</summary>
public sealed record PipelineArtifactVersionSummaryDto(Guid Id, int Version, ArtifactStatus Status, string Hash, DateTimeOffset CreatedAt);

/// <summary>FR-301: one artifact version with its content. <c>intact</c> is false when the content no longer matches its
/// hash (FR-20).</summary>
public sealed record PipelineArtifactVersionDto(
    Guid Id,
    string Key,
    string Title,
    int Version,
    int? ParentVersion,
    ArtifactStatus Status,
    string Hash,
    bool Intact,
    string Content,
    Guid? AgentRunId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt);

/// <summary>A line of an artifact diff (FR-21); <c>skipped</c> counts the unchanged lines a <c>skipped</c> entry stands for.</summary>
public sealed record PipelineDiffLineDto(DiffLineKind Kind, int? OldLine, int? NewLine, string Text, int? Skipped);

/// <summary>FR-301: the line diff between two versions of a run's artifact.</summary>
public sealed record PipelineArtifactDiffDto(string Key, int From, int To, string UnifiedDiff, IReadOnlyList<PipelineDiffLineDto> Lines);
