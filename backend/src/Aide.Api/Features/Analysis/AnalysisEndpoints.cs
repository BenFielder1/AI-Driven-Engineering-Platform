using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Access;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Analysis;

/// <summary>
/// FR-180–FR-185: the Analysis phase (§6 <c>/api/projects/:id/analysis</c>). Reading is for members; re-running the Stack
/// Analyst is for writers. The profile itself is edited, approved and rejected through the generic artifact routes
/// (<c>POST /artifacts/stack_profile</c>, <c>/artifacts/:artifactId/approve|reject</c>), and the phase is left with
/// <c>POST /advance</c> (FR-185).
/// </summary>
public static class AnalysisEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}/analysis")
            .WithTags("Analysis")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/", GetAnalysis)
            .WithName("GetAnalysis")
            .WithSummary("The Analysis page of a release version (default: the project's current release version; a past version's page shows that version's summary, run and profile, read-only), members (FR-182): the Repository Analyst's summary state and, for a version that infers its stack, the Stack Analyst's run, the latest stack_profile parsed, the layers that need input, whether it can be approved (and why not), whether the project can continue to refinement, the detector's profile for \"Fill in manually\", the profile as a /api/stack/options selection, the pinned and previous compositions and the deployment state.")
            .Produces<AnalysisDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project, or the release version in this project."));

        group.MapPost("/run", RunStackAnalysis)
            .WithName("RunStackAnalysis")
            .WithSummary("Run the Stack Analyst again (Contributor or Owner, FR-180 Retry or re-run): queues it on the version's base commit; the new stack_profile draft arrives with the run. 409 WRONG_PHASE, BUDGET_EXCEEDED, INVALID_STATE.")
            .Produces<StackAnalysisQueuedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status403Forbidden, "FORBIDDEN: Viewers can't run agents."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
                (StatusCodes.Status409Conflict, "WRONG_PHASE (not in analysis, or the version doesn't infer its stack), BUDGET_EXCEEDED (token budget used up), INVALID_STATE (the Stack Analyst is queued or running) or CONFLICT (a concurrent start)."));

        return app;
    }

    private static async Task<Results<Ok<AnalysisDto>, ProblemHttpResult, StatusCodeHttpResult>> GetAnalysis(
        Guid id,
        IProjectAccess access,
        AnalysisService analysis,
        CancellationToken cancellationToken,
        Guid? releaseVersionId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var result = await analysis.GetAsync(id, cancellationToken, releaseVersionId);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : ApiProblems.FromError(result.Error!);
    }

    private static async Task<Results<Accepted<StackAnalysisQueuedDto>, ProblemHttpResult, StatusCodeHttpResult>> RunStackAnalysis(
        Guid id,
        IProjectAccess access,
        ICurrentUser currentUser,
        AnalysisService analysis,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.RunAgents, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var queued = await analysis.RunAsync(id, currentUser.Actor, cancellationToken);
        return queued.IsSuccess
            ? TypedResults.Accepted($"/api/projects/{id}/analysis", queued.Value)
            : ApiProblems.FromError(queued.Error!);
    }
}
