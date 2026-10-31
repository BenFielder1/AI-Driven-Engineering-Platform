using Aide.Api.Jobs;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// Payload of the <c>preview_rollout</c> and <c>production_rollout</c> jobs (FR-147, FR-149): what to deploy, why, for
/// which version (and release), and when it was requested (the rollout's migration run is the one started after it).
/// </summary>
public sealed record RolloutJobPayload(
    Guid ProjectId,
    DeploymentEnvironment Environment,
    Guid ReleaseVersionId,
    Guid? ReleaseId,
    string GitRef,
    string GitSha,
    DeploymentTrigger Trigger,
    Guid? TriggeredBy,
    DateTimeOffset RequestedAt);

/// <summary>
/// FR-147, FR-148, FR-149: queues rollouts in the caller's unit of work, and answers what the preview database has
/// applied. One preview rollout per project: queuing one cancels older queued ones (a running one sees the newer job and
/// stops). The production rollout is queued once per release.
/// </summary>
public sealed partial class RolloutScheduler(AideDbContext db, JobEnqueuer jobs, IWorkingCopies workingCopies, ILogger<RolloutScheduler> logger)
{
    private const int MaxPaths = 100_000;

    /// <summary>
    /// FR-147: queues the preview rollout of <paramref name="sha"/>; <c>null</c> when deployment isn't set up. In a
    /// transaction it takes the project lock first (the preview reset takes it too, FR-148); a rollout queued while a reset
    /// is queued or running waits for the reset (<see cref="RolloutJobHandler"/>).
    /// </summary>
    public async Task<Job?> EnqueuePreviewAsync(
        Guid projectId, Guid releaseVersionId, string branch, string sha, DeploymentTrigger trigger, Guid? triggeredBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await Projects.ProjectLocks.LockProjectAsync(db, projectId, cancellationToken);
        }

        if (!await DeploymentScheduler.ConfiguredAsync(db, projectId, cancellationToken))
        {
            return null;
        }

