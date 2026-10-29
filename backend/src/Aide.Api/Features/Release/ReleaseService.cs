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
    /// FR-42 amended by FR-64, FR-43: adds the next <c>in_progress</c> release attempt for the project's release version in
    /// development at its current checkpoint (tag <c>&lt;tagPrefix&gt;&lt;version&gt;</c>, attempt = attempts at that checkpoint so
    /// far + 1, the version's PR URL for GitHub) and <c>RELEASE_STARTED</c>.
    /// </summary>
    public static async Task<ReleaseEntity> AddAttemptAsync(
        AideDbContext db, Project project, string trigger, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(project);
        var checkpoint = project.Checkpoint;
        var versionId = project.CurrentReleaseVersionId ?? await db.CurrentReleaseVersionIdAsync(project.Id, cancellationToken) ?? Guid.Empty;
        var attempts = await db.Releases.AsNoTracking()
            .Where(r => r.ReleaseVersionId == versionId && r.Checkpoint == checkpoint)
            .Select(r => r.Attempt)
            .ToListAsync(cancellationToken);
        attempts.AddRange(db.ChangeTracker.Entries<ReleaseEntity>()
            .Where(e => e.State == EntityState.Added && e.Entity.ProjectId == project.Id && e.Entity.Checkpoint == checkpoint)
            .Select(e => e.Entity.Attempt));
        var provider = await db.ProjectGitSettings.AsNoTracking()
            .Where(s => s.ProjectId == project.Id)
            .Select(s => (GitProvider?)s.Provider)
            .FirstOrDefaultAsync(cancellationToken);
        var version = await db.ReleaseVersions.AsNoTracking()
            .Where(v => v.Id == versionId)
            .Select(v => new { v.Version, v.TagPrefix, v.PullRequestUrl })
            .FirstOrDefaultAsync(cancellationToken);

        var release = new ReleaseEntity
        {
            ProjectId = project.Id,
            ReleaseVersionId = versionId,
            Checkpoint = checkpoint,
            Tag = TagFor(version?.TagPrefix, version?.Version),
            Status = ReleaseStatus.InProgress,
            Attempt = ReleaseRules.NextAttempt(attempts),
            PrUrl = provider is { } gitProvider && gitProvider != GitProvider.Local ? version?.PullRequestUrl : null,
            CreatedAt = now,
        };
        db.Releases.Add(release);
        db.AddEvent(
            EventTypes.ReleaseStarted,
            project.Id,
            new { releaseId = release.Id, releaseVersionId = versionId, version = version?.Version, checkpoint, tag = release.Tag, attempt = release.Attempt, trigger },
            actor,
            now);
        return release;
    }

    /// <summary>§4.10: <c>&lt;tagPrefix&gt;&lt;version&gt;</c>; a version text that isn't semver (or a bad prefix) keeps the text as the tag.</summary>
    public static string TagFor(string? tagPrefix, string? version)
    {
        var prefix = tagPrefix ?? ReleaseVersion.DefaultTagPrefix;
        return SemVer.TryParse(version, out var parsed) && ReleaseVersionRules.IsValidTagPrefix(prefix)
            ? ReleaseVersionRules.Tag(prefix, parsed)
            : prefix + (version ?? string.Empty);
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
                releaseVersionId = release.ReleaseVersionId,
                checkpoint = release.Checkpoint,
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

    /// <summary>The latest attempt of the project's current release version at <paramref name="checkpoint"/> (tracked when <paramref name="track"/>), or <c>null</c>.</summary>
    public static async Task<ReleaseEntity?> LatestAtAsync(AideDbContext db, Guid projectId, int checkpoint, bool track, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var versionId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
        var releases = track ? db.Releases : db.Releases.AsNoTracking();
        return await releases
            .Where(r => r.ProjectId == projectId && r.ReleaseVersionId == versionId && r.Checkpoint == checkpoint)
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

    /// <param name="release">The release attempt.</param>
    /// <param name="mergeStrategy">The project's merge strategy (FR-65).</param>
    /// <param name="productionDeployment">FR-91: the release's production deployment, if any.</param>
    public static ReleaseDto ToDto(ReleaseEntity release, MergeStrategy mergeStrategy, Deployments.DeploymentDto? productionDeployment = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        return new ReleaseDto(
            release.Id,
            release.ReleaseVersionId,
            release.Checkpoint,
            release.Status,
            release.Tag,
            release.MergeCommitSha,
            release.PrUrl,
            release.ConflictingFiles,
            release.ErrorCode,
            release.ErrorMessage,
            release.Attempt,
            release.CreatedAt,
            release.CompletedAt,
            release.GithubReleaseUrl,
            release.AwaitingMergeSince,
            Git.MergeReasonsJson.Read(release.MergeReasons),
            mergeStrategy,
            productionDeployment);
    }

    /// <summary>The latest attempt for the project state (FR-42, FR-43, FR-65).</summary>
    public static ReleaseSummaryDto ToSummary(ReleaseEntity release, MergeStrategy mergeStrategy)
    {
        ArgumentNullException.ThrowIfNull(release);
        return new ReleaseSummaryDto(
            release.Id,
            release.ReleaseVersionId,
            release.Checkpoint,
            release.Status,
            release.Tag,
            release.CreatedAt,
            release.PrUrl,
            release.GithubReleaseUrl,
            release.AwaitingMergeSince,
            Git.MergeReasonsJson.Read(release.MergeReasons),
            mergeStrategy);
    }

    /// <summary>The project's merge strategy (FR-65; <c>aide_merges</c> when the project is missing).</summary>
    public static async Task<MergeStrategy> MergeStrategyAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => (MergeStrategy?)p.MergeStrategy)
            .FirstOrDefaultAsync(cancellationToken) ?? MergeStrategy.AideMerges;
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

        var latest = await ReleaseRecords.LatestAtAsync(db, project.Id, project.Checkpoint, track: false, cancellationToken);
        var verdict = ReleaseRules.CanRetry(
            new LifecycleState(project.CurrentPhase, project.Status, project.Checkpoint),
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
            new LifecycleJobPayload(project.Id, project.Checkpoint, ReleaseRecords.Retried),
            project.Id,
            cancellationToken,
            now);
        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<ReleaseEntity>(saved.Error!);
        }

        LogRetried(logger, project.Id, release.Checkpoint, release.Attempt);
        return Result.Success(release);
    }

    /// <summary>The release attempts of the project, newest first; only those of <paramref name="releaseVersionId"/> when given.</summary>
    public async Task<IReadOnlyList<ReleaseDto>> ListAsync(Guid projectId, CancellationToken cancellationToken, Guid? releaseVersionId = null)
    {
        var releases = await db.Releases.AsNoTracking()
            .Where(r => r.ProjectId == projectId && (releaseVersionId == null || r.ReleaseVersionId == releaseVersionId))
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(cancellationToken);
        var strategy = await ReleaseRecords.MergeStrategyAsync(db, projectId, cancellationToken);
        var production = await Deployments.DeploymentReader.ProductionByReleaseAsync(db, [.. releases.Select(r => r.Id)], cancellationToken);
        return [.. releases.Select(r => ReleaseRecords.ToDto(r, strategy, production.GetValueOrDefault(r.Id)))];
    }

    /// <summary>The latest release attempt, or <c>null</c> before the first release.</summary>
    public async Task<ReleaseDto?> LatestAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var release = await db.Releases.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (release is null)
        {
            return null;
        }

        var production = await Deployments.DeploymentReader.ProductionByReleaseAsync(db, [release.Id], cancellationToken);
        return ReleaseRecords.ToDto(release, await ReleaseRecords.MergeStrategyAsync(db, projectId, cancellationToken), production.GetValueOrDefault(release.Id));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: release at checkpoint {Checkpoint} retried (attempt {Attempt})")]
    private static partial void LogRetried(ILogger logger, Guid projectId, int checkpoint, int attempt);
}
