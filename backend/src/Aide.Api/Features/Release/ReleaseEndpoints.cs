using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Access;
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
            .WithSummary("Retry a failed release (Owner, FR-43): queues the release job again for the current project version (merge the project PR or branch into the default branch, tag v<version>-aide, or the first free v<version>-aide.<k> when a version number reused after a rollback is already tagged elsewhere; an existing tag of the version holding the branch is a no-op). Only in the release phase with status releasing, after the latest attempt failed (e.g. MERGE_CONFLICT resolved on the branch). Returns 202 with the new in_progress release; the outcome arrives on the live channel.")
            .Produces<ReleaseDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status202Accepted, "The release job was queued; the release is in_progress."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: only Owners release."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
                (StatusCodes.Status409Conflict, "NOT_RELEASING (not in the release phase with status releasing, e.g. already released), RELEASE_IN_PROGRESS (a release job is queued or running) or CONFLICT (a concurrent change)."));

        group.MapGet("/releases", ListReleases)
            .WithName("ListReleases")
            .WithSummary("Every release attempt of the project, newest first (members, FR-42, FR-43). Empty before the first release.")
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
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.RequestRelease, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var retried = await releases.RetryAsync(id, currentUser.Actor, cancellationToken);
        return retried.IsSuccess
            ? TypedResults.Accepted($"/api/projects/{id}/releases/latest", ReleaseRecords.ToDto(retried.Value))
            : ApiProblems.FromError(retried.Error!);
    }

    private static async Task<Results<Ok<IReadOnlyList<ReleaseDto>>, ProblemHttpResult>> ListReleases(
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

        return TypedResults.Ok(await releases.ListAsync(id, cancellationToken));
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
