using Aide.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aide.Infrastructure.Persistence;

/// <summary>Release-version lookups shared by the persistence layer and the API (§4.10).</summary>
public static class ReleaseVersionQueries
{
    /// <summary>Version given to a project created without one (FR-66 default).</summary>
    public const string InitialVersion = "0.1.0";

    /// <summary>
    /// The project's current release version id: <see cref="Project.CurrentReleaseVersionId"/>, or else the project's most
    /// recently created release version (a released project with nothing in development); null for an unknown project.
    /// </summary>
    public static Task<Guid?> CurrentReleaseVersionIdAsync(this AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return CurrentIdQuery(db, projectId).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The project's current release version row (see <see cref="CurrentReleaseVersionIdAsync"/>), tracked.</summary>
    public static async Task<ReleaseVersion?> CurrentReleaseVersionAsync(this AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var id = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
        return id is { } versionId ? await db.ReleaseVersions.FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken) : null;
    }

    internal static Guid? CurrentReleaseVersionId(this AideDbContext db, Guid projectId) => CurrentIdQuery(db, projectId).FirstOrDefault();

    private static IQueryable<Guid?> CurrentIdQuery(AideDbContext db, Guid projectId) =>
        db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.CurrentReleaseVersionId ?? db.ReleaseVersions
                .Where(v => v.ProjectId == p.Id)
                .OrderByDescending(v => v.CreatedAt)
                .ThenByDescending(v => v.Id)
                .Select(v => (Guid?)v.Id)
                .FirstOrDefault());
}

/// <summary>
/// §4.10 persistence defaults, applied in the same <c>SaveChanges</c> (the same transaction):
/// <list type="bullet">
/// <item>A new project without a current release version gets its first one (<c>in_development</c>,
/// <see cref="Project.DevelopmentVersion"/> or <see cref="ReleaseVersionQueries.InitialVersion"/>), and the project points at it.</item>
/// <item>New artifacts, steps, gates, transitions, reports, rollbacks, releases and agent runs saved without a
/// <c>release_version_id</c> get the project's current release version (<see cref="ReleaseVersionQueries"/>).</item>
/// </list>
/// Code that knows the version sets it explicitly; this only fills what was left unset, so the NOT NULL columns can't be
/// forgotten. <c>projects.current_release_version_id</c> is a deferred FK (see the ReleaseVersions migration), so a project
/// and its first version can be inserted together.
/// </summary>
public sealed class ReleaseVersionStampingInterceptor : SaveChangesInterceptor
{
    private const string Column = "ReleaseVersionId";

    private static readonly Type[] Stamped =
    [
        typeof(Artifact), typeof(ExecutionStep), typeof(ApprovalGate), typeof(StateTransition),
        typeof(ComplianceReport), typeof(RollbackEvent), typeof(Release), typeof(AgentRun),
    ];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is AideDbContext db)
        {
            Stamp(db, projectId => db.CurrentReleaseVersionId(projectId));
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is AideDbContext db)
        {
            var unresolved = Stamp(db, _ => null);
            foreach (var projectId in unresolved)
            {
                var versionId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
                Stamp(db, id => id == projectId ? versionId : null);
            }
        }

        return result;
    }

    /// <summary>Adds first versions and stamps what can be resolved; returns the project ids still needing a database lookup.</summary>
    private static HashSet<Guid> Stamp(AideDbContext db, Func<Guid, Guid?> lookup)
    {
        foreach (var project in db.ChangeTracker.Entries<Project>()
                     .Where(e => e.State == EntityState.Added && e.Entity.CurrentReleaseVersionId is null)
                     .Select(e => e.Entity)
                     .ToList())
        {
            var version = new ReleaseVersion
            {
                ProjectId = project.Id,
                Version = project.DevelopmentVersion ?? ReleaseVersionQueries.InitialVersion,
                Status = ReleaseVersionStatus.InDevelopment,
                TagPrefix = project.TagPrefix,
                Request = project.Idea,
                CreatedAt = project.CreatedAt,
            };
            db.ReleaseVersions.Add(version);
            project.CurrentReleaseVersionId = version.Id;
            project.DevelopmentVersion = version.Version;
        }

        var unresolved = new HashSet<Guid>();
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added && Stamped.Contains(e.Metadata.ClrType)).ToList())
        {
            var property = entry.Property(Column);
            if (property.CurrentValue is Guid id && id != Guid.Empty)
            {
                continue;
            }

            var projectId = (Guid)entry.Property("ProjectId").CurrentValue!;
            var versionId = Tracked(db, projectId) ?? lookup(projectId);
            if (versionId is { } resolved)
            {
                property.CurrentValue = resolved;
            }
            else
            {
                unresolved.Add(projectId);
            }
        }

        return unresolved;
    }

    private static Guid? Tracked(AideDbContext db, Guid projectId) =>
        db.ChangeTracker.Entries<Project>().FirstOrDefault(e => e.Entity.Id == projectId) is { Entity.CurrentReleaseVersionId: { } current }
            ? current
            : null;
}
