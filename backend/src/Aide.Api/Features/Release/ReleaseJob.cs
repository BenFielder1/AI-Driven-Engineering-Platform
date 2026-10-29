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
/// <item>FR-65: under the <c>manual</c> strategy nothing is merged (GitHub: the PR is marked ready for review); a merge
/// GitHub refuses (branch protection, required reviews or checks, missing permission) is not a failure either: the attempt,
/// the version and the project become <c>awaiting_merge</c> with the merge-readiness reasons (FR-84), Owners are notified
/// and the merge poll (§4.14) waits for the merge on GitHub.</item>
/// <item>FR-64: once tagged, a GitHub project gets its GitHub Release (<see cref="GitHubReleasePublisher"/>). FR-140: a
/// GitLab project's provider merges the merge request and creates the GitLab Release (with the annotated tag and AIDE's
/// notes) itself; its URL is recorded where the GitHub Release URL is.</item>
/// </list>
/// Transient failures retry the job; when its retries run out, <see cref="ReleaseJobEndedHook"/> fails the attempt.
/// </summary>
public sealed partial class ReleaseJobHandler(
    AideDbContext db,
    WorkingCopyPreparer preparer,
    ReleaseCompletion completion,
    GitHubReleasePublisher publisher,
    Git.MergeReadinessService readiness,
    TimeProvider time,
    ILogger<ReleaseJobHandler> logger) : IJobHandler
{
    public const string CompletedReason = ReleaseCompletion.CompletedReason;

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

        // FR-65: the manual strategy never merges; the release then waits for the merge on GitHub (or in the Local repository).
        var merge = project.MergeStrategy == MergeStrategy.AideMerges;
        var request = new ReleaseRequest(
            working.Branch,
            release.Tag,
            version?.Version ?? string.Empty,
            project.Name,
            version?.PullRequestNumber,
            (sha, ct) => RecordMergeAsync(release.Id, sha, ct))
        {
            Merge = merge,
            Notes = await publisher.HostNotesAsync(release, working.Provider.Kind, working.Connection.RepoUrl, cancellationToken),
            RecordedMergeCommitSha = release.MergeCommitSha,
        };
        var outcome = await working.Provider.ReleaseAsync(working.Connection, request, working.Token, cancellationToken);
        switch (outcome.Kind)
        {
            case ReleaseOutcomeKind.Released:
                var url = outcome.ReleaseUrl ?? await publisher.PublishAsync(release, outcome.Tag ?? release.Tag, cancellationToken);
                await completion.SucceedAsync(release.Id, outcome, url, mergedExternally: outcome.MergedExternally, cancellationToken);
                return;
            case ReleaseOutcomeKind.AwaitingMerge:
                await completion.AwaitMergeAsync(release.Id, project.MergeStrategy, await AwaitingReasonsAsync(project, outcome, cancellationToken), cancellationToken);
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
    /// FR-65, FR-84: why the release waits for a merge on GitHub: the merge-readiness reasons (<c>manual_strategy</c> first
    /// for the manual strategy). When readiness can't be read, or finds nothing although GitHub refused, GitHub's own answer
    /// is the reason (<c>branch_protected</c>): the merge endpoint is authoritative.
    /// </summary>
    private async Task<IReadOnlyList<Git.MergeReadinessReasonDto>> AwaitingReasonsAsync(Project project, ReleaseOutcome outcome, CancellationToken cancellationToken)
    {
        List<Git.MergeReadinessReasonDto> reasons = [];
        try
        {
            var computed = await readiness.ComputeAsync(project.Id, cancellationToken);
            if (computed.IsSuccess)
            {
                reasons.AddRange(computed.Value.Reasons);
            }
        }
        catch (Exception ex) when (ex is Infrastructure.GitHub.GitHubRateLimitedException or Infrastructure.GitLab.GitLabRateLimitedException)
        {
            LogReadinessRateLimited(logger, project.Id);
        }

        // FR-140: the host's refusal is authoritative: its implied reason (GitLab 403 → no_permission), else branch_protected.
        if (outcome.MergeRefused && reasons.All(r => r.Code == MergeReadinessReasonCode.ManualStrategy))
        {
            reasons.Add(new Git.MergeReadinessReasonDto(outcome.RefusedReason ?? MergeReadinessReasonCode.BranchProtected, outcome.Reason ?? "The Git host refused the merge."));
        }

        // FR-138: AIDE couldn't take the merge request out of draft: say so (readiness ignores the draft until AIDE marked it ready).
        if (outcome.StillDraft && reasons.All(r => r.Code != MergeReadinessReasonCode.Draft))
        {
            reasons.Add(new Git.MergeReadinessReasonDto(MergeReadinessReasonCode.Draft, "AIDE couldn't mark the merge request ready; it is still a draft."));
        }

        if (project.MergeStrategy == MergeStrategy.Manual && reasons.All(r => r.Code != MergeReadinessReasonCode.ManualStrategy))
        {
            reasons.Insert(0, new Git.MergeReadinessReasonDto(
                MergeReadinessReasonCode.ManualStrategy, "The project's merge strategy is manual: AIDE never merges; merge the pull request on the Git host."));
        }

        return reasons;
    }

    private async Task FailAsync(Guid releaseId, string errorCode, string message, IReadOnlyList<string> conflictingFiles, CancellationToken cancellationToken)
    {
        var release = await db.Releases.FirstAsync(r => r.Id == releaseId, cancellationToken);
        if (release.Status != ReleaseStatus.InProgress)
        {
            LogSkipped(logger, "the release attempt was already recorded");
            return;
        }

        ReleaseRecords.Fail(
            db, release, errorCode, message, conflictingFiles, SystemActor.Name, time.GetUtcNow(), provider: await ReleaseRecords.ProviderAsync(db, release.ProjectId, cancellationToken));
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Merge readiness of project {ProjectId} was rate limited; the awaiting release records the Git host's answer only")]
    private static partial void LogReadinessRateLimited(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Release {Tag} of project {ProjectId} failed ({ErrorCode}, {ConflictCount} conflicting files)")]
    private static partial void LogFailed(ILogger logger, Guid projectId, string tag, string errorCode, int conflictCount);
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
        var provider = inProgress.Count == 0 ? null : await ReleaseRecords.ProviderAsync(db, projectId, cancellationToken);
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
                job.DeadLettered,
                provider);
        }
    }
}

public static class ReleaseServiceCollectionExtensions
{
    /// <summary>FR-42, FR-43: the release service, the release job and its ended hook.</summary>
    public static IServiceCollection AddReleases(this IServiceCollection services)
    {
        services.AddScoped<ReleaseService>();
        services.AddScoped<ReleaseCompletion>();
        services.AddScoped<GitHubReleasePublisher>();
        services.AddScoped<IJobEndedHook, MergePollJobEndedHook>();
        services.AddJobHandler<MergePollJobHandler>(JobTypes.MergePoll);
        services.AddScoped<IJobEndedHook, ReleaseJobEndedHook>();
        services.AddJobHandler<ReleaseJobHandler>(JobTypes.Release);
        return services;
    }
}
