using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Access;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Rollback;

/// <summary>FR-44, FR-45, §4.8: rollback (§6 <c>/api/projects/:id/rollback</c>), its history and the version picker.</summary>
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
            .WithSummary("Roll the project back to an earlier version (Owner, FR-44, FR-45, §4.8). In one transaction: restores the phase recorded at that version (status rolled_back), invalidates artifacts created after it, resets their plans' steps, cancels pending gates and queued jobs, clears the review state and records the rollback. The Git branch is not rewritten. Checks run in order: INVALID_TARGET_VERSION, ROLLBACK_BLOCKED, RATE_LIMITED (one rollback per project per minute).")
            .Produces<RollbackDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithResponseDescriptions(
                (StatusCodes.Status200OK, "The rollback was applied and recorded."),
                (StatusCodes.Status400BadRequest, "VALIDATION (targetVersion < 1, reason empty or over 500 characters) or INVALID_TARGET_VERSION (not below the current version, not recorded, or a release-phase version)."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: only Owners roll back."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
                (StatusCodes.Status409Conflict, "ROLLBACK_BLOCKED with details [\"reason: <step_in_progress|agent_run_in_progress|release_running|release_partially_applied>\"] (a step, agent run or release job is running, or a queued release retry already merged), or CONFLICT (a concurrent change)."),
                (StatusCodes.Status429TooManyRequests, "RATE_LIMITED: a rollback within the last minute; see Retry-After."))
            .WithRetryAfterOn429();

        group.MapGet("/rollbacks", ListRollbacks)
            .WithName("ListRollbacks")
            .WithSummary("Every rollback of the project, newest first (members, FR-44), including rollbacks chosen on escalation gates.")
            .Produces<IReadOnlyList<RollbackDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project."));

        group.MapGet("/versions", ListProjectVersions)
            .WithName("ListProjectVersions")
            .WithSummary("The project's versions from its state transitions, newest first (members, FR-44): the phase recorded at each version (the latest transition at that version; version 1 without one is refinement), when it was entered, and whether it is a valid rollback target now.")
            .Produces<IReadOnlyList<ProjectVersionDto>>()
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

        var rolledBack = await rollbacks.RollbackAsync(id, request.TargetVersion, request.Reason.Trim(), currentUser.Actor, cancellationToken);
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

    private static async Task<Results<Ok<IReadOnlyList<ProjectVersionDto>>, ProblemHttpResult>> ListProjectVersions(
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

        return await rollbacks.VersionsAsync(id, cancellationToken) is { } versions
            ? TypedResults.Ok(versions)
            : ApiProblems.NotFound("The project was not found.");
    }
}
