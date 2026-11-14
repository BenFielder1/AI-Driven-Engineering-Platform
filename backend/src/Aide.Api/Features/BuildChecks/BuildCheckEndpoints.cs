using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Access;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.BuildChecks;

/// <summary>FR-259, §6 part 2: a release version's build checks (read only).</summary>
public static class BuildCheckEndpoints
{
    public static IEndpointRouteBuilder MapBuildCheckEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/versions/{versionId:guid}")
            .WithTags("Execution")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/build-checks", ListBuildChecks)
            .WithName("ListBuildChecks")
            .WithSummary("The build checks of a release version, newest first (members, FR-259): per review cycle and attempt, the head commit, status, the preview and CI sources with their results, the failures with their log excerpts, and the build-fix step numbers planned from it.")
            .Produces<IReadOnlyList<BuildCheckDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project, or the release version in this project."));
        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<BuildCheckDto>>, ProblemHttpResult, StatusCodeHttpResult>> ListBuildChecks(
        Guid projectId,
        Guid versionId,
        IProjectAccess access,
        AideDbContext db,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(projectId, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        if (!await db.ReleaseVersions.AsNoTracking().AnyAsync(v => v.Id == versionId && v.ProjectId == projectId, cancellationToken))
        {
            return ApiProblems.NotFound("The release version was not found in this project.");
        }

        return TypedResults.Ok(await BuildCheckReader.ListAsync(db, projectId, versionId, cancellationToken));
    }
}
