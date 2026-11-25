using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Api;

/// <summary>
/// FR-300, FR-301, FR-303: pipeline runs, stages, timeline, artifacts, gates and replay (SPEC §6; D-0.7.0-4: reads plus
/// gate decisions). Every call checks that the caller can view the run's project (404 NOT_FOUND otherwise, §2).
/// </summary>
public static partial class PipelineRunEndpoints
{
    public static IEndpointRouteBuilder MapPipelineRunEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/projects/{id:guid}/pipeline-runs", ListRuns)
            .WithTags("Pipeline runs")
            .RequireAuthorization()
            .WithName("ListPipelineRuns")
            .WithSummary("FR-300: the project's pipeline runs, newest first (project viewer).")
            .Produces<IReadOnlyList<PipelineRunDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var runs = app.MapGroup("/api/pipeline-runs/{runId:guid}")
            .WithTags("Pipeline runs")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        runs.MapGet("/", GetRun).WithName("GetPipelineRun")
            .WithSummary("FR-300: a run with its stage, pack and generic status, checkpoint, definition hash, times, cost and token totals.")
            .Produces<PipelineRunDto>();
        runs.MapGet("/stages", ListStages).WithName("ListPipelineRunStages")
            .WithSummary("FR-300: the run's stages in definition order with state, iterations (entries) and timings.")
            .Produces<IReadOnlyList<PipelineStageDto>>();
        runs.MapGet("/timeline", GetTimeline).WithName("GetPipelineRunTimeline")
            .WithSummary("FR-300: the run's transitions, stage entries, agent runs, gate events and artifact versions in time order, cursor-paged (limit 1-200, default 50). 400 VALIDATION for a bad cursor or limit.")
            .Produces<PipelineTimelinePageDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
        runs.MapGet("/replay", GetReplay).WithName("GetPipelineRunReplay")
            .WithSummary("FR-303: the run's timeline with each agent run's input and output. Read-only. 400 VALIDATION for a bad cursor or limit.")
            .Produces<PipelineTimelinePageDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
        runs.MapGet("/artifacts", ListArtifacts).WithName("ListPipelineRunArtifacts")
            .WithSummary("FR-301: the run's artifacts by key with their versions, newest first.")
            .Produces<IReadOnlyList<PipelineArtifactDto>>();
        runs.MapGet("/artifacts/{key}/versions/{version:int}", GetArtifactVersion).WithName("GetPipelineRunArtifactVersion")
            .WithSummary("FR-301: one version of a run's artifact, with its content.")
            .Produces<PipelineArtifactVersionDto>();
        runs.MapGet("/artifacts/{key}/diff", DiffArtifact).WithName("DiffPipelineRunArtifact")
            .WithSummary("FR-301: the line diff between versions from and to of a run's artifact. 400 VALIDATION for missing versions or versions too large to diff.")
            .Produces<PipelineArtifactDiffDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
        runs.MapGet("/gates", ListGates).WithName("ListPipelineRunGates")
            .WithSummary("FR-301: the run's gates, newest first.")
            .Produces<IReadOnlyList<PipelineGateDto>>();

