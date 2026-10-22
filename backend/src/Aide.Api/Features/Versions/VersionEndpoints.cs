using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Versions;

/// <summary>
/// §4.10, FR-60–FR-63, D-V2-11: release versions (<c>/api/projects/:id/versions</c>) and lifecycle checkpoints
/// (<c>/api/projects/:id/checkpoints</c>, v1's <c>/versions</c> list).
/// </summary>
public static class VersionEndpoints
{
    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}")
            .WithTags("Versions")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/versions", ListReleaseVersions)
            .WithName("ListReleaseVersions")
            .WithSummary("The project's release versions, highest first (members, FR-62): in development, awaiting merge, released and imported baselines, with tag, base, branch, PR, checkpoints, preset and dates.")
            .Produces<IReadOnlyList<ReleaseVersionDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project."));

        group.MapPost("/versions", StartReleaseVersion)
            .WithName("StartReleaseVersion")
            .WithSummary("Start a new release version (Owner, FR-60, FR-61): needs a released project whose release is merged and nothing in development. The project goes to refinement / active at checkpoint 1 with the request as its idea; VERSION_STARTED is recorded and the Repository Analyst is queued.")
            .Produces<StartReleaseVersionResultDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status201Created, "The release version (in_development) and the project state."),
                (StatusCodes.Status400BadRequest, "VALIDATION (version or request missing or too long), VERSION_INVALID (not MAJOR.MINOR.PATCH[-prerelease]) or PRESET_NOT_FOUND."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: only Owners start versions."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
                (StatusCodes.Status409Conflict, "VERSION_IN_PROGRESS, RELEASE_NOT_MERGED (the latest release is awaiting merge), NOT_RELEASED, VERSION_NOT_GREATER (not above the released version), TAG_EXISTS (the tag is already in the repository) or CONFLICT."),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR: the repository's tags could not be checked."));

        group.MapGet("/checkpoints", ListCheckpoints)
            .WithName("ListCheckpoints")
            .WithSummary("The lifecycle checkpoints of a release version (default: the version in development, else the latest released), newest first (members, FR-44, FR-63): the phase recorded at each, when it was entered, and whether it is a rollback target now. Replaces v1's GET /versions (D-V2-11).")
            .Produces<IReadOnlyList<CheckpointDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project, or a releaseVersionId of another project."));

        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<ReleaseVersionDto>>, ProblemHttpResult>> ListReleaseVersions(
        Guid id,
        IProjectAccess access,
        VersionService versions,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        return TypedResults.Ok(await versions.ListAsync(id, cancellationToken));
    }

    private static async Task<Results<Created<StartReleaseVersionResultDto>, ValidationProblem, ProblemHttpResult>> StartReleaseVersion(
        Guid id,
        StartReleaseVersionRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        VersionService versions,
        AideDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var grant = await access.CheckAsync(id, ProjectAction.StartVersion, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        if (string.IsNullOrWhiteSpace(request.Request))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["request"] = ["Describe what the new version should add, improve or fix."] });
        }

        var started = await versions.StartAsync(id, request, currentUser.RequiredUserId, currentUser.Actor, cancellationToken);
        if (started.IsFailure)
        {
            return ApiProblems.FromError(started.Error!);
        }

        var dto = await versions.GetAsync(id, started.Value.Id, cancellationToken);
        var state = await ProjectEndpoints.LoadStateAsync(db, id, time, cancellationToken);
        return dto is null || state is null
            ? ApiProblems.NotFound("The project was not found.")
            : TypedResults.Created($"/api/projects/{id}/versions", new StartReleaseVersionResultDto(dto, state));
    }

    private static async Task<Results<Ok<IReadOnlyList<CheckpointDto>>, ProblemHttpResult>> ListCheckpoints(
        Guid id,
        IProjectAccess access,
        VersionService versions,
        CancellationToken cancellationToken,
        Guid? releaseVersionId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var checkpoints = await versions.CheckpointListAsync(id, releaseVersionId, cancellationToken);
        return checkpoints.IsSuccess ? TypedResults.Ok(checkpoints.Value) : ApiProblems.FromError(checkpoints.Error!);
    }
}