        var older = await db.Jobs
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.PreviewRollout && j.Status == JobStatus.Queued)
            .ToListAsync(cancellationToken);
        foreach (var job in older)
        {
            job.Status = JobPolicy.Cancel(job.Status);
            job.CompletedAt = now;
        }

        LogQueued(logger, projectId, DeploymentEnvironment.Preview, older.Count);
        return jobs.Enqueue(
            JobTypes.PreviewRollout,
            new RolloutJobPayload(projectId, DeploymentEnvironment.Preview, releaseVersionId, null, branch, sha, trigger, triggeredBy, now),
            projectId,
            now);
    }

    /// <summary>FR-149: queues the production rollout of the release's merge commit, once per release; else <c>null</c>.</summary>
    public async Task<Job?> EnqueueProductionAsync(Domain.Entities.Release release, string defaultBranch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.MergeCommitSha is not { Length: > 0 } sha || !await DeploymentScheduler.ConfiguredAsync(db, release.ProjectId, cancellationToken))
        {
            return null;
        }

        if (await ProductionRolloutAsync(release.ProjectId, release.Id, cancellationToken) is not null)
        {
            return null;
        }

        LogQueued(logger, release.ProjectId, DeploymentEnvironment.Production, 0);
        return jobs.Enqueue(
            JobTypes.ProductionRollout,
            new RolloutJobPayload(release.ProjectId, DeploymentEnvironment.Production, release.ReleaseVersionId, release.Id, defaultBranch, sha, DeploymentTrigger.Auto, null, now),
            release.ProjectId,
            now);
    }

    /// <summary>The release's production rollout job (one being added counts), or <c>null</c>.</summary>
    public async Task<Job?> ProductionRolloutAsync(Guid projectId, Guid releaseId, CancellationToken cancellationToken)
    {
        var added = db.ChangeTracker.Entries<Job>()
            .Where(e => e.State == EntityState.Added && e.Entity.Type == JobTypes.ProductionRollout && e.Entity.ProjectId == projectId)
            .Select(e => e.Entity);
        var stored = await db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.ProductionRollout)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(cancellationToken);
        return added.Concat(stored).FirstOrDefault(j => TryPayload(j.Payload)?.ReleaseId == releaseId);
    }

    /// <summary>The project's newest preview rollout job (any state), or <c>null</c>.</summary>
    public Task<Job?> LatestPreviewAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.PreviewRollout && j.Status != JobStatus.Cancelled)
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>FR-148: whether a preview rollout is queued or running.</summary>
    public Task<bool> PreviewRunningAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking().AnyAsync(
            j => j.ProjectId == projectId && j.Type == JobTypes.PreviewRollout && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
            cancellationToken);

    /// <summary>FR-148: whether a preview database reset is queued or running.</summary>
    public Task<bool> PreviewResetPendingAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking().AnyAsync(
            j => j.ProjectId == projectId && j.Type == JobTypes.PreviewDatabaseReset && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
            cancellationToken);

    /// <summary>
    /// FR-148: the migration files applied to the preview database that the commit <paramref name="headSha"/> no longer
    /// has (empty: not diverged, or unknown).
    /// </summary>
    public async Task<IReadOnlyList<string>> DivergedAsync(Guid projectId, string headSha, CancellationToken cancellationToken)
    {
        if (await AppliedPreviewCommitAsync(projectId, cancellationToken) is not { } applied || applied == headSha)
        {
            return [];
        }

        try
        {
            var appliedPaths = await workingCopies.ListPathsAtCommitAsync(projectId, applied, MaxPaths, cancellationToken);
            var headPaths = await workingCopies.ListPathsAtCommitAsync(projectId, headSha, MaxPaths, cancellationToken);
            return PreviewDatabaseRules.Missing(appliedPaths, headPaths);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            LogUnreadable(logger, projectId);
            return [];
        }
    }

    /// <summary>
    /// FR-148: the commit whose migrations the preview database last received since its last reset: the newest successful
    /// preview migration run (Supabase BaaS), or the newest ready backend preview (its pre-deploy migrated, FR-154) when the
    /// preview has its own database. <c>null</c>: nothing applied (or no preview database).
    /// </summary>
    public async Task<string?> AppliedPreviewCommitAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var resetAt = await db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Type == EventTypes.DatabaseReset)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => (DateTimeOffset?)e.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken) ?? DateTimeOffset.MinValue;
        var run = await db.DatabaseMigrationRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Environment == DeploymentEnvironment.Preview && r.Status == MigrationRunStatus.Succeeded && r.FinishedAt > resetAt)
            .OrderByDescending(r => r.FinishedAt)
            .Select(r => new { r.GitSha, At = r.FinishedAt })
            .FirstOrDefaultAsync(cancellationToken);
        var ownDatabase = await db.ManagedDatabases.AsNoTracking()
                .AnyAsync(d => d.ProjectId == projectId && d.Environment == DeploymentEnvironment.Preview && d.Status != ManagedDatabaseStatus.Removed, cancellationToken)
            || await db.DeploymentTargets.AsNoTracking()
                .AnyAsync(t => t.ProjectId == projectId && t.Component == DeploymentComponent.Backend && (t.Provider == DeploymentProvider.Render || t.Provider == DeploymentProvider.Railway), cancellationToken);
        var deploy = !ownDatabase ? null : await db.Deployments.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.Environment == DeploymentEnvironment.Preview && d.Component == DeploymentComponent.Backend
                && d.Status == DeploymentStatus.Ready && d.ReadyAt > resetAt)
            .OrderByDescending(d => d.ReadyAt)
            .Select(d => new { d.GitSha, At = d.ReadyAt })
            .FirstOrDefaultAsync(cancellationToken);
        return (run, deploy) switch
        {
            (null, null) => null,
            ({ } r, null) => r.GitSha,
            (null, { } d) => d.GitSha,
            ({ } r, { } d) => r.At >= d.At ? r.GitSha : d.GitSha,
        };
    }

    public static RolloutJobPayload? TryPayload(string payload)
    {
        try
        {
            return AideJson.Deserialize<RolloutJobPayload>(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {Environment} rollout queued ({Cancelled} older queued rollout(s) cancelled)")]
    private static partial void LogQueued(ILogger logger, Guid projectId, DeploymentEnvironment environment, int cancelled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: the preview migration history couldn't be compared (working copy unreadable)")]
    private static partial void LogUnreadable(ILogger logger, Guid projectId);
}
