using System.Text.Json.Nodes;
using Aide.Api.Features.Activity;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;

namespace Aide.Api.Features.Admin;

/// <summary>
/// One entry of the cross-project admin event log (FR-51). <c>projectId</c>/<c>projectName</c> are <c>null</c> for events
/// without a project (e.g. <c>PROJECT_DELETED</c>, whose project id is in the payload). <c>type</c> is the stored event
/// type, <c>category</c> its activity-feed category (<c>null</c> for operations markers such as
/// <c>DEAD_LETTER_RETRIED</c>), <c>outcome</c> whether it records a failure or alert, <c>summary</c> a one-line
/// description and <c>payload</c> the raw event payload.
/// </summary>
public sealed record AdminEventDto(
    Guid Id,
    Guid? ProjectId,
    string? ProjectName,
    string Type,
    EventCategory? Category,
    EventOutcome Outcome,
    ActorDto Actor,
    string Summary,
    JsonObject Payload,
    DateTimeOffset CreatedAt);

/// <summary>
/// One agent run in the cross-project admin run log (FR-23, FR-51): the project, agent, phase, step, provider, model,
/// prompt template version, status, provider attempts, token counts, estimated cost in USD, duration and error. Input
/// artifacts, feedback and output are on the project's run detail (<c>GET /api/projects/{id}/runs/{runId}</c>).
/// </summary>
public sealed record AdminAgentRunDto(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    AgentKind Agent,
    Phase Phase,
    Guid? StepId,
    LlmProvider Provider,
    string Model,
    string PromptVersion,
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
/// A dead-lettered job (FR-51, §4.9): its type, project, status (<c>dead</c>, or <c>retried</c> once an Admin retried
/// it), attempts made, last error and payload; <c>deadLetteredAt</c> is when it ran out of attempts.
/// </summary>
public sealed record DeadLetterDto(
    Guid Id,
    string Type,
    Guid? ProjectId,
    string? ProjectName,
    JobStatus Status,
    int Attempts,
    string? LastError,
    JsonObject Payload,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeadLetteredAt);

/// <summary>A background job (§4.9): the job queued by a dead-letter retry (FR-51).</summary>
public sealed record JobDto(
    Guid Id,
    string Type,
    Guid? ProjectId,
    JobStatus Status,
    int Attempts,
    DateTimeOffset RunAfter,
    DateTimeOffset CreatedAt)
{
    public static JobDto From(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new JobDto(job.Id, job.Type, job.ProjectId, job.Status, job.Attempts, job.RunAfter, job.CreatedAt);
    }
}
