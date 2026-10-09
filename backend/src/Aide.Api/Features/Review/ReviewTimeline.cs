using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Review;

/// <summary>
/// §4.8: the compliance reports of the project's current review timeline — at or below the current project version and,
/// after a rollback, created after it. A rollback clears the review state, so reports from before it never count again,
/// even once the project climbs back to (or past) their version. Every read of "the latest report", "the previous cycle"
/// or the trend goes through here.
/// </summary>
public static class ReviewTimeline
{
    /// <summary>The project's current-timeline reports (untracked).</summary>
    public static IQueryable<ComplianceReport> Current(AideDbContext db, Guid projectId, int projectVersion, DateTimeOffset? lastRollbackAt)
    {
        ArgumentNullException.ThrowIfNull(db);
        var reports = db.ComplianceReports.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.ProjectVersion <= projectVersion);
        return lastRollbackAt is { } rolledBackAt ? reports.Where(r => r.CreatedAt > rolledBackAt) : reports;
    }

    /// <inheritdoc cref="Current(AideDbContext, Guid, int, DateTimeOffset?)"/>
    public static IQueryable<ComplianceReport> Current(AideDbContext db, Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Current(db, project.Id, project.ProjectVersion, project.LastRollbackAt);
    }

    /// <summary>Newest first (creation time, then id).</summary>
    public static IOrderedQueryable<ComplianceReport> NewestFirst(this IQueryable<ComplianceReport> reports) =>
        reports.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id);
}
