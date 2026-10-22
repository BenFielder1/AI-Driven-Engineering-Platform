using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Review;

/// <summary>
/// §4.8, §4.10: the compliance reports of the project's current review timeline — of the current release version, at or
/// below the current checkpoint and, after a rollback, created after it. A rollback clears the review state, so reports from
/// before it never count again, even once the project climbs back to (or past) their checkpoint; reports of other release
/// versions never count. Every read of "the latest report", "the previous cycle" or the trend goes through here.
/// </summary>
public static class ReviewTimeline
{
    /// <summary>The project's current-timeline reports (untracked).</summary>
    public static IQueryable<ComplianceReport> Current(
        AideDbContext db, Guid projectId, Guid? releaseVersionId, int checkpoint, DateTimeOffset? lastRollbackAt)
    {
        ArgumentNullException.ThrowIfNull(db);
        var reports = db.ComplianceReports.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Checkpoint <= checkpoint);
        if (releaseVersionId is { } versionId)
        {
            reports = reports.Where(r => r.ReleaseVersionId == versionId);
        }

        return lastRollbackAt is { } rolledBackAt ? reports.Where(r => r.CreatedAt > rolledBackAt) : reports;
    }

    /// <inheritdoc cref="Current(AideDbContext, Guid, Guid?, int, DateTimeOffset?)"/>
    public static IQueryable<ComplianceReport> Current(AideDbContext db, Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Current(db, project.Id, project.CurrentReleaseVersionId, project.Checkpoint, project.LastRollbackAt);
    }

    /// <summary>Every report of a past release version (read-only history, FR-62); untracked.</summary>
    public static IQueryable<ComplianceReport> OfVersion(AideDbContext db, Guid projectId, Guid releaseVersionId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.ComplianceReports.AsNoTracking().Where(r => r.ProjectId == projectId && r.ReleaseVersionId == releaseVersionId);
    }

    /// <summary>Newest first (creation time, then id).</summary>
    public static IOrderedQueryable<ComplianceReport> NewestFirst(this IQueryable<ComplianceReport> reports) =>
        reports.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id);
}
