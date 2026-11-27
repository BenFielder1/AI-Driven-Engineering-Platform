using System.ComponentModel.DataAnnotations;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Api;

/// <summary>FR-299: a registered pipeline: id, title, version, current definition hash and pack.</summary>
public sealed record PipelineSummaryDto(string Id, string Title, int Version, string Hash, string PackId)
{
    /// <summary>FR-330, FR-362: <c>engine</c> when the engine's run control starts and drives its runs (Start run);
    /// <c>host</c> when the host does (the SDLC).</summary>
    public RunControl RunControl { get; init; }
}

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
    IReadOnlyList<PipelineStatusDefinitionDto> Statuses)
{
    /// <summary>FR-330, FR-362: who controls the pipeline's runs (<c>engine</c> or <c>host</c>).</summary>
    public RunControl RunControl { get; init; }

    /// <summary>FR-331, FR-362: the approval modes a run may choose (empty: any), and the default.</summary>
    public IReadOnlyList<ApprovalMode> ApprovalModes { get; init; } = [];

    public ApprovalMode? DefaultApprovalMode { get; init; }

    /// <summary>FR-336, FR-337, FR-366: the triggers the definition declares.</summary>
    public IReadOnlyList<PipelineTriggerDefinitionDto> Triggers { get; init; } = [];

    /// <summary>FR-354, FR-363: the follow-up stages and when they run after completion.</summary>
    public IReadOnlyList<PipelineFollowUpDefinitionDto> FollowUps { get; init; } = [];
}

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
    IReadOnlyList<PipelineStageTransitionDto> Transitions)
{
    /// <summary>FR-343, FR-363: a parallel group's branches (lanes); <c>null</c> for other stages.</summary>
    public IReadOnlyList<PipelineBranchDefinitionDto>? Branches { get; init; }

    /// <summary>FR-345, FR-363: the stage's fan-out, or <c>null</c>.</summary>
    public PipelineFanOutDefinitionDto? FanOut { get; init; }

    /// <summary>FR-349, FR-363: the stage's output variants, or <c>null</c>.</summary>
    public PipelineVariantsDefinitionDto? Variants { get; init; }

    /// <summary>FR-355, FR-363: the stage's map (and reduce), or <c>null</c>.</summary>
    public PipelineMapDefinitionDto? Map { get; init; }

    /// <summary>FR-356, FR-363: the stage's conversation, or <c>null</c>.</summary>
    public PipelineConversationDefinitionDto? Conversation { get; init; }

    /// <summary>FR-347, FR-363: earlier stages the agent may route back to.</summary>
    public IReadOnlyList<string> Routes { get; init; } = [];

    /// <summary>FR-344, FR-363: whether the stage has veto checks, and where a veto goes (<c>null</c>: the run ends vetoed).</summary>
    public bool Veto { get; init; }

    public string? OnVetoTo { get; init; }
}

/// <summary>The gate a stage raises: type key, the approval modes it applies in and the artifact it approves.</summary>
public sealed record PipelineStageGateDto(string Type, IReadOnlyList<ApprovalMode> Modes, string? Approves);

/// <summary>A way out of a stage: <c>kind</c> is <c>next</c>, <c>on_success</c>, <c>on_reject</c> or <c>loop</c>.
/// <c>on</c> is a loop's outcome; <c>maxIterations</c> its fixed cap, or <c>maxIterationsSetting</c> the setting that
/// holds it.</summary>
public sealed record PipelineStageTransitionDto(string Kind, string To, string? On, string? Title, int? MaxIterations, string? MaxIterationsSetting);

/// <summary>An artifact a definition produces or reads.</summary>
public sealed record PipelineArtifactDefinitionDto(string Key, string Title, ArtifactFormat Format)
{
    /// <summary>FR-352: <c>project</c> for a source-of-truth artifact people edit and runs pin; <c>run</c> otherwise.</summary>
    public ArtifactScope Scope { get; init; }

    /// <summary>FR-335, FR-362: supplied from outside the run (at start, by Submit input or a trigger).</summary>
    public bool Input { get; init; }

    /// <summary>FR-279, FR-362: the JSON schema a JSON artifact is checked against (the pack's schema document), or
    /// <c>null</c>. References to the pack's other schema files are left as written; the server checks inputs in full.</summary>
    public System.Text.Json.JsonElement? Schema { get; init; }
}

/// <summary>A pack status and the generic status it maps to (§4.57).</summary>
public sealed record PipelineStatusDefinitionDto(string Name, GenericRunStatus Generic);

