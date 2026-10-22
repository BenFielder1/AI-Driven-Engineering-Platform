using Aide.Api.Features.Git;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Release;

/// <summary>Payload of the <c>merge_poll</c> job (§4.14): the project, the awaiting attempt and the PR's last ETag.</summary>
public sealed record MergePollJobPayload(Guid ProjectId, Guid ReleaseId, string? ETag = null);

/// <summary>
/// FR-64, FR-65: how a release attempt ends, shared by the release job and the merge poll. Each method writes one unit of
/// work guarded by the project's <c>xmin</c> (a concurrent change retries the job) and goes through the lifecycle state
/// machine for the project's status (CLAUDE.md rule 9); the release version and the project's denormalised versions
/// change in the same transaction (§5).
/// </summary>
public sealed partial class ReleaseCompletion(
    AideDbContext db,
    LifecycleWriter writer,
    JobEnqueuer jobs,
    Deployments.DeploymentScheduler deployments,
    TimeProvider time,
    ILogger<ReleaseCompletion> logger)
{
    public const string CompletedReason = "Release completed: merged and tagged";

    public const string AwaitingReason = "Release awaiting merge on GitHub";

    /// <summary>
    /// Released: the attempt (in progress, or awaiting its merge) succeeds (tag, merge commit, GitHub Release URL,
    /// completed_at), the version becomes <c>released</c>, the project <c>released</c> with this version as its released
    /// version and nothing in development, and <c>RELEASE_COMPLETED</c> (after <c>RELEASE_MERGED_EXTERNALLY</c> when the merge
    /// happened on GitHub). If the project moved on meanwhile, the Git facts are recorded on the attempt only. FR-91: with
    /// Vercel set up, the production deployment of the merge commit is recorded and queued in the same transaction (a
    /// failed deployment never undoes the release, D-V2-10).
    /// </summary>
    public async Task SucceedAsync(Guid releaseId, ReleaseOutcome outcome, string? githubReleaseUrl, bool mergedExternally, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        db.ChangeTracker.Clear();
        var now = time.GetUtcNow();
        var release = await db.Releases.FirstAsync(r => r.Id == releaseId, cancellationToken);
        if (!ReleaseAttemptRules.CanTransition(release.Status, ReleaseStatus.Succeeded))
        {
            LogSkipped(logger, "the release attempt was already recorded");
            return;
        }

        release.Status = ReleaseStatus.Succeeded;
        release.Tag = outcome.Tag ?? release.Tag;
        release.MergeCommitSha = outcome.MergeCommitSha;
        release.GithubReleaseUrl = githubReleaseUrl ?? release.GithubReleaseUrl;
        release.CompletedAt = now;
        var project = await db.Projects.FirstAsync(p => p.Id == release.ProjectId, cancellationToken);
        if (IsReleasing(project, release.Checkpoint))
        {
            var completed = LifecycleStateMachine.Apply(State(project), TransitionRequest.ReleaseCompleted(CompletedReason), LifecycleSnapshot.Empty);
            if (completed.IsFailure)
            {
                throw new JobFailedException(completed.Error!.Message, transient: true, completed.Error.Code);
            }

            await writer.ApplyAsync(project, completed.Value, SystemActor.Name, now, cancellationToken);
            var versions = await db.ReleaseVersions.Where(v => v.ProjectId == project.Id).ToListAsync(cancellationToken);
            if (versions.FirstOrDefault(v => v.Id == release.ReleaseVersionId) is { } version
                && ReleaseVersionRules.CanTransition(version.Status, ReleaseVersionStatus.Released))
            {
                version.Status = ReleaseVersionStatus.Released;
                version.Tag = release.Tag;
                version.MergeCommitSha = release.MergeCommitSha;
                version.ReleasedAt = now;
            }

            Versions.ReleaseVersionScope.Refresh(project, versions);
        }
        else
        {
            LogProjectMoved(logger, project.Id, release.Checkpoint);
        }

        if (mergedExternally)
        {
            db.AddEvent(
                EventTypes.ReleaseMergedExternally,
                release.ProjectId,
                new
                {
                    releaseId = release.Id,
                    releaseVersionId = release.ReleaseVersionId,
                    version = await VersionTextAsync(release.ReleaseVersionId, cancellationToken),
                    checkpoint = release.Checkpoint,
                    tag = release.Tag,
                    mergeCommitSha = release.MergeCommitSha,
                    githubReleaseUrl = release.GithubReleaseUrl,
                    pullRequestUrl = release.PrUrl,
                },
                SystemActor.Name,
                now);
        }

        db.AddEvent(
            EventTypes.ReleaseCompleted,
            release.ProjectId,
            new
            {
                releaseId = release.Id,
                releaseVersionId = release.ReleaseVersionId,
                checkpoint = release.Checkpoint,
                tag = release.Tag,
                attempt = release.Attempt,
                mergeCommitSha = release.MergeCommitSha,
                pullRequestUrl = release.PrUrl,
                githubReleaseUrl = release.GithubReleaseUrl,
                alreadyTagged = outcome.AlreadyTagged,
                mergedExternally,
            },
            SystemActor.Name,
            now);
        var defaultBranch = await db.ProjectGitSettings.AsNoTracking()
            .Where(s => s.ProjectId == release.ProjectId)
            .Select(s => s.DefaultBranch)
            .FirstOrDefaultAsync(cancellationToken) ?? "main";
        await deployments.AutoProductionAsync(release, defaultBranch, now, cancellationToken);
        await SaveAsync(cancellationToken);
        LogReleased(logger, release.ProjectId, release.Tag, outcome.AlreadyTagged, mergedExternally);
    }

    /// <summary>
    /// FR-65: the merge was refused or the strategy is <c>manual</c>: the attempt becomes <c>awaiting_merge</c> (since now,
    /// with <paramref name="reasons"/>), the version <c>awaiting_merge</c>, the project <c>awaiting_merge</c> (state
    /// machine), <c>RELEASE_AWAITING_MERGE</c> (Owners notified with the PR link and reasons) and the merge poll is queued
    /// for its first poll — all in one transaction.
    /// </summary>
    public async Task AwaitMergeAsync(Guid releaseId, MergeStrategy strategy, IReadOnlyList<MergeReadinessReasonDto> reasons, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        db.ChangeTracker.Clear();
        var now = time.GetUtcNow();
        var release = await db.Releases.FirstAsync(r => r.Id == releaseId, cancellationToken);
        var project = await db.Projects.FirstAsync(p => p.Id == release.ProjectId, cancellationToken);
        if (!ReleaseAttemptRules.CanTransition(release.Status, ReleaseStatus.AwaitingMerge) || !IsReleasing(project, release.Checkpoint))
        {
            LogSkipped(logger, "the attempt or the project moved on before awaiting the merge");
            return;
        }

        var awaiting = LifecycleStateMachine.Apply(State(project), TransitionRequest.ReleaseAwaitingMerge(AwaitingReason), LifecycleSnapshot.Empty);
        if (awaiting.IsFailure)
        {
            throw new JobFailedException(awaiting.Error!.Message, transient: true, awaiting.Error.Code);
        }

        release.Status = ReleaseStatus.AwaitingMerge;
        release.AwaitingMergeSince = now;
        release.MergeReasons = AideJson.Serialize(reasons);
        await writer.ApplyAsync(project, awaiting.Value, SystemActor.Name, now, cancellationToken);
        var versions = await db.ReleaseVersions.Where(v => v.ProjectId == project.Id).ToListAsync(cancellationToken);
        if (versions.FirstOrDefault(v => v.Id == release.ReleaseVersionId) is { } version
            && ReleaseVersionRules.CanTransition(version.Status, ReleaseVersionStatus.AwaitingMerge))
        {
            version.Status = ReleaseVersionStatus.AwaitingMerge;
        }

        Versions.ReleaseVersionScope.Refresh(project, versions);
        db.AddEvent(
            EventTypes.ReleaseAwaitingMerge,
            project.Id,
            new
            {
                releaseId = release.Id,
                releaseVersionId = release.ReleaseVersionId,
                version = versions.FirstOrDefault(v => v.Id == release.ReleaseVersionId)?.Version,
                checkpoint = release.Checkpoint,
                tag = release.Tag,
                attempt = release.Attempt,
                pullRequestUrl = release.PrUrl,
                mergeStrategy = strategy,
                awaitingMerge = true,
                reasons,
            },
            SystemActor.Name,
            now);
        jobs.Enqueue(JobTypes.MergePoll, new MergePollJobPayload(project.Id, release.Id), project.Id, now, MergePollRules.NextPollAt(now, now));
        await SaveAsync(cancellationToken);
        LogAwaiting(logger, project.Id, release.Tag, reasons.Count);
    }

    /// <summary>
    /// FR-65, §4.14: the awaited merge won't happen (<c>PR_CLOSED</c>, <c>MERGE_TIMEOUT</c>) or the tag can't be made
    /// (<c>TAG_EXISTS</c>): the attempt fails with <paramref name="errorCode"/> (<c>RELEASE_FAILED</c>), the version is back
    /// in development and the project back to <c>releasing</c> (state machine), so an Owner can retry with
    /// <c>POST /release</c>.
    /// </summary>
    public async Task AbandonAsync(Guid releaseId, string errorCode, string message, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var now = time.GetUtcNow();
        var release = await db.Releases.FirstAsync(r => r.Id == releaseId, cancellationToken);
        if (release.Status != ReleaseStatus.AwaitingMerge)
        {
            LogSkipped(logger, "the attempt is no longer awaiting its merge");
            return;
        }

        ReleaseRecords.Fail(db, release, errorCode, message, [], SystemActor.Name, now);
        var project = await db.Projects.FirstAsync(p => p.Id == release.ProjectId, cancellationToken);
        if (project.Checkpoint == release.Checkpoint)
        {
            await BackToReleasingAsync(db, writer, project, $"Release failed: {errorCode}", now, cancellationToken);
        }

        await SaveAsync(cancellationToken);
        LogAbandoned(logger, release.ProjectId, release.Tag, errorCode);
    }

    /// <summary>
    /// FR-65: a project in <c>release/awaiting_merge</c> goes back to <c>releasing</c> through the state machine, and its
    /// <c>awaiting_merge</c> version back to <c>in_development</c> (denormalised versions refreshed). Nothing is saved here.
    /// Any other project state is left alone.
    /// </summary>
    public static async Task BackToReleasingAsync(
        AideDbContext db, LifecycleWriter writer, Project project, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(project);
        if (project.CurrentPhase != Phase.Release || project.Status != ProjectStatus.AwaitingMerge)
        {
            return;
        }

        var abandoned = LifecycleStateMachine.Apply(State(project), TransitionRequest.ReleaseMergeAbandoned(reason), LifecycleSnapshot.Empty);
        if (abandoned.IsFailure)
        {
            throw new JobFailedException(abandoned.Error!.Message, transient: true, abandoned.Error.Code);
        }

        await writer.ApplyAsync(project, abandoned.Value, SystemActor.Name, now, cancellationToken);
        var versions = await db.ReleaseVersions.Where(v => v.ProjectId == project.Id).ToListAsync(cancellationToken);
        foreach (var version in versions.Where(v => v.Status == ReleaseVersionStatus.AwaitingMerge))
        {
            if (ReleaseVersionRules.CanTransition(version.Status, ReleaseVersionStatus.InDevelopment))
            {
                version.Status = ReleaseVersionStatus.InDevelopment;
            }
        }

        Versions.ReleaseVersionScope.Refresh(project, versions);
    }

    private Task<string?> VersionTextAsync(Guid releaseVersionId, CancellationToken cancellationToken) =>
        db.ReleaseVersions.AsNoTracking().Where(v => v.Id == releaseVersionId).Select(v => v.Version).FirstOrDefaultAsync(cancellationToken);

    /// <summary>The project is still in release at the attempt's checkpoint, releasing or awaiting its merge.</summary>
    public static bool IsReleasing(Project project, int checkpoint)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.CurrentPhase == Phase.Release
            && project.Status is ProjectStatus.Releasing or ProjectStatus.AwaitingMerge
            && project.Checkpoint == checkpoint;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            // A concurrent change (e.g. a rollback): retry; the next attempt checks again.
            throw new JobFailedException(saved.Error!.Message, transient: true, saved.Error.Code);
        }
    }

    private static LifecycleState State(Project project) => new(project.CurrentPhase, project.Status, project.Checkpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Release completion did nothing: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} released as {Tag} (tag already existed: {AlreadyTagged}, merged on GitHub: {MergedExternally})")]
    private static partial void LogReleased(ILogger logger, Guid projectId, string tag, bool alreadyTagged, bool mergedExternally);

    [LoggerMessage(Level = LogLevel.Information, Message = "Release {Tag} of project {ProjectId} awaits a merge on GitHub ({ReasonCount} reasons)")]
    private static partial void LogAwaiting(ILogger logger, Guid projectId, string tag, int reasonCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Awaited merge of release {Tag} of project {ProjectId} abandoned ({ErrorCode})")]
    private static partial void LogAbandoned(ILogger logger, Guid projectId, string tag, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId} left release checkpoint {Checkpoint} while it was being released; the release is recorded, the project is unchanged")]
    private static partial void LogProjectMoved(ILogger logger, Guid projectId, int checkpoint);
}
