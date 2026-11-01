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
/// USD, duration and error. <c>userFeedback</c> is the feedback or answers given to the agent. <c>releaseVersionId</c> is the
/// release version it ran for (§4.10). <c>checkpoint</c> is the checkpoint it ran at (null until recorded). <c>agentKey</c>
/// is the specialist that ran a plan step (FR-121; <c>generalist</c> for a generalist step of a composed project; null for
/// base agents); <c>presetId</c>/<c>presetVersion</c> are history (FR-129: the retired preset of runs before the upgrade).
/// <c>legacyAgentKey</c> (§4.23) is the v2 agent key the run's step was planned with when it ran as its mapped v4
/// specialist (e.g. <c>db-engineer</c> for <c>data-engineer</c>); null otherwise. <c>toolCallCount</c> is
/// the number of repo tool calls (FR-71). <c>credentialLabel</c> names the LLM connection that paid for the run,
/// <c>Label (••••a1b2)</c> (FR-79, D-V2-5; null for a system key, a provider without a key, or a deleted connection).
/// </summary>
public sealed record AgentRunDto(
    Guid Id,
    Guid? ReleaseVersionId,
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
    int? Checkpoint,
    string? AgentKey,
    string? PresetId,
    string? PresetVersion,
    int ToolCallCount,
    string? CredentialLabel,
    string? LegacyAgentKey = null);

/// <summary>
/// An agent run with the provider's output text (FR-23). <c>output</c> is <c>null</c> until the run succeeds and is cut
/// to the first 200,000 characters (<c>outputTruncated</c>). The v2 fields are as on <see cref="AgentRunDto"/>.
/// <c>toolCalls</c> is the run's repo tool log (FR-71: tool, arguments, bytes, truncated, error code) and <c>warnings</c>
/// what was recorded on it (FR-70, FR-71): <c>tools_unsupported</c>, <c>snapshot_unavailable</c>,
/// <c>summary_wait_timeout</c>, <c>summary_unavailable</c>, and <c>output_truncated</c> (FR-162). <c>continuations</c> is how
/// many continuations the answer took (0–5, FR-162) and <c>maxOutputTokens</c> the output token limit its calls used
/// (FR-160); both are <c>null</c> for runs stored before v4 and for runs that haven't succeeded.
/// </summary>
public sealed record AgentRunDetailDto(
    Guid Id,
    Guid? ReleaseVersionId,
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
    bool OutputTruncated,
    int? Checkpoint,
    string? AgentKey,
    string? PresetId,
    string? PresetVersion,
    int ToolCallCount,
    string? CredentialLabel,
    IReadOnlyList<Execution.ToolCallDto> ToolCalls,
    IReadOnlyList<string> Warnings,
    int? Continuations,
    int? MaxOutputTokens,
    string? LegacyAgentKey = null);
