using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Releases;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Versions;

/// <summary>
/// §4.10, §6 Notes: which release version a read or write is about. The project's current release version
/// (<see cref="Project.CurrentReleaseVersionId"/>) is the version in development, or the latest released one when nothing is
/// in development. History endpoints take an optional <c>releaseVersionId</c> and default to it.
/// </summary>
public static class ReleaseVersionScope
{
    /// <summary>
    /// The release version a history read covers: <paramref name="requested"/> when it belongs to the project
    /// (<c>NOT_FOUND</c> otherwise), else the project's current release version (null only for an unknown project).
    /// </summary>
    public static async Task<Result<Guid?>> ResolveAsync(AideDbContext db, Guid projectId, Guid? requested, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (requested is not { } id)
        {
            return Result.Success(await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken));
        }

        return await db.ReleaseVersions.AsNoTracking().AnyAsync(v => v.Id == id && v.ProjectId == projectId, cancellationToken)
            ? Result.Success<Guid?>(id)
            : Result.Failure<Guid?>(new DomainError(ErrorCodes.NotFound, "The release version was not found in this project."));
    }

    /// <summary>
    /// Writes the denormalised <see cref="Project.ReleasedVersion"/> and <see cref="Project.DevelopmentVersion"/> from the
    /// project's release versions (§5: in the same unit of work as the release version change).
    /// </summary>
    public static void Refresh(Project project, IEnumerable<ReleaseVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(versions);
        var rows = versions.Where(v => v.ProjectId == project.Id).Select(v => new ReleaseVersionRow(v.Version, v.Status)).ToList();
        project.ReleasedVersion = ReleaseVersionRules.ReleasedVersion(rows)?.ToString();
        project.DevelopmentVersion = ReleaseVersionRules.DevelopmentVersion(rows)?.Version;
    }
}
