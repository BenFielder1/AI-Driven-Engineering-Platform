using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Execution;

namespace Aide.Api.Features.Execution;

/// <summary>
/// One step of an execution run (FR-57): number, title, the specialist that runs it (<c>agentKey</c>, FR-121; null for the
/// base Code Generator), status, attempt count, declared vs produced files, and the commit once accepted
/// (<c>commitUrl</c> is null for the Local provider). <c>legacyAgentKey</c> (§4.23): the v2 agent key a plan locked before
/// the upgrade named, which now runs as <c>agentKey</c>; null otherwise.
/// </summary>
public sealed record ExecutionStepSummaryDto(
    Guid Id,
    int Number,
    string Title,
    string? AgentKey,
    StepStatus Status,
    int Attempt,
    IReadOnlyList<DeclaredFileDto> DeclaredFiles,
    IReadOnlyList<FileManifestEntryDto> ProducedFiles,
    string? CommitSha,
    string? CommitUrl,
    string? LegacyAgentKey = null);

/// <summary>
/// One execution run (FR-57): the steps of one plan version. <c>id</c> is the plan artifact's id. The development run is
/// the implementation plan's; a review-fix run is the optimisation plan of review <c>cycle</c>. A run of an invalidated
/// plan is <c>superseded</c> (status <c>superseded</c>, with <c>supersededReason</c>). <c>tokens</c> is input + output
/// tokens of the steps' agent runs; <c>commitCount</c> the steps committed. <c>complianceReportId</c> is the report whose
/// optimisation plan this is (null for the development run): cycle numbers restart after a rollback, report ids don't.
/// </summary>
public sealed record ExecutionRunDto(
    Guid Id,
    ArtifactType PlanType,
    int PlanVersion,
    Guid ReleaseVersionId,
    int? Cycle,
    PlanRunStatus Status,
    bool Superseded,
    string? SupersededReason,
    int StepCount,
    int CompletedCount,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int CommitCount,
    long Tokens,
    IReadOnlyList<ExecutionStepSummaryDto> Steps,
    Guid? ComplianceReportId);

/// <summary>The agent run of a step attempt (FR-57): agent, specialist key, model, tokens, duration and status.</summary>
public sealed record StepAttemptRunDto(
    Guid Id,
    AgentKind Agent,
    string? AgentKey,
    string Model,
    int InputTokens,
    int OutputTokens,
    long DurationMs,
    AgentRunStatus Status);

/// <summary>
/// A repository tool call made during an agent run (FR-71): the tool name, its arguments as given by the model, the bytes
/// returned and whether the result was cut to the tool's limit. <c>error</c> is the code of a refused call
/// (<c>UNSAFE_PATH</c>, <c>TOOL_LIMIT</c>, <c>NOT_FOUND</c>, …), else <c>null</c>.
/// </summary>
public sealed record ToolCallDto(string Tool, JsonElement Arguments, int Bytes, bool Truncated, string? Error);

/// <summary>
/// One attempt of an execution step (FR-57): its outcome, the Guardian errors (<c>violations</c>) and <c>warnings</c>, the
/// files an accepted attempt committed, its agent run (null when the run row is gone) and that run's tool calls.
/// </summary>
public sealed record StepAttemptDto(
    int Attempt,
    StepAttemptOutcome Outcome,
    DateTimeOffset CreatedAt,
    IReadOnlyList<StepViolationDto> Violations,
    IReadOnlyList<StepViolationDto> Warnings,
    IReadOnlyList<FileManifestEntryDto> ProducedFiles,
    StepAttemptRunDto? Run,
    IReadOnlyList<ToolCallDto> ToolCalls);
