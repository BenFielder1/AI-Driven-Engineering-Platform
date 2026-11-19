using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Api;

/// <summary>
/// FR-300, FR-303: one timeline entry. Every entry has <c>id</c> (its source row), <c>at</c> and <c>kind</c>; the other
/// fields belong to kinds:
/// <list type="bullet">
/// <item><c>transition</c>: <c>fromStage</c>, <c>toStage</c>, <c>transitionKind</c>, <c>checkpoint</c>, <c>reason</c>.</item>
/// <item><c>stage_entered</c>: <c>stageId</c>, <c>iteration</c>, <c>checkpoint</c>, <c>exitedAt</c>, <c>outcome</c>.</item>
/// <item><c>agent_run</c>: <c>stageId</c>, <c>agentKey</c>, <c>agentRunStatus</c>, <c>model</c>, <c>promptVersion</c>,
/// tokens, <c>cost</c>, <c>durationMs</c>, <c>latencyMs</c>, <c>error</c>; in the replay also <c>input</c>,
/// <c>inputTruncated</c>, <c>output</c> and <c>outputTruncated</c>.</item>
/// <item><c>gate_created</c> / <c>gate_decided</c>: <c>gateId</c>, <c>gateType</c>, <c>gateStatus</c>, <c>decision</c>,
/// <c>comment</c>.</item>
/// <item><c>artifact_version</c>: <c>artifactId</c>, <c>artifactKey</c>, <c>artifactVersion</c>, <c>artifactStatus</c>,
/// <c>contentHash</c>, <c>agentRunId</c>.</item>
/// </list>
/// </summary>
public sealed record PipelineTimelineItemDto
{
    public required Guid Id { get; init; }

    public required DateTimeOffset At { get; init; }

    public required PipelineTimelineKind Kind { get; init; }

    public string? FromStage { get; init; }

    public string? ToStage { get; init; }

    public string? TransitionKind { get; init; }

    public int? Checkpoint { get; init; }

    public string? Reason { get; init; }

    public string? StageId { get; init; }

    public int? Iteration { get; init; }

    public DateTimeOffset? ExitedAt { get; init; }

    public string? Outcome { get; init; }

    public string? AgentKey { get; init; }

    public AgentRunStatus? AgentRunStatus { get; init; }

    public string? Model { get; init; }

    public string? PromptVersion { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public decimal? Cost { get; init; }

    public long? DurationMs { get; init; }

    public long? LatencyMs { get; init; }

    public string? Error { get; init; }

    /// <summary>Replay only: the rendered prompt and messages, redacted (D-0.7.0-16); <c>null</c> for runs before 0.7.0.</summary>
    public string? Input { get; init; }

    /// <summary>Replay only: the stored input was capped (1 MB, D-0.7.0-16) or this response cut it at
    /// <see cref="TimelineItems.MaxReplayTextLength"/> characters.</summary>
    public bool? InputTruncated { get; init; }

    /// <summary>Replay only: the agent's output text (at most <see cref="TimelineItems.MaxReplayTextLength"/> characters).</summary>
    public string? Output { get; init; }

    /// <summary>Replay only: the output was cut at <see cref="TimelineItems.MaxReplayTextLength"/> characters.</summary>
    public bool? OutputTruncated { get; init; }

    public Guid? GateId { get; init; }

    public string? GateType { get; init; }

    public GateStatus? GateStatus { get; init; }

    public EscalationDecision? Decision { get; init; }

    public string? Comment { get; init; }

    public Guid? ArtifactId { get; init; }

    public string? ArtifactKey { get; init; }

    public int? ArtifactVersion { get; init; }

    public ArtifactStatus? ArtifactStatus { get; init; }

    public string? ContentHash { get; init; }

    public Guid? AgentRunId { get; init; }
}

/// <summary>A page of a run's timeline (FR-300) or replay (FR-303); <c>next</c> is the cursor of the following page, or <c>null</c>.</summary>
public sealed record PipelineTimelinePageDto(IReadOnlyList<PipelineTimelineItemDto> Items, string? Next);