/// <summary>FR-298: one (agent key, prompt version) group of a pipeline's agent runs; FR-353: <c>outcomes</c>, the mean outcome score and scored-target count per metric.</summary>
public sealed record PromptStatsDto(
    string AgentKey,
    string PromptVersion,
    int Runs,
    double AvgInputTokens,
    double AvgOutputTokens,
    decimal AvgCost,
    double? AvgLatencyMs,
    double AvgDurationMs,
    double FailureRate,
    IReadOnlyList<PromptOutcomeDto> Outcomes);

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
    int AgentRuns)
{
    /// <summary>FR-352, §4.76: the project-scope artifact versions the run pinned at its start (every stage reads them).</summary>
    public IReadOnlyList<PipelineRunPinDto> PinnedArtifacts { get; init; } = [];

    /// <summary>FR-354, FR-363: the run's follow-ups in declared order, with due times once the run completed.</summary>
    public IReadOnlyList<PipelineRunFollowUpDto> FollowUps { get; init; } = [];
}

/// <summary>FR-352: a project-scope artifact version a run pinned.</summary>
public sealed record PipelineRunPinDto(string Key, Guid ArtifactId, int Version);

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
    long? DurationMs)
{
    /// <summary>FR-355, FR-363: a map stage's progress over its newest entry's items; <c>null</c> for other stages and
    /// before the map has opened its items.</summary>
    public PipelineMapProgressDto? Map { get; init; }

    /// <summary>FR-349, FR-363: the variant (<c>v1</c>…) a variants stage promoted on its newest entry; <c>null</c> before
    /// one was chosen and for other stages.</summary>
    public string? ChosenVariant { get; init; }
}

/// <summary>FR-355, FR-363: a map execution's items: how many there are, have completed and have failed (the rest are
/// open). <c>executionId</c> pages them (<c>ListPipelineRunMapItems</c>).</summary>
public sealed record PipelineMapProgressDto(Guid ExecutionId, int Total, int Completed, int Failed);

/// <summary>FR-355, FR-363: one map item: its execution, index, status (<c>open</c>, <c>completed</c>, <c>failed</c>),
/// times, its newest agent run and its output version, when it has them.</summary>
public sealed record PipelineMapItemDto(
    Guid ExecutionId,
    int ItemIndex,
    MapItemStatus Status,
    DateTimeOffset EnteredAt,
    DateTimeOffset? ExitedAt,
    Guid? AgentRunId,
    AgentRunStatus? AgentRunStatus,
    string? Error,
    Guid? ArtifactId,
    string? ArtifactKey,
    int? ArtifactVersion);

/// <summary>FR-363: a page of a map execution's items in index order, with the map's progress; <c>next</c> is the cursor
/// of the following page, or <c>null</c>.</summary>
public sealed record PipelineMapItemPageDto(PipelineMapProgressDto Progress, IReadOnlyList<PipelineMapItemDto> Items, string? Next);

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
    bool Overdue)
{
    /// <summary>FR-364: what the gate asks to approve (variants, a tool call or an external payload), shown to everyone who
    /// can see the gate; <c>null</c> for gates without one (the SDLC's and definition gates).</summary>
    public PipelineGatePayloadDto? Payload { get; init; }
}

/// <summary>Approve or reject a gate through the engine (FR-301): the same fields and limits as the project gate endpoints.</summary>
public sealed record DecidePipelineGateRequest
{
    public const int CommentMaxLength = 4000;

    public const int ChoiceMaxLength = 16;

    [MaxLength(CommentMaxLength)]
    public string? Comment { get; init; }

    public EscalationDecision? Decision { get; init; }

    [Range(1, int.MaxValue)]
    public int? TargetCheckpoint { get; init; }

    /// <summary>FR-349: the chosen variant (v1…vn) when approving a <c>variant_select</c> gate; ignored by other gates.</summary>
    [MaxLength(ChoiceMaxLength)]
    public string? Choice { get; init; }
}

/// <summary>FR-302: a pending gate in the inbox, with its project and pipeline. <c>canDecide</c> is whether the caller
/// may approve or reject it (the decision endpoints' rule; FR-307: today only project Owners and Admins).</summary>
public sealed record PipelineGateInboxItemDto(PipelineGateDto Gate, string ProjectName, string PipelineId, string PipelineTitle, bool CanDecide);

/// <summary>A page of the gate inbox; <c>next</c> is the cursor of the following page, or <c>null</c>.</summary>
public sealed record PipelineGateInboxPageDto(IReadOnlyList<PipelineGateInboxItemDto> Items, string? Next);

/// <summary>FR-301: an artifact key of a run with its versions (newest first).</summary>
public sealed record PipelineArtifactDto(string Key, string Title, IReadOnlyList<PipelineArtifactVersionSummaryDto> Versions);

/// <summary>One artifact version in a run's artifact list.</summary>
public sealed record PipelineArtifactVersionSummaryDto(Guid Id, int Version, ArtifactStatus Status, string Hash, DateTimeOffset CreatedAt)
{
    /// <summary>FR-349, FR-354, FR-363: the variant label (<c>v1</c>…, a fan-out branch, <c>followup:&lt;duration&gt;</c>).</summary>
    public string? Variant { get; init; }

    /// <summary>FR-355, FR-363: the map item index of an item's output.</summary>
    public int? ItemIndex { get; init; }
}

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
