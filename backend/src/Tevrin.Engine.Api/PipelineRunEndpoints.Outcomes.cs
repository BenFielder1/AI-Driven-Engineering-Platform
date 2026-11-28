using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Runtime.Runs;

namespace Tevrin.Engine.Api;

/// <summary>FR-353: external outcome scores of a run (list: Viewer; score: Contributor).</summary>
public static partial class PipelineRunEndpoints
{
    private static void MapOutcomes(RouteGroupBuilder runs)
    {
        runs.MapGet("/outcomes", ListOutcomes).WithName("ListPipelineRunOutcomes")
            .WithSummary("FR-353: the run's outcome scores, oldest first, optionally only those about one agent run (agentRunId) or one artifact (artifactKey, with version and variant). 400 VALIDATION for a bad filter.")
            .Produces<IReadOnlyList<OutcomeScoreDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
        runs.MapPost("/outcomes", CreateOutcome).WithName("CreatePipelineRunOutcome")
            .WithSummary("FR-353: store an external outcome score for an agent run or artifact version of this run (Contributor). probability_brier scores are (prediction − value)² (§4.74). 400 VALIDATION, 403 FORBIDDEN, 404 NOT_FOUND for a target not in this run.")
            .Produces<OutcomeScoreDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<Results<Ok<IReadOnlyList<OutcomeScoreDto>>, ProblemHttpResult>> ListOutcomes(
        Guid runId, IRunAccessPolicy access, PipelineRunReader reader, PipelineRunOutcomes outcomes, IEngineProblems problems,
        CancellationToken cancellationToken, Guid? agentRunId = null, string? artifactKey = null, int? version = null, string? variant = null)
    {
        var (run, problem) = await VisibleRunAsync(runId, access, reader, problems, cancellationToken);
        if (run is null)
        {
            return problem!;
        }

        var listed = await outcomes.ListAsync(run, agentRunId, artifactKey, version, variant, cancellationToken);
        return listed.IsFailure ? problems.FromError(listed.Error!) : TypedResults.Ok(listed.Value);
    }

    private static async Task<Results<Created<OutcomeScoreDto>, ProblemHttpResult>> CreateOutcome(
        Guid runId, CreateOutcomeScoreRequest? request, IRunControlAccess access, EngineRunControl control, PipelineRunOutcomes outcomes,
        IEngineProblems problems, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return problems.FromError(PipelineRequests.Invalid("target", "A score body is required."));
        }

        var (run, problem) = await ControlledRunAsync(runId, RunControlAction.EditArtifacts, access, control, problems, cancellationToken);
        if (run is null)
        {
            return problem!;
        }

        var created = await outcomes.CreateAsync(run, request, access.UserId, cancellationToken);
        return created.IsFailure
            ? problems.FromError(created.Error!)
            : TypedResults.Created($"/api/pipeline-runs/{run.Id}/outcomes", created.Value);
    }
}
