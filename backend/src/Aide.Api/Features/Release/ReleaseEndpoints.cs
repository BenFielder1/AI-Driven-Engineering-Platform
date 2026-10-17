using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Versions;
using Aide.Domain.Access;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Release;

/// <summary>FR-42, FR-43: release attempts (§6 <c>/api/projects/:id/release</c>) and the release history.</summary>
public static class ReleaseEndpoints
{
    public static IEndpointRouteBuilder MapReleaseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}")
            .WithTags("Release")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/release", RetryRelease)
            .WithName("RetryRelease")
            .WithSummary("Retry a failed release (Owner, FR-43 amended by FR-64, FR-65): queues the release job again for the version in development at its current checkpoint (merge the version's PR, or the branch for Local, under the project's merge strategy; tag <tagPrefix><version> on the merge commit, reusing a tag that already points at it; create the GitHub Release). Only in the release phase with status releasing, after the latest attempt failed, e.g. MERGE_CONFLICT resolved on the branch, or PR_CLOSED (PR reopened on GitHub) / MERGE_TIMEOUT, after which the project stays in release. Not while awaiting_merge: the merge poll completes that attempt. Returns 202 with the new in_progress release; the outcome (succeeded, awaiting_merge or failed) arrives on the live channel.")
            .Produces<ReleaseDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status202Accepted, "The release job was queued; the release is in_progress."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: only Owners release."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
                (StatusCodes.Status409Conflict, "NOT_RELEASING (not in the release phase with status releasing, e.g. already released or awaiting_merge), RELEASE_IN_PROGRESS (a release job is queued or running) or CONFLICT (a concurrent change)."));

        group.MapGet("/releases", ListReleases)
            .WithName("ListReleases")
            .WithSummary("The release attempts of a release version (default: the version in development, else the latest released), newest first (members, FR-42, FR-43, FR-64). Empty before the first release.")
            .Produces<IReadOnlyList<ReleaseDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project."));

        group.MapGet("/releases/latest", GetLatestRelease)
            .WithName("GetLatestRelease")
            .WithSummary("The latest release attempt (members, FR-42, FR-43): in progress, succeeded, or failed with its error and conflicting files.")
            .Produces<ReleaseDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project, or no release has started yet."));

        return app;
    }

    private static async Task<Results<Accepted<ReleaseDto>, ProblemHttpResult>> RetryRelease(
        Guid id,
        IProjectAccess access,
        ICurrentUser currentUser,
        ReleaseService releases,
        AideDbContext db,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.RequestRelease, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var retried = await releases.RetryAsync(id, currentUser.Actor, cancellationToken);
        return retried.IsSuccess
            ? TypedResults.Accepted($"/api/projects/{id}/releases/latest", ReleaseRecords.ToDto(retried.Value, await ReleaseRecords.MergeStrategyAsync(db, id, cancellationToken)))
            : ApiProblems.FromError(retried.Error!);
    }

    private static async Task<Results<Ok<IReadOnlyList<ReleaseDto>>, ProblemHttpResult>> ListReleases(
        Guid id,
        IProjectAccess access,
        ReleaseService releases,
        AideDbContext db,
        CancellationToken cancellationToken,
        Guid? releaseVersionId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var scope = await ReleaseVersionScope.ResolveAsync(db, id, releaseVersionId, cancellationToken);
        if (scope.IsFailure)
        {
            return ApiProblems.FromError(scope.Error!);
        }

        return TypedResults.Ok(await releases.ListAsync(id, cancellationToken, scope.Value));
    }

    private static async Task<Results<Ok<ReleaseDto>, ProblemHttpResult>> GetLatestRelease(
        Guid id,
        IProjectAccess access,
        ReleaseService releases,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        return await releases.LatestAsync(id, cancellationToken) is { } latest
            ? TypedResults.Ok(latest)
            : ApiProblems.NotFound("No release has started yet.");
    }
}
