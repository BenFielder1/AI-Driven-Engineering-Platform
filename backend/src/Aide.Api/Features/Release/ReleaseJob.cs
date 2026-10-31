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
    Databases.MigrationRunService migrations,
    IWorkingCopies workingCopies,
    TimeProvider time,
    ILogger<ReleaseJobHandler> logger) : IJobHandler
{
    public const string CompletedReason = ReleaseCompletion.CompletedReason;

    /// <summary>FR-140: how soon a release whose merge the host is still performing looks again.</summary>
    public static readonly TimeSpan MergePendingRecheck = TimeSpan.FromSeconds(30);

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

        // §4.21: Supabase BaaS production migrations run before the code reaches the default branch (platforms deploy it on
        // merge): immediately before AIDE merges, or as the release enters awaiting_merge (manual). A failure blocks the merge,
        // and the merge is pinned to the commit they ran on.
        var migrated = await MigrateProductionAsync(project, release.Id, working.Branch, cancellationToken);
        if (migrated.Failed)
        {
            return;
        }

        // FR-65: the manual strategy never merges; the release then waits for the merge on GitHub (or in the Local repository).
        var merge = MergeReadinessRules.AideMerges(project.MergeStrategy);

        // §4.21: when readiness shows the PR can't merge, the migrations didn't run, so AIDE never calls an unpinned merge:
        // a conflict fails the release as FR-43 does (MERGE_CONFLICT with the files); a PR that isn't open is released
        // without merging (merged elsewhere: tagged; closed: PR_CLOSED), as the providers do for those states.
        if (merge && migrated.Blocked == MergeReadinessReasonCode.MergeConflict)
        {
            await FailConflictedAsync(release.Id, working, migrated.Head!, cancellationToken);
            return;
        }

        var notOpen = merge && migrated.Blocked == MergeReadinessReasonCode.PrNotOpen;
        merge &= !notOpen;
        var request = new ReleaseRequest(
            working.Branch,
            release.Tag,
            version?.Version ?? string.Empty,
            project.Name,
            version?.PullRequestNumber,
            (sha, ct) => RecordMergeAsync(release.Id, sha, ct))
        {
            Merge = merge,
            Strategy = project.MergeStrategy,
            Notes = await publisher.HostNotesAsync(release, working.Provider.Kind, working.Connection.RepoUrl, cancellationToken),
            RecordedMergeCommitSha = release.MergeCommitSha,
            MergeSubmitted = release.MergeSubmittedAt is not null,
            OnMergeSubmitting = ct => RecordMergeSubmittedAsync(release.Id, ct),
            ExpectedHeadSha = migrated.Head,
        };
        var outcome = await working.Provider.ReleaseAsync(working.Connection, request, working.Token, cancellationToken);
        switch (outcome.Kind)
        {
            case ReleaseOutcomeKind.Released:
                if (outcome.MergedHeadMismatch is { } mergedHead)
                {
                    await RecordMergedHeadMismatchAsync(project.Id, release.Id, migrated.Head, mergedHead, outcome.MergeCommitSha, cancellationToken);
                }

                var url = outcome.ReleaseUrl ?? await publisher.PublishAsync(release, outcome.Tag ?? release.Tag, cancellationToken);
                await completion.SucceedAsync(release.Id, outcome, url, mergedExternally: outcome.MergedExternally, cancellationToken);
                return;
            case ReleaseOutcomeKind.AwaitingMerge when notOpen:
                // Readiness said the PR wasn't open, but it is (reopened meanwhile): look again, migrating first.
                throw new JobFailedException("The pull request is open again; the release retries and runs the production migrations first.", transient: true, ErrorCodes.GitError);
            case ReleaseOutcomeKind.AwaitingMerge:
                await completion.AwaitMergeAsync(release.Id, project.MergeStrategy, await AwaitingReasonsAsync(project, outcome, cancellationToken), cancellationToken);
                return;
            case ReleaseOutcomeKind.Conflict:
                await FailAsync(release.Id, ErrorCodes.MergeConflict, outcome.Reason!, outcome.ConflictingFiles, cancellationToken);
                return;
            case ReleaseOutcomeKind.Failed when outcome.MergePending:
                await MergePendingAsync(project, release.Id, outcome, cancellationToken);
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
    /// §4.21, FR-153: runs the Supabase BaaS migrations of the PR head on the production database and records the run on
    /// the release (<c>releases.migration_run_id</c>). The head is exactly the commit the provider will merge: the fetched
    /// remote-tracking branch (which the host's PR head must equal), else the local branch (Local Git); the merge is then
    /// pinned to it (<see cref="ReleaseRequest.ExpectedHeadSha"/>). A succeeded run counts only for that same commit: a head
    /// that moved runs the migrations again first. Skipped when the project isn't BaaS (host mode migrates on the platform,
    /// FR-154), when the attempt already merged or submitted its merge, and when merge readiness shows the PR can't merge at
    /// all (a conflict, or not open): unmerged migrations never run against production for nothing. <c>Failed</c>: the run
    /// failed and the release was failed with its code (<c>MIGRATION_FAILED</c>, <c>MIGRATION_HISTORY_DIVERGED</c>); nothing
    /// is merged then. <c>Head</c>: the commit migrated (the merge pin), or <c>null</c> when nothing ran.
    /// </summary>
    private async Task<(bool Failed, string? Head, MergeReadinessReasonCode? Blocked)> MigrateProductionAsync(Project project, Guid releaseId, string branch, CancellationToken cancellationToken)
    {
        var release = await db.Releases.AsNoTracking().FirstAsync(r => r.Id == releaseId, cancellationToken);
        if (release.MergeCommitSha is not null || release.MergeSubmittedAt is not null
            || !await migrations.HasMigrationStepAsync(project.Id, DeploymentEnvironment.Production, cancellationToken))
        {
            return (false, null, null);
        }

        var head = await MergeHeadAsync(project.Id, branch, cancellationToken)
            ?? throw new JobFailedException("The version branch can't be read to run the production migrations.", transient: true, ErrorCodes.GitError);
        if (release.MigrationRunId is { } runId
            && await db.DatabaseMigrationRuns.AsNoTracking().AnyAsync(r => r.Id == runId && r.Status == MigrationRunStatus.Succeeded && r.GitSha == head, cancellationToken))
        {
            return (false, head, null);
        }

        // Unknown readiness (an error, a rate limit) doesn't block: the migrations run and the merge is pinned to them.
        if (await BlockingReasonAsync(project, cancellationToken) is { } blocked)
        {
            LogMigrationsDeferred(logger, project.Id);
            return (false, head, blocked);
        }

        DatabaseMigrationRun run;
        try
        {
            run = await migrations.RunAsync(project.Id, DeploymentEnvironment.Production, release.ReleaseVersionId, release.Id, head, cancellationToken);
        }
        catch (Databases.MigrationTargetMissingException)
        {
            return (false, null, null);
        }

        db.ChangeTracker.Clear();
        var tracked = await db.Releases.FirstAsync(r => r.Id == releaseId, cancellationToken);
        tracked.MigrationRunId = run.Id;
        await SaveAsync(cancellationToken);
        db.ChangeTracker.Clear();
        if (run.Status == MigrationRunStatus.Succeeded)
        {
            return (false, head, null);
        }

        await FailAsync(
            releaseId,
            run.ErrorCode ?? ErrorCodes.MigrationFailed,
            $"The production database migrations failed, so nothing was merged: {run.ErrorMessage ?? "see the migration run"}",
            [],
            cancellationToken);
        return (true, null, null);
    }

    /// <summary>
    /// FR-43 for a BaaS release blocked before its migrations: the conflicting files from a trial merge of the head into the
    /// default branch, and the release fails with <c>MERGE_CONFLICT</c>. A trial merge that is clean (readiness was stale)
    /// retries the release.
    /// </summary>
    private async Task FailConflictedAsync(Guid releaseId, PreparedWorkingCopy working, string head, CancellationToken cancellationToken)
    {
        var files = await workingCopies.TrialMergeConflictsAsync(working.Connection.ProjectId, working.Provider.ComparisonBaseRef(working.Connection), head, cancellationToken);
        if (files.Count == 0)
        {
            throw new JobFailedException("Merge readiness reported a conflict the branch no longer has; the release retries.", transient: true, ErrorCodes.GitError);
        }

        await FailAsync(
            releaseId,
            ErrorCodes.MergeConflict,
            $"The pull request from {working.Branch} conflicts with {working.Connection.DefaultBranch} in {files.Count} file(s). Resolve them on the project branch, then retry the release.",
            files,
            cancellationToken);
    }

    /// <summary>
    /// The commit the provider merges (as the GitHub, GitLab and Bitbucket providers resolve it): the fetched
    /// remote-tracking branch, else the local branch (Local Git has no remote).
    /// </summary>
    private async Task<string?> MergeHeadAsync(Guid projectId, string branch, CancellationToken cancellationToken) =>
        await workingCopies.ResolveCommitAsync(projectId, $"refs/remotes/{GitRemote.Name}/{branch}", cancellationToken)
        ?? await workingCopies.ResolveCommitAsync(projectId, $"refs/heads/{branch}", cancellationToken);

    /// <summary>
    /// Why merge readiness says the PR can't be merged at all: <c>merge_conflict</c> (first) or <c>pr_not_open</c>;
    /// <c>null</c> when it can, or when readiness is unknown (that doesn't block).
    /// </summary>
    private async Task<MergeReadinessReasonCode?> BlockingReasonAsync(Project project, CancellationToken cancellationToken)
    {
        try
        {
            var computed = await readiness.ComputeAsync(project.Id, cancellationToken);
            if (computed.IsFailure)
            {
                return null;
            }

            var codes = computed.Value.Reasons.Select(r => r.Code).ToList();
            return codes.Contains(MergeReadinessReasonCode.MergeConflict) ? MergeReadinessReasonCode.MergeConflict
                : codes.Contains(MergeReadinessReasonCode.PrNotOpen) ? MergeReadinessReasonCode.PrNotOpen
                : null;
        }
        catch (Exception ex) when (ex is Infrastructure.GitHub.GitHubRateLimitedException or Infrastructure.GitLab.GitLabRateLimitedException
            or Infrastructure.Bitbucket.BitbucketRateLimitedException)
        {
            LogReadinessRateLimited(logger, project.Id);
            return null;
        }
    }

    /// <summary>
    /// §4.21: the host merged another head than the one migrated before the merge (Bitbucket can't pin it): logged and
    /// recorded (<c>MERGED_HEAD_MISMATCH</c>); the production rollout's post-merge run on the merge commit is relied on.
    /// </summary>
    private async Task RecordMergedHeadMismatchAsync(
        Guid projectId, Guid releaseId, string? expectedHead, string mergedHead, string? mergeCommitSha, CancellationToken cancellationToken)
    {
        LogMergedHeadMismatch(logger, projectId, mergedHead, expectedHead);
        db.ChangeTracker.Clear();
        db.AddEvent(
            EventTypes.MergedHeadMismatch,
            projectId,
            new { releaseId, expectedHeadSha = expectedHead, mergedHeadSha = mergedHead, mergeCommitSha },
            SystemActor.Name,
            time.GetUtcNow());
        await SaveAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: the host merged head {MergedHead}, not the migrated head {ExpectedHead}; the post-merge production run migrates it")]
    private static partial void LogMergedHeadMismatch(ILogger logger, Guid projectId, string mergedHead, string? expectedHead);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: production migrations wait: merge readiness shows the PR can't merge")]
    private static partial void LogMigrationsDeferred(ILogger logger, Guid projectId);

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
    /// FR-140: the host accepted AIDE's merge and is still performing it. Within <see cref="ReleaseRules.SubmittedMergeCeiling"/>
    /// of the submission the job looks again shortly without counting an attempt (the attempt keeps
    /// <c>merge_submitted_at</c>, so the retry knows the merge is AIDE's own); after it, the attempt becomes
    /// <c>awaiting_merge</c> and the merge poll takes over (its 14-day ceiling, <c>PR_CLOSED</c>).
    /// </summary>
    private async Task MergePendingAsync(Project project, Guid releaseId, ReleaseOutcome outcome, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var submittedAt = await db.Releases.AsNoTracking().Where(r => r.Id == releaseId).Select(r => r.MergeSubmittedAt).FirstOrDefaultAsync(cancellationToken);
        if (ReleaseRules.KeepWaitingForSubmittedMerge(submittedAt, now))
        {
            throw new JobDeferredException(outcome.Reason!, now + MergePendingRecheck);
        }

        LogMergePendingTooLong(logger, project.Id);
        var reasons = (await AwaitingReasonsAsync(project, outcome, cancellationToken)).ToList();
        if (reasons.All(r => r.Code == MergeReadinessReasonCode.ManualStrategy))
        {
            reasons.Add(new Git.MergeReadinessReasonDto(
                MergeReadinessReasonCode.ChecksPending, "The Git host accepted the merge but hasn't finished it after an hour; AIDE keeps checking and tags it once merged."));
        }

        await completion.AwaitMergeAsync(releaseId, project.MergeStrategy, reasons, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: the host is still performing AIDE's merge after an hour; the merge poll takes over")]
    private static partial void LogMergePendingTooLong(ILogger logger, Guid projectId);

    /// <summary>
    /// FR-140: puts "merge submitted" on the attempt right before the host's merge call (a Bitbucket merge can finish
    /// asynchronously): a rollback then waits for the release, and a retry knows a merged pull request is AIDE's own.
    /// </summary>
    private async Task RecordMergeSubmittedAsync(Guid releaseId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var release = await db.Releases.FirstAsync(r => r.Id == releaseId, cancellationToken);
        if (release.Status != ReleaseStatus.InProgress || release.MergeSubmittedAt is not null)
        {
            return;
        }

        release.MergeSubmittedAt = time.GetUtcNow();
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
        catch (Exception ex) when (ex is Infrastructure.GitHub.GitHubRateLimitedException or Infrastructure.GitLab.GitLabRateLimitedException
            or Infrastructure.Bitbucket.BitbucketRateLimitedException)
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