        runs.MapPost("/gates/{gateId:guid}/approve", ApproveGate).WithName("ApprovePipelineRunGate")
            .WithSummary("FR-301: approve a pending gate of the run, exactly as the project gate endpoint (same rules, errors, follow-ups and audit). 403 FORBIDDEN, 409 GATE_NOT_PENDING, PHASE_PRECONDITION_FAILED, WRONG_PHASE, ROLLBACK_BLOCKED, CONFLICT or DEFINITION_UNAVAILABLE, 400 VALIDATION, INVALID_DECISION or INVALID_TARGET_VERSION, 429 RATE_LIMITED.")
            .Produces<PipelineGateDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        runs.MapPost("/gates/{gateId:guid}/reject", RejectGate).WithName("RejectPipelineRunGate")
            .WithSummary("FR-301: reject a pending gate of the run, exactly as the project gate endpoint. 403 FORBIDDEN, 409 GATE_NOT_PENDING, CONFLICT or DEFINITION_UNAVAILABLE, 400 VALIDATION or INVALID_DECISION.")
            .Produces<PipelineGateDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        MapRunControl(app, runs);
        MapOutcomes(runs);
        MapTriggers(app);
        MapProjectArtifacts(app);
        MapAgentFeedback(app);
        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<PipelineRunDto>>, ProblemHttpResult>> ListRuns(
        Guid id, IRunAccessPolicy access, PipelineRunReader reader, IEngineProblems problems, CancellationToken cancellationToken)
    {
        if (await access.CheckViewAsync(id, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        return TypedResults.Ok(await reader.ListAsync(id, cancellationToken));
    }

    private static async Task<Results<Ok<PipelineRunDto>, ProblemHttpResult>> GetRun(
        Guid runId, IRunAccessPolicy access, PipelineRunReader reader, IEngineProblems problems, CancellationToken cancellationToken)
    {
        var (run, problem) = await VisibleRunAsync(runId, access, reader, problems, cancellationToken);
        return run is null ? problem! : TypedResults.Ok(await reader.ToDtoAsync(run, cancellationToken));
    }

    private static async Task<Results<Ok<IReadOnlyList<PipelineStageDto>>, ProblemHttpResult>> ListStages(
        Guid runId, IRunAccessPolicy access, PipelineRunReader reader, IEngineProblems problems, CancellationToken cancellationToken)
    {
        var (run, problem) = await VisibleRunAsync(runId, access, reader, problems, cancellationToken);
        return run is null ? problem! : TypedResults.Ok(await reader.StagesAsync(run, cancellationToken));
    }

    private static Task<Results<Ok<PipelineTimelinePageDto>, ProblemHttpResult>> GetTimeline(
        Guid runId, IRunAccessPolicy access, PipelineRunReader reader, PipelineTimelineReader timeline, IEngineProblems problems,
        CancellationToken cancellationToken, string? cursor = null, int? limit = null) =>
        PageAsync(runId, access, reader, timeline, problems, cursor, limit, replay: false, cancellationToken);

    private static Task<Results<Ok<PipelineTimelinePageDto>, ProblemHttpResult>> GetReplay(
        Guid runId, IRunAccessPolicy access, PipelineRunReader reader, PipelineTimelineReader timeline, IEngineProblems problems,
        CancellationToken cancellationToken, string? cursor = null, int? limit = null) =>
        PageAsync(runId, access, reader, timeline, problems, cursor, limit, replay: true, cancellationToken);

    private static async Task<Results<Ok<PipelineTimelinePageDto>, ProblemHttpResult>> PageAsync(
        Guid runId, IRunAccessPolicy access, PipelineRunReader reader, PipelineTimelineReader timeline, IEngineProblems problems,
        string? cursor, int? limit, bool replay, CancellationToken cancellationToken)
    {
        var (run, problem) = await VisibleRunAsync(runId, access, reader, problems, cancellationToken);
        if (run is null)
        {
            return problem!;
        }

        if (PipelineRequests.Paging(cursor, limit, out var after, out var size) is { } invalid)
        {
            return problems.FromError(invalid);
        }

        return TypedResults.Ok(await timeline.PageAsync(run.Id, after, size, replay, cancellationToken));
    }

    private static async Task<Results<Ok<IReadOnlyList<PipelineArtifactDto>>, ProblemHttpResult>> ListArtifacts(
        Guid runId, IRunAccessPolicy access, PipelineRunReader reader, IEngineProblems problems, CancellationToken cancellationToken)
    {
        var (run, problem) = await VisibleRunAsync(runId, access, reader, problems, cancellationToken);
        return run is null ? problem! : TypedResults.Ok(await reader.ArtifactsAsync(run, cancellationToken));
    }

    private static async Task<Results<Ok<PipelineArtifactVersionDto>, ProblemHttpResult>> GetArtifactVersion(
        Guid runId, string key, int version, IRunAccessPolicy access, PipelineRunReader reader, IEngineProblems problems, CancellationToken cancellationToken)
    {
        var (run, problem) = await VisibleRunAsync(runId, access, reader, problems, cancellationToken);
        if (run is null)
        {
            return problem!;
        }

        return await reader.ArtifactVersionAsync(run, key, version, cancellationToken) is { } dto
            ? TypedResults.Ok(dto)
            : problems.FromError(new DomainError(EngineErrorCodes.NotFound, $"Version {version} of {key} was not found in this run."));
    }

    private static async Task<Results<Ok<PipelineArtifactDiffDto>, ProblemHttpResult>> DiffArtifact(
        Guid runId, string key, IRunAccessPolicy access, PipelineRunReader reader, IEngineProblems problems,
        CancellationToken cancellationToken, int? from = null, int? to = null)
    {
        var (run, problem) = await VisibleRunAsync(runId, access, reader, problems, cancellationToken);
        if (run is null)
        {
            return problem!;
        }

        if (from is not ({ } older and >= 1) || to is not ({ } newer and >= 1))
        {
            return problems.FromError(PipelineRequests.Invalid(from is null or < 1 ? "from" : "to", "from and to must be version numbers (1 or more)."));
        }

        var diff = await reader.DiffAsync(run, key, older, newer, cancellationToken);
        return diff.IsFailure ? problems.FromError(diff.Error!) : TypedResults.Ok(diff.Value);
    }

    private static async Task<Results<Ok<IReadOnlyList<PipelineGateDto>>, ProblemHttpResult>> ListGates(
        Guid runId, IRunAccessPolicy access, PipelineRunReader reader, IEngineProblems problems, CancellationToken cancellationToken)
    {
        var (run, problem) = await VisibleRunAsync(runId, access, reader, problems, cancellationToken);
        return run is null ? problem! : TypedResults.Ok(await reader.GatesAsync(run, cancellationToken));
    }

    /// <summary>The run when it exists and the caller can view its project; else the 404 (or 401) problem.</summary>
    internal static async Task<(PipelineRun? Run, ProblemHttpResult? Problem)> VisibleRunAsync(
        Guid runId, IRunAccessPolicy access, PipelineRunReader reader, IEngineProblems problems, CancellationToken cancellationToken)
    {
        var run = await reader.FindRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return (null, problems.FromError(new DomainError(EngineErrorCodes.NotFound, "The pipeline run was not found.")));
        }

        if (await access.CheckViewAsync(run.ProjectId, cancellationToken) is { } denied)
        {
            return (null, problems.FromError(denied.Code == EngineErrorCodes.NotFound
                ? new DomainError(EngineErrorCodes.NotFound, "The pipeline run was not found.")
                : denied));
        }

        return (run, null);
    }
}
