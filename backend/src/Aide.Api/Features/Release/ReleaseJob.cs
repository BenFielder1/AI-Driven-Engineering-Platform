using System.Text.Json;
using Aide.Api.Features.Execution;
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
using ReleaseEntity = Aide.Domain.Entities.Release;

namespace Aide.Api.Features.Release;

/// <summary>
/// FR-42 amended by FR-64, FR-43: the release job, queued with the transition into release (or by <c>POST /release</c>).
/// Idempotent (§4.9).
/// <list type="number">
/// <item>Skip unless the project is in release/releasing at the checkpoint the job was queued for and that checkpoint's
/// latest attempt is in progress (an attempt is created for a job queued without one).</item>
/// <item>Prepare the working copy (GitHub: fetch), re-check (touching the project row) that the project is still releasing,
/// then release through the project's Git provider: the version's tag <c>&lt;tagPrefix&gt;&lt;version&gt;</c> already holding the
/// release is a no-op, on another commit it fails the release (<c>TAG_EXISTS</c>, nothing merged); otherwise merge the PR
/// (GitHub) or the branch (Local) — the merge commit is recorded on the attempt at once — and create the annotated tag.</item>
/// <item>Released: the attempt succeeds (merge commit, completed_at), the release version becomes <c>released</c> (tag,
/// merge commit, released_at), the project becomes <c>released</c> through the state machine (checkpoint unchanged), its
/// released version is this version and nothing is in development, and <c>RELEASE_COMPLETED</c> is written, in one
/// transaction guarded by the project's xmin.</item>
/// <item>Merge conflict or another permanent failure: the attempt fails with its code (and the conflicting files), the
/// project stays release/releasing, <c>RELEASE_FAILED</c> is written; the job itself ends (nothing to retry).</item>
/// </list>
/// Transient failures retry the job; when its retries run out, <see cref="ReleaseJobEndedHook"/> fails the attempt.
/// </summary>
public sealed partial class ReleaseJobHandler(
    AideDbContext db,
    WorkingCopyPreparer preparer,
    LifecycleWriter writer,
    TimeProvider time,
    ILogger<ReleaseJobHandler> logger) : IJobHandler
{
    public const string CompletedReason = "Release completed: merged and tagged";

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = Payload(job);
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == payload.ProjectId, cancellationToken);
        if (project is null || !ReleaseRules.ShouldRun(State(project), payload.ProjectVersion))
        {
            LogSkipped(logger, "the project is gone, not releasing, or at another version");
            return;
        }

        var release = await AttemptAsync(project, cancellationToken);
        if (release is null)
        {
            LogSkipped(logger, "the latest release attempt of this version is not in progress");
            return;
        }

        var preparation = await preparer.TryPrepareAsync(project.Id, cancellationToken);
        if (preparation.Result.IsFailure)
        {
            var error = preparation.Result.Error!;
            if (preparation.Transient)
            {
                throw new JobFailedException(error.Message, transient: true, ErrorCodes.GitError);
            }

            await FailAsync(release.Id, ErrorCodes.GitError, error.Message, [], cancellationToken);
            return;
        }

        var working = preparation.Result.Value;
        var version = await db.ReleaseVersions.AsNoTracking()
            .Where(v => v.Id == release.ReleaseVersionId)
            .Select(v => new { v.Version, v.PullRequestNumber })
            .FirstOrDefaultAsync(cancellationToken);

        // Preparing can take a while: immediately before the merge, confirm (and claim, by touching the project row) that the
        // project is still releasing this version and the attempt is still in progress. A rollback that read the project
        // earlier then fails its xmin check instead of rolling back over a merge.
        if (!await ConfirmStillReleasingAsync(project.Id, payload.ProjectVersion, release.Id, cancellationToken))
        {
            LogSkipped(logger, "the project left release or the attempt ended before the merge");
            return;
        }

        var request = new ReleaseRequest(
            working.Branch,
            release.Tag,
            version?.Version ?? string.Empty,
            project.Name,
            version?.PullRequestNumber,
            (sha, ct) => RecordMergeAsync(release.Id, sha, ct));
        var outcome = await working.Provider.ReleaseAsync(working.Connection, request, working.Token, cancellationToken);
        switch (outcome.Kind)
        {
            case ReleaseOutcomeKind.Released:
                await SucceedAsync(release.Id, outcome, cancellationToken);
                return;
            case ReleaseOutcomeKind.Conflict:
                await FailAsync(release.Id, ErrorCodes.MergeConflict, outcome.Reason!, outcome.ConflictingFiles, cancellationToken);
                return;
            case ReleaseOutcomeKind.Failed when outcome.Transient:
                throw new JobFailedException(outcome.Reason!, transient: true, outcome.ErrorCode);
            default:
                await FailAsync(release.Id, outcome.ErrorCode ?? ErrorCodes.GitError, outcome.Reason!, [], cancellationToken);
                return;
        }
    }

    /// <summary>The in-progress attempt at the project's version; a job queued without one (e.g. before this phase) gets one.</summary>
    private async Task<ReleaseEntity?> AttemptAsync(Project project, CancellationToken cancellationToken)
    {
        var latest = await ReleaseRecords.LatestAtAsync(db, project.Id, project.Checkpoint, track: false, cancellationToken);
        if (latest is not null)
        {
            return latest.Status == ReleaseStatus.InProgress ? latest : null;
        }

        var created = await ReleaseRecords.AddAttemptAsync(db, project, ReleaseRecords.PhaseEntered, SystemActor.Name, time.GetUtcNow(), cancellationToken);
        await SaveAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return created;
    }

    /// <summary>
    /// Re-reads the project (tracked) and the attempt: still release/releasing at <paramref name="projectVersion"/> with the
    /// attempt in progress → touches the project (its <c>xmin</c> moves, so a concurrent writer that read it earlier gets a
    /// conflict) and returns true. A concurrent change makes the save fail, and the job retries (and checks again).
    /// </summary>
    private async Task<bool> ConfirmStillReleasingAsync(Guid projectId, int projectVersion, Guid releaseId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null || !ReleaseRules.ShouldRun(State(project), projectVersion))
        {
            return false;
        }

        var attempt = await db.Releases.AsNoTracking().Where(r => r.Id == releaseId).Select(r => (ReleaseStatus?)r.Status).FirstOrDefaultAsync(cancellationToken);
        if (attempt != ReleaseStatus.InProgress)
        {
            return false;
        }

        project.UpdatedAt = time.GetUtcNow();
        await SaveAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return true;
    }

    /// <summary>
    /// Puts the merge commit on the attempt as soon as the merge exists, before tagging: a rollback then knows the release was
    /// partially applied (<c>ROLLBACK_BLOCKED</c> while its job waits to retry), and the history shows the merge if tagging fails.
    /// </summary>
    private async Task RecordMergeAsync(Guid releaseId, string mergeCommitSha, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var release = await db.Releases.FirstAsync(r => r.Id == releaseId, cancellationToken);
        if (release.Status != ReleaseStatus.InProgress || release.MergeCommitSha == mergeCommitSha)
        {
            return;
        }

        release.MergeCommitSha = mergeCommitSha;
        await SaveAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Records the release. Released → the attempt succeeds and the project becomes <c>released</c> (state machine); if the
    /// project moved on meanwhile, the Git facts are still recorded on the attempt but the project isn't touched.
    /// </summary>
    private async Task SucceedAsync(Guid releaseId, ReleaseOutcome outcome, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var release = await db.Releases.FirstAsync(r => r.Id == releaseId, cancellationToken);
        if (release.Status != ReleaseStatus.InProgress)
        {
            LogSkipped(logger, "the release attempt was already recorded");
            return;
        }

        // FR-64: the tag is the version's own (<prefix><version>); the provider created it, or found it already holding this
        // release (alreadyTagged).
        release.Status = ReleaseStatus.Succeeded;
        release.Tag = outcome.Tag ?? release.Tag;
        release.MergeCommitSha = outcome.MergeCommitSha;
        release.CompletedAt = now;
        var project = await db.Projects.FirstAsync(p => p.Id == release.ProjectId, cancellationToken);
        if (ReleaseRules.ShouldRun(State(project), release.Checkpoint))
        {
            var completed = LifecycleStateMachine.Apply(State(project), TransitionRequest.ReleaseCompleted(CompletedReason), LifecycleSnapshot.Empty);
            if (completed.IsFailure)
            {
                throw new JobFailedException(completed.Error!.Message, transient: true, completed.Error.Code);
            }

            await writer.ApplyAsync(project, completed.Value, SystemActor.Name, now, cancellationToken);
            await MarkVersionReleasedAsync(project, release, now, cancellationToken);
        }
        else
        {
            LogProjectMoved(logger, project.Id, release.Checkpoint);
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
                alreadyTagged = outcome.AlreadyTagged,
            },
            SystemActor.Name,
            now);
        await SaveAsync(cancellationToken);
        LogReleased(logger, release.ProjectId, release.Tag, outcome.AlreadyTagged);
    }

    /// <summary>
    /// FR-64, §4.10: the release version becomes <c>released</c> (tag, merge commit, released_at) and the project's
    /// denormalised versions follow: released = the highest released version, nothing in development. Same unit of work.
    /// </summary>
    private async Task MarkVersionReleasedAsync(Project project, ReleaseEntity release, DateTimeOffset now, CancellationToken cancellationToken)
    {
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

    private async Task FailAsync(Guid releaseId, string errorCode, string message, IReadOnlyList<string> conflictingFiles, CancellationToken cancellationToken)
    {
        var release = await db.Releases.FirstAsync(r => r.Id == releaseId, cancellationToken);
        if (release.Status != ReleaseStatus.InProgress)
        {
            LogSkipped(logger, "the release attempt was already recorded");
            return;
        }

        ReleaseRecords.Fail(db, release, errorCode, message, conflictingFiles, SystemActor.Name, time.GetUtcNow());
        await SaveAsync(cancellationToken);
        LogFailed(logger, release.ProjectId, release.Tag, errorCode, conflictingFiles.Count);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            // A concurrent change (e.g. a rollback): retry; the next attempt checks again (and finds the tag if it was made).
            throw new JobFailedException(saved.Error!.Message, transient: true, saved.Error.Code);
        }
    }

    private static LifecycleState State(Project project) => new(project.CurrentPhase, project.Status, project.Checkpoint);

    private static LifecycleJobPayload Payload(JobExecution job)
    {
        try
        {
            return AideJson.Deserialize<LifecycleJobPayload>(job.Payload);
        }
        catch (JsonException ex)
        {
            throw new JobFailedException($"The release job payload can't be read: {ex.Message}", transient: false, ErrorCodes.Validation);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Release job did nothing: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} released as {Tag} (tag already existed: {AlreadyTagged})")]
    private static partial void LogReleased(ILogger logger, Guid projectId, string tag, bool alreadyTagged);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Release {Tag} of project {ProjectId} failed ({ErrorCode}, {ConflictCount} conflicting files)")]
    private static partial void LogFailed(ILogger logger, Guid projectId, string tag, string errorCode, int conflictCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId} left release checkpoint {Checkpoint} while it was being released; the release is recorded, the project is unchanged")]
    private static partial void LogProjectMoved(ILogger logger, Guid projectId, int checkpoint);
}

