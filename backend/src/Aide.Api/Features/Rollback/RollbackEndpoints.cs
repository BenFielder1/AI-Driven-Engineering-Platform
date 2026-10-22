using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Access;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Rollback;

/// <summary>FR-44, FR-45, FR-63, §4.8, §4.10: rollback (§6 <c>/api/projects/:id/rollback</c>) and its history. The checkpoint picker is <c>GET /checkpoints</c>.</summary>
public static class RollbackEndpoints
{
    public static IEndpointRouteBuilder MapRollbackEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}")
            .WithTags("Rollback")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/rollback", RollbackProject)
            .WithName("RollbackProject")
            .WithSummary("Roll the project back to an earlier checkpoint of the release version in development (Owner, FR-44, FR-45, FR-63, §4.8, §4.10). In one transaction: restores the phase recorded at that checkpoint (status rolled_back), invalidates that version's artifacts created after it, resets their plans' steps, cancels pending gates and queued jobs, clears the review state and records the rollback. The Git branch is not rewritten. A released version is closed (ROLLBACK_BLOCKED version_released). Checks run in order: ROLLBACK_BLOCKED (version_released, release_partially_applied while awaiting merge), INVALID_TARGET_VERSION, ROLLBACK_BLOCKED (running work), RATE_LIMITED (one rollback per project per minute).")
            .Produces<RollbackDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithResponseDescriptions(
                (StatusCodes.Status200OK, "The rollback was applied and recorded."),
                (StatusCodes.Status400BadRequest, "VALIDATION (targetCheckpoint < 1, reason empty or over 500 characters) or INVALID_TARGET_VERSION (not below the current checkpoint, not recorded in the version in development, or a release-phase checkpoint)."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: only Owners roll back."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
                (StatusCodes.Status409Conflict, "ROLLBACK_BLOCKED with details [\"reason: <version_released|release_partially_applied|step_in_progress|agent_run_in_progress|release_running>\"] (the project is released with nothing in development, the release is awaiting merge or partly applied, or a step, agent run or release job is running), or CONFLICT (a concurrent change)."),
                (StatusCodes.Status429TooManyRequests, "RATE_LIMITED: a rollback within the last minute; see Retry-After."))
            .WithRetryAfterOn429();

        group.MapGet("/rollbacks", ListRollbacks)
            .WithName("ListRollbacks")
            .WithSummary("Every rollback of the project, newest first (members, FR-44), including rollbacks chosen on escalation gates.")
            .Produces<IReadOnlyList<RollbackDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project."));

        return app;
    }

    private static async Task<Results<Ok<RollbackDto>, ValidationProblem, ProblemHttpResult>> RollbackProject(
        Guid id,
        RollbackRequest request,
        HttpContext httpContext,
        IProjectAccess access,
        ICurrentUser currentUser,
        RollbackService rollbacks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var grant = await access.CheckAsync(id, ProjectAction.Rollback, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["reason"] = ["A reason is required."] });
        }

        var rolledBack = await rollbacks.RollbackAsync(id, request.TargetCheckpoint, request.Reason.Trim(), currentUser.Actor, cancellationToken);
        if (rolledBack.IsFailure)
        {
            ResponseDocs.SetRetryAfter(httpContext, rolledBack.Error!.RetryAfter);
            return ApiProblems.FromError(rolledBack.Error!);
        }

        return TypedResults.Ok(rolledBack.Value);
    }

    private static async Task<Results<Ok<IReadOnlyList<RollbackDto>>, ProblemHttpResult>> ListRollbacks(
        Guid id,
        IProjectAccess access,
        RollbackService rollbacks,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        return TypedResults.Ok(await rollbacks.ListAsync(id, cancellationToken));
    }
}
