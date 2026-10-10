using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;

namespace Aide.Api.Features.Runs;

/// <summary>An artifact version a run used as input (FR-23).</summary>
public sealed record RunInputArtifactDto(ArtifactType Type, int Version);

/// <summary>
/// An agent run (FR-23): which agent ran in which phase (and step), on which provider and model, with which prompt
/// template version and input artifact versions; its status, provider attempts (FR-25), token counts, estimated cost in
/// USD, duration and error. <c>userFeedback</c> is the feedback or answers given to the agent.
/// </summary>
public sealed record AgentRunDto(
    Guid Id,
    AgentKind Agent,
    Phase Phase,
    Guid? StepId,
    LlmProvider Provider,
    string Model,
    string PromptVersion,
    IReadOnlyList<RunInputArtifactDto> InputArtifacts,
    string? UserFeedback,
    AgentRunStatus Status,
    int Attempts,
    int InputTokens,
    int OutputTokens,
    decimal CostEstimate,
    long DurationMs,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// An agent run with the provider's output text (FR-23). <c>output</c> is <c>null</c> until the run succeeds and is cut
/// to the first 200,000 characters (<c>outputTruncated</c>).
/// </summary>
public sealed record AgentRunDetailDto(
    Guid Id,
    AgentKind Agent,
    Phase Phase,
    Guid? StepId,
    LlmProvider Provider,
    string Model,
    string PromptVersion,
    IReadOnlyList<RunInputArtifactDto> InputArtifacts,
    string? UserFeedback,
    AgentRunStatus Status,
    int Attempts,
    int InputTokens,
    int OutputTokens,
    decimal CostEstimate,
    long DurationMs,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Output,
    bool OutputTruncated);