/// <summary>A release job that ended without succeeding (retries used up) fails the project's in-progress attempts with its last (token-free) error.</summary>
public sealed class ReleaseJobEndedHook(TimeProvider time) : IJobEndedHook
{
    public async Task OnJobEndedAsync(AideDbContext db, JobEnded job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobType != JobTypes.Release || job.ProjectId is not { } projectId)
        {
            return;
        }

        var inProgress = await db.Releases
            .Where(r => r.ProjectId == projectId && r.Status == ReleaseStatus.InProgress)
            .ToListAsync(cancellationToken);
        foreach (var release in inProgress)
        {
            ReleaseRecords.Fail(
                db,
                release,
                ErrorCodes.GitError,
                job.Error ?? "The release job failed.",
                [],
                SystemActor.Name,
                time.GetUtcNow(),
                job.DeadLettered);
        }
    }
}

public static class ReleaseServiceCollectionExtensions
{
    /// <summary>FR-42, FR-43: the release service, the release job and its ended hook.</summary>
    public static IServiceCollection AddReleases(this IServiceCollection services)
    {
        services.AddScoped<ReleaseService>();
        services.AddScoped<IJobEndedHook, ReleaseJobEndedHook>();
        services.AddJobHandler<ReleaseJobHandler>(JobTypes.Release);
        return services;
    }
}
