using Aide.Api.Features.Lifecycle;
using Aide.Api.Jobs;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using ReleaseEntity = Aide.Domain.Entities.Release;

namespace Aide.Api.Features.Release;

/// <summary>Writes release attempts and their events into the current unit of work (nothing is saved here).</summary>
public static class ReleaseRecords
{
    /// <summary>The trigger of the release job queued by entering the release phase (FR-42).</summary>
    public const string PhaseEntered = LifecycleJobPayload.PhaseEntered;

    /// <summary>The trigger of the release job queued by <c>POST /release</c> (FR-43).</summary>
    public const string Retried = "retried";

    /// <summary>
    /// FR-42, FR-43: adds the next <c>in_progress</c> release attempt for the project's current version (tag
    /// <c>v&lt;version&gt;-aide</c> until the provider picks the actual tag, attempt = attempts so far + 1, the project PR URL for GitHub) and <c>RELEASE_STARTED</c>.
    /// </summary>
    public static async Task<ReleaseEntity> AddAttemptAsync(
        AideDbContext db, Project project, string trigger, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(project);
        var version = project.ProjectVersion;
        var attempts = await db.Releases.AsNoTracking()
            .Where(r => r.ProjectId == project.Id && r.ProjectVersion == version)
            .Select(r => r.Attempt)
            .ToListAsync(cancellationToken);
        attempts.AddRange(db.ChangeTracker.Entries<ReleaseEntity>()
            .Where(e => e.State == EntityState.Added && e.Entity.ProjectId == project.Id && e.Entity.ProjectVersion == version)
            .Select(e => e.Entity.Attempt));
        var git = await db.ProjectGitSettings.AsNoTracking()
            .Where(s => s.ProjectId == project.Id)
            .Select(s => new { s.Provider, s.PullRequestUrl })
            .FirstOrDefaultAsync(cancellationToken);

        var release = new ReleaseEntity
        {
            ProjectId = project.Id,
            ProjectVersion = version,
            Tag = ReleaseRules.Tag(version),
            Status = ReleaseStatus.InProgress,
            Attempt = ReleaseRules.NextAttempt(attempts),
            PrUrl = git?.Provider == GitProvider.Github ? git.PullRequestUrl : null,
            CreatedAt = now,
        };
        db.Releases.Add(release);
        db.AddEvent(
            EventTypes.ReleaseStarted,
            project.Id,
            new { releaseId = release.Id, projectVersion = version, tag = release.Tag, attempt = release.Attempt, trigger },
            actor,
            now);
        return release;
    }

    /// <summary>FR-43: marks an in-progress attempt failed and adds <c>RELEASE_FAILED</c> (the Owner notification seam, FR-47).</summary>
    public static void Fail(
        AideDbContext db, ReleaseEntity release, string errorCode, string message, IReadOnlyList<string> conflictingFiles, string actor, DateTimeOffset now, bool deadLettered = false)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(release);
        release.Status = ReleaseStatus.Failed;
        release.ErrorCode = errorCode;
        release.ErrorMessage = message;
        release.ConflictingFiles = conflictingFiles;
        release.CompletedAt = now;
        db.AddEvent(
            EventTypes.ReleaseFailed,
            release.ProjectId,
            new
            {
                releaseId = release.Id,
                projectVersion = release.ProjectVersion,
                tag = release.Tag,
                attempt = release.Attempt,
                errorCode,
                errorMessage = message,
                conflictingFiles,
                deadLettered,
            },
            actor,
            now);
    }

    /// <summary>The latest attempt at <paramref name="projectVersion"/> (tracked when <paramref name="track"/>), or <c>null</c>.</summary>
    public static Task<ReleaseEntity?> LatestAtAsync(AideDbContext db, Guid projectId, int projectVersion, bool track, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var releases = track ? db.Releases : db.Releases.AsNoTracking();
        return releases
            .Where(r => r.ProjectId == projectId && r.ProjectVersion == projectVersion)
            .OrderByDescending(r => r.Attempt)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>A release job of the project is queued or running.</summary>
    public static Task<bool> JobActiveAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Jobs.AnyAsync(
            j => j.ProjectId == projectId && j.Type == JobTypes.Release && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
            cancellationToken);
    }

    public static ReleaseDto ToDto(ReleaseEntity release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return new ReleaseDto(
            release.Id,
            release.ProjectVersion,
            release.Status,
            release.Tag,
            release.MergeCommitSha,
            release.PrUrl,
            release.ConflictingFiles,
            release.ErrorCode,
            release.ErrorMessage,
            release.Attempt,
            release.CreatedAt,
            release.CompletedAt);
    }
}

/// <summary>FR-42, FR-43: release history and the Owner's retry of a failed release.</summary>
public sealed partial class ReleaseService(AideDbContext db, JobEnqueuer jobs, TimeProvider time, ILogger<ReleaseService> logger)
{
    /// <summary>
    /// FR-43 <c>POST /release</c>: <see cref="ReleaseRules.CanRetry"/> (<c>NOT_RELEASING</c>, <c>RELEASE_IN_PROGRESS</c>), then the
    /// next in-progress attempt, <c>RELEASE_STARTED</c> and the release job in one transaction guarded by the project's
    /// <c>xmin</c> (two concurrent retries: one succeeds, one gets <c>CONFLICT</c>).
    /// </summary>
    public async Task<Result<ReleaseEntity>> RetryAsync(Guid projectId, string actor, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<ReleaseEntity>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        var latest = await ReleaseRecords.LatestAtAsync(db, project.Id, project.ProjectVersion, track: false, cancellationToken);
        var verdict = ReleaseRules.CanRetry(
            new LifecycleState(project.CurrentPhase, project.Status, project.ProjectVersion),
            latest?.Status,
            await ReleaseRecords.JobActiveAsync(db, project.Id, cancellationToken));
        if (verdict.IsFailure)
        {
            return Result.Failure<ReleaseEntity>(verdict.Error!);
        }

        var now = time.GetUtcNow();
        project.UpdatedAt = now;
        var release = await ReleaseRecords.AddAttemptAsync(db, project, ReleaseRecords.Retried, actor, now, cancellationToken);
        await jobs.EnqueueAsync(
            JobTypes.Release,
            new LifecycleJobPayload(project.Id, project.ProjectVersion, ReleaseRecords.Retried),
            project.Id,
            cancellationToken,
            now);
        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<ReleaseEntity>(saved.Error!);
        }

        LogRetried(logger, project.Id, release.ProjectVersion, release.Attempt);
        return Result.Success(release);
    }

    /// <summary>Every release attempt of the project, newest first.</summary>
    public async Task<IReadOnlyList<ReleaseDto>> ListAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var releases = await db.Releases.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(cancellationToken);
        return [.. releases.Select(ReleaseRecords.ToDto)];
    }

    /// <summary>The latest release attempt, or <c>null</c> before the first release.</summary>
    public async Task<ReleaseDto?> LatestAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var release = await db.Releases.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return release is null ? null : ReleaseRecords.ToDto(release);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: release of version {ProjectVersion} retried (attempt {Attempt})")]
    private static partial void LogRetried(ILogger logger, Guid projectId, int projectVersion, int attempt);
}
