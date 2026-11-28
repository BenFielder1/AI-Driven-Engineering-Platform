using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Runs;

namespace Tevrin.Engine.Api;

/// <summary>FR-351: a human feedback entry for an agent.</summary>
public sealed record CreateAgentFeedbackRequest
{
    /// <summary>The feedback, 1–2,000 characters.</summary>
    public string? Text { get; init; }
}

/// <summary>FR-351: one agent feedback entry (<c>human</c> or <c>stage</c>).</summary>
public sealed record AgentFeedbackDto(
    Guid Id,
    string PipelineId,
    string AgentKey,
    AgentFeedbackSource Source,
    string Text,
    Guid? RunId,
    Guid? AgentRunId,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt);

/// <summary>
/// FR-351, §4.75: the agent feedback store. Entries for an agent key of a pipeline in a project are injected into the
/// agent's prompt by stages with <c>feedback.inject</c>; people list, add and delete them here.
/// </summary>
public static partial class PipelineRunEndpoints
{
    private static void MapAgentFeedback(IEndpointRouteBuilder app)
    {
        var feedback = app.MapGroup("/api/projects/{id:guid}")
            .WithTags("Pipeline runs")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        feedback.MapGet("/pipelines/{pipelineId}/agents/{agentKey}/feedback", ListAgentFeedback).WithName("ListAgentFeedback")
            .WithSummary("FR-351: an agent's feedback entries in the project, newest first, deleted ones left out (project viewer). 404 NOT_FOUND for an unknown pipeline or agent key.")
            .Produces<IReadOnlyList<AgentFeedbackDto>>();
        feedback.MapPost("/pipelines/{pipelineId}/agents/{agentKey}/feedback", CreateAgentFeedback).WithName("CreateAgentFeedback")
            .WithSummary("FR-351: add a human feedback entry for an agent (Contributor). 400 VALIDATION (text blank or over 2,000 characters), 403 FORBIDDEN, 404 NOT_FOUND.")
            .Produces<AgentFeedbackDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        feedback.MapDelete("/agent-feedback/{feedbackId:guid}", DeleteAgentFeedback).WithName("DeleteAgentFeedback")
            .WithSummary("FR-351: delete a feedback entry; it is never injected again (Owner, or the entry's author as a Contributor). 403 FORBIDDEN, 404 NOT_FOUND.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<Results<Ok<IReadOnlyList<AgentFeedbackDto>>, ProblemHttpResult>> ListAgentFeedback(
        Guid id, string pipelineId, string agentKey, IRunControlAccess access, AgentFeedbackEntries entries, IEngineProblems problems,
        CancellationToken cancellationToken)
    {
        if (await access.CheckAsync(id, RunControlAction.ViewFeedback, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        var listed = await entries.ListAsync(id, pipelineId, agentKey, cancellationToken);
        return listed.IsFailure
            ? problems.FromError(listed.Error!)
            : TypedResults.Ok<IReadOnlyList<AgentFeedbackDto>>([.. listed.Value.Select(ToFeedbackDto)]);
    }

    private static async Task<Results<Created<AgentFeedbackDto>, ProblemHttpResult>> CreateAgentFeedback(
        Guid id, string pipelineId, string agentKey, CreateAgentFeedbackRequest? request, IRunControlAccess access,
        AgentFeedbackEntries entries, IEngineProblems problems, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return problems.FromError(PipelineRequests.Invalid("text", "text is required."));
        }

        if (await access.CheckAsync(id, RunControlAction.AddFeedback, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        var created = await entries.CreateAsync(id, pipelineId, agentKey, request.Text, access.UserId, cancellationToken);
        return created.IsFailure
            ? problems.FromError(created.Error!)
            : TypedResults.Created($"/api/projects/{id}/pipelines/{pipelineId}/agents/{agentKey}/feedback", ToFeedbackDto(created.Value));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAgentFeedback(
        Guid id, Guid feedbackId, IRunControlAccess access, AgentFeedbackEntries entries, IEngineProblems problems,
        CancellationToken cancellationToken)
    {
        if (await access.CheckAsync(id, RunControlAction.ViewFeedback, cancellationToken) is { } hidden)
        {
            return problems.FromError(hidden);
        }

        if (await entries.FindAsync(id, feedbackId, cancellationToken) is not { } entry)
        {
            return problems.FromError(new DomainError(EngineErrorCodes.NotFound, "The feedback entry was not found."));
        }

        // The author deletes their own entry as a contributor; anyone else's needs the owner.
        var own = entry.CreatedBy is { } author && author == access.UserId;
        if (await access.CheckAsync(id, own ? RunControlAction.AddFeedback : RunControlAction.ManageFeedback, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        await entries.DeleteAsync(entry, cancellationToken);
        return TypedResults.NoContent();
    }

    private static AgentFeedbackDto ToFeedbackDto(AgentFeedback f) =>
        new(f.Id, f.PipelineId, f.AgentKey, f.Source, f.Text, f.RunId, f.AgentRunId, f.CreatedBy, f.CreatedAt);
}
