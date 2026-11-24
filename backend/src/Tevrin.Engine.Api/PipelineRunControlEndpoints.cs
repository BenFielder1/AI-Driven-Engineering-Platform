using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Runtime.Runs;

namespace Tevrin.Engine.Api;

/// <summary>FR-331: start a run of an engine-controlled pipeline.</summary>
public sealed record StartPipelineRunRequest
{
    /// <summary>The pipeline id (e.g. <c>fixture-linear</c>).</summary>
    [Required]
    [MaxLength(100)]
    public string PipelineId { get; init; } = string.Empty;

    /// <summary>The run's approval mode; the project's when omitted.</summary>
    public ApprovalMode? ApprovalMode { get; init; }

    /// <summary>Input artifacts (declared <c>input: true</c>) by key, each stored as version 1.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Inputs { get; init; }
}

/// <summary>FR-333: roll a run back to an earlier checkpoint.</summary>
public sealed record RollbackPipelineRunRequest
{
    public const int ReasonMaxLength = 2000;

    [Required]
    [Range(1, int.MaxValue)]
    public int TargetCheckpoint { get; init; }

    [Required]
    [MaxLength(ReasonMaxLength)]
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// FR-331, FR-333, FR-335: run control for engine-controlled pipelines (SPEC §6). A host-controlled pipeline (the SDLC)
/// gives 409 <c>RUN_CONTROLLED_BY_HOST</c>, a final run 409 <c>RUN_NOT_ACTIVE</c>.
/// </summary>
public static partial class PipelineRunEndpoints
{
    private static void MapRunControl(IEndpointRouteBuilder app, RouteGroupBuilder runs)
    {
        app.MapPost("/api/projects/{id:guid}/pipeline-runs", StartRun)
            .WithTags("Pipeline runs")
            .RequireAuthorization()
            .WithName("StartPipelineRun")
            .WithSummary("FR-331: start a run of an engine-controlled pipeline (Contributor). 404 NOT_FOUND for an unknown pipeline, 409 RUN_CONTROLLED_BY_HOST or RUN_ALREADY_ACTIVE, 400 VALIDATION (undeclared input key) or INPUT_INVALID (details: the failing paths).")
            .Produces<PipelineRunDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        runs.MapPost("/cancel", CancelRun).WithName("CancelPipelineRun")
            .WithSummary("FR-333: end the run cancelled and cancel its queued jobs (Owner). 403 FORBIDDEN, 409 RUN_NOT_ACTIVE or RUN_CONTROLLED_BY_HOST.")
            .Produces<PipelineRunDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        runs.MapPost("/rollback", RollbackRun).WithName("RollbackPipelineRun")
            .WithSummary("FR-333: roll the run back to a checkpoint by the engine rollback rules (Owner). 400 VALIDATION or INVALID_TARGET_VERSION, 403 FORBIDDEN, 409 RUN_NOT_ACTIVE or RUN_CONTROLLED_BY_HOST.")
            .Produces<PipelineRunDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        runs.MapPost("/retry", RetryRun).WithName("RetryPipelineRun")
            .WithSummary("FR-333: re-queue the run's failed stage (Contributor). 403 FORBIDDEN, 409 RUN_NOT_ACTIVE, RUN_CONTROLLED_BY_HOST or CONFLICT (the stage is still queued or running).")
            .Produces<PipelineRunDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        runs.MapPost("/inputs/{artifactKey}", SubmitInput).WithName("SubmitPipelineRunInput")
            .WithSummary("FR-335: add a version of an input artifact; a run waiting on input continues (Contributor). 400 INPUT_INVALID (details: the failing paths), 403 FORBIDDEN, 404 NOT_FOUND for an unknown input, 409 RUN_NOT_ACTIVE or RUN_CONTROLLED_BY_HOST.")
            .Accepts<JsonElement>("application/json")
            .Produces<PipelineArtifactVersionDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Results<Created<PipelineRunDto>, ProblemHttpResult>> StartRun(
        Guid id, StartPipelineRunRequest? request, IRunControlAccess access, PipelineRunStarter starter, PipelineRunReader reader,
        IEngineProblems problems, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PipelineId))
        {
            return problems.FromError(PipelineRequests.Invalid("pipelineId", "pipelineId is required."));
        }

