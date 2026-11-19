using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Api;

public static partial class PipelineRunEndpoints
{
    private const string LoggerCategory = "Tevrin.Engine.Api.PipelineGates";

    private static Task<Results<Ok<PipelineGateDto>, ProblemHttpResult>> ApproveGate(
        Guid runId, Guid gateId, DecidePipelineGateRequest? request, HttpContext httpContext, PipelineRunReader reader,
        IEngineDbContext db, IGateDecisionService decisions, IEngineProblems problems, ILoggerFactory loggers, CancellationToken cancellationToken) =>
        DecideAsync(
            runId, gateId, request, httpContext, reader, db, decisions, problems, loggers,
            (projectId, decision) => decisions.ApproveAsync(projectId, gateId, decision, cancellationToken),
            cancellationToken);

    private static Task<Results<Ok<PipelineGateDto>, ProblemHttpResult>> RejectGate(
        Guid runId, Guid gateId, DecidePipelineGateRequest? request, HttpContext httpContext, PipelineRunReader reader,
        IEngineDbContext db, IGateDecisionService decisions, IEngineProblems problems, ILoggerFactory loggers, CancellationToken cancellationToken) =>
        DecideAsync(
            runId, gateId, request, httpContext, reader, db, decisions, problems, loggers,
            (projectId, decision) => decisions.RejectAsync(projectId, gateId, decision, cancellationToken),
            cancellationToken);

    /// <summary>
    /// FR-301: the request must be valid (400), the run exist and the caller be allowed to decide on its project (404/403,
    /// the project endpoint's rule and order), the gate belong to the run (404) and the run's pinned definition be
    /// runnable (409 DEFINITION_UNAVAILABLE); then the host's gate service decides, with its own rules, errors,
    /// follow-ups and audit.
    /// </summary>
    private static async Task<Results<Ok<PipelineGateDto>, ProblemHttpResult>> DecideAsync(
        Guid runId,
        Guid gateId,
        DecidePipelineGateRequest? request,
        HttpContext httpContext,
        PipelineRunReader reader,
        IEngineDbContext db,
        IGateDecisionService decisions,
        IEngineProblems problems,
        ILoggerFactory loggers,
        Func<Guid, PipelineGateDecision, Task<Result<ApprovalGate>>> decide,
        CancellationToken cancellationToken)
    {
        // The project gate route's order: the request (400), then the decide permission (404 for a project the caller
        // can't see, 403 below Owner), then the gate (404); then this route's definition check (409).
        request ??= new DecidePipelineGateRequest();
        if (request.Comment is { Length: > DecidePipelineGateRequest.CommentMaxLength })
        {
            return problems.FromError(PipelineRequests.Invalid("comment", $"comment must be at most {DecidePipelineGateRequest.CommentMaxLength} characters."));
        }

        if (request.TargetCheckpoint is < 1)
        {
            return problems.FromError(PipelineRequests.Invalid("targetCheckpoint", "targetCheckpoint must be 1 or more."));
        }

        var run = await reader.FindRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return problems.FromError(new DomainError(EngineErrorCodes.NotFound, "The pipeline run was not found."));
        }

        if (await decisions.CheckDecideAsync(run.ProjectId, cancellationToken) is { } denied)
        {
            return problems.FromError(denied.Code == EngineErrorCodes.NotFound
                ? new DomainError(EngineErrorCodes.NotFound, "The pipeline run was not found.")
                : denied);
        }

        if (!await db.ApprovalGates.AsNoTracking().AnyAsync(g => g.Id == gateId && g.PipelineRunId == run.Id, cancellationToken))
        {
            return problems.FromError(new DomainError(EngineErrorCodes.NotFound, "The gate was not found in this run."));
        }

        var definition = await reader.DefinitionAsync(run, cancellationToken);
        if (definition is not { Available: true })
        {
            LogDefinitionUnavailable(loggers.CreateLogger(LoggerCategory), run.Id, run.DefinitionHash, gateId);
            return problems.FromError(new DomainError(
                EngineErrorCodes.DefinitionUnavailable,
                "This run uses a pipeline definition this version of Tevrin can't run."));
        }

        var decided = await decide(run.ProjectId, new PipelineGateDecision(request.Comment, request.Decision, request.TargetCheckpoint));
        if (decided.IsFailure)
        {
            if (decided.Error!.RetryAfter is { } wait)
            {
                httpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

            return problems.FromError(decided.Error!);
        }

        var dtos = await reader.ToGateDtosAsync([decided.Value], cancellationToken);
        return TypedResults.Ok(dtos[0]);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "DEFINITION_UNAVAILABLE: run {RunId} is pinned to definition {DefinitionHash}, which this build can't run (gate {GateId})")]
    private static partial void LogDefinitionUnavailable(ILogger logger, Guid runId, string definitionHash, Guid gateId);
}