        if (await access.CheckAsync(id, RunControlAction.Start, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        var mode = await access.ProjectApprovalModeAsync(id, cancellationToken);
        var started = await starter.StartAsync(
            new StartPipelineRun(id, request.PipelineId, request.ApprovalMode, mode, request.Inputs, access.UserId), cancellationToken);
        if (started.IsFailure)
        {
            return problems.FromError(started.Error!);
        }

        return TypedResults.Created($"/api/pipeline-runs/{started.Value.Id}", await reader.ToDtoAsync(started.Value, cancellationToken));
    }

    private static Task<Results<Ok<PipelineRunDto>, ProblemHttpResult>> CancelRun(
        Guid runId, IRunControlAccess access, EngineRunControl control, PipelineRunReader reader, IEngineProblems problems, CancellationToken cancellationToken) =>
        ControlAsync(runId, RunControlAction.Cancel, access, control, reader, problems, run => control.CancelAsync(run, cancellationToken), cancellationToken);

    private static Task<Results<Ok<PipelineRunDto>, ProblemHttpResult>> RetryRun(
        Guid runId, IRunControlAccess access, EngineRunControl control, PipelineRunReader reader, IEngineProblems problems, CancellationToken cancellationToken) =>
        ControlAsync(runId, RunControlAction.Retry, access, control, reader, problems, run => control.RetryAsync(run, cancellationToken), cancellationToken);

    private static async Task<Results<Ok<PipelineRunDto>, ProblemHttpResult>> RollbackRun(
        Guid runId, RollbackPipelineRunRequest? request, IRunControlAccess access, EngineRunControl control, PipelineRunReader reader,
        IEngineProblems problems, CancellationToken cancellationToken)
    {
        if (request is null || request.TargetCheckpoint < 1)
        {
            return problems.FromError(PipelineRequests.Invalid("targetCheckpoint", "targetCheckpoint must be 1 or more."));
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > RollbackPipelineRunRequest.ReasonMaxLength)
        {
            return problems.FromError(PipelineRequests.Invalid("reason", $"reason is required (at most {RollbackPipelineRunRequest.ReasonMaxLength} characters)."));
        }

        return await ControlAsync(
            runId, RunControlAction.Rollback, access, control, reader, problems,
            run => control.RollbackAsync(run, request.TargetCheckpoint, request.Reason.Trim(), cancellationToken), cancellationToken);
    }

    private static async Task<Results<Created<PipelineArtifactVersionDto>, ProblemHttpResult>> SubmitInput(
        Guid runId, string artifactKey, JsonElement body, IRunControlAccess access, EngineRunControl control, PipelineRunReader reader,
        IEngineProblems problems, CancellationToken cancellationToken)
    {
        var (run, problem) = await ControlledRunAsync(runId, RunControlAction.SubmitInput, access, control, problems, cancellationToken);
        if (run is null)
        {
            return problem!;
        }

        var submitted = await control.SubmitInputAsync(run, artifactKey, body, access.UserId, cancellationToken);
        if (submitted.IsFailure)
        {
            return problems.FromError(submitted.Error!);
        }

        var dto = await reader.ArtifactVersionAsync(run, artifactKey, submitted.Value.Version, cancellationToken);
        return TypedResults.Created($"/api/pipeline-runs/{run.Id}/artifacts/{artifactKey}/versions/{submitted.Value.Version}", dto);
    }

    private static async Task<Results<Ok<PipelineRunDto>, ProblemHttpResult>> ControlAsync(
        Guid runId, RunControlAction action, IRunControlAccess access, EngineRunControl control, PipelineRunReader reader,
        IEngineProblems problems, Func<PipelineRun, Task<Result<PipelineRun>>> act, CancellationToken cancellationToken)
    {
        var (run, problem) = await ControlledRunAsync(runId, action, access, control, problems, cancellationToken);
        if (run is null)
        {
            return problem!;
        }

        var done = await act(run);
        return done.IsFailure ? problems.FromError(done.Error!) : TypedResults.Ok(await reader.ToDtoAsync(done.Value, cancellationToken));
    }

    /// <summary>The tracked run when it exists and the caller may do <paramref name="action"/> on its project (404 for a
    /// project they can't see, 403 below the role).</summary>
    private static async Task<(PipelineRun? Run, ProblemHttpResult? Problem)> ControlledRunAsync(
        Guid runId, RunControlAction action, IRunControlAccess access, EngineRunControl control, IEngineProblems problems, CancellationToken cancellationToken)
    {
        var run = await control.FindAsync(runId, cancellationToken);
        if (run is null)
        {
            return (null, problems.FromError(new DomainError(EngineErrorCodes.NotFound, "The pipeline run was not found.")));
        }

        if (await access.CheckAsync(run.ProjectId, action, cancellationToken) is { } denied)
        {
            return (null, problems.FromError(denied.Code == EngineErrorCodes.NotFound
                ? new DomainError(EngineErrorCodes.NotFound, "The pipeline run was not found.")
                : denied));
        }

        return (run, null);
    }
}
