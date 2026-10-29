using System.Text.Json;
using Aide.Api.Features.Execution;
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
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Release;

/// <summary>
/// FR-65, §4.14, NFR-REL2: the merge poll of an <c>awaiting_merge</c> release attempt. One job row per attempt that defers
/// itself between polls (a wait, never a blocking sleep or a counted attempt), so polling survives restarts:
/// <list type="bullet">
/// <item>GitHub: <c>GET /pulls/{n}</c> with the last ETag (<c>If-None-Match</c>; a 304 is "no change"). <c>merged</c> →
/// the PR's <c>merge_commit_sha</c> is the tag target (authoritative even for squash or rebase merges); closed without merging
/// → <c>PR_CLOSED</c>.</item>
/// <item>GitLab (FR-140): <c>GET …/merge_requests/:iid</c>: <c>merged</c> → the merged commit is tagged with the GitLab
/// Release; <c>closed</c> → <c>PR_CLOSED</c>. Under 10 % of the rate limit left pushes the next poll to the reset
/// (§4.19).</item>
/// <item>Local: the version branch head is in the default branch (<c>merge-base --is-ancestor</c>) → the merge commit is the
/// oldest first-parent commit of the default branch that contains it (the head itself after a fast-forward).</item>
/// </list>
/// Then, per <see cref="MergePollRules"/>: wait (2 minutes for the first hour, then 10), complete (tag, GitHub Release,
/// released — <c>RELEASE_MERGED_EXTERNALLY</c>), or fail after 14 days (<c>MERGE_TIMEOUT</c>); after a failure the project
/// is back to <c>releasing</c> for a retry. A GitHub rate limit defers to the reset without counting an attempt; GitHub
/// failing is a poll without news.
/// </summary>
public sealed partial class MergePollJobHandler(
    AideDbContext db,
    GitHubRepoAccess access,
    GitHostTargets hosts,
    GitHubApi api,
    WorkingCopyPreparer preparer,
    IWorkingCopies workingCopies,
    ReleaseCompletion completion,
    GitHubReleasePublisher publisher,
    TimeProvider time,
    ILogger<MergePollJobHandler> logger) : IJobHandler
{
    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = Payload(job);
        var release = await db.Releases.AsNoTracking().FirstOrDefaultAsync(r => r.Id == payload.ReleaseId && r.ProjectId == payload.ProjectId, cancellationToken);
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == payload.ProjectId, cancellationToken);
        if (release is null || release.Status != ReleaseStatus.AwaitingMerge || project is null
            || project.CurrentPhase != Phase.Release || project.Status != ProjectStatus.AwaitingMerge || project.Checkpoint != release.Checkpoint)
        {
            LogSkipped(logger, "the attempt is no longer awaiting its merge");
            return;
        }

        var now = time.GetUtcNow();
        var since = release.AwaitingMergeSince ?? release.CreatedAt;
        var version = await db.ReleaseVersions.AsNoTracking()
            .Where(v => v.Id == release.ReleaseVersionId)
            .Select(v => new { v.PullRequestNumber, v.BranchName })
            .FirstOrDefaultAsync(cancellationToken);

        MergePollObservation observation;
        DateTimeOffset? notBefore = null;
        var etag = payload.ETag;
        try
        {
            if (await hosts.ResolveAsync(project.Id, cancellationToken) is { } host)
            {
                (observation, notBefore) = await ObserveHostAsync(project, host, version?.PullRequestNumber, cancellationToken);
            }
            else
            {
                (observation, etag) = await ObserveAsync(project, version?.PullRequestNumber, version?.BranchName, etag, cancellationToken);
            }
        }
        catch (GitHubRateLimitedException ex)
        {
            // §4.14, NFR-REL2: back off until the reset without counting an attempt (and without changing the schedule).
            var until = JobPolicy.DeferUntil(now, ex.ResetAt);
            throw new JobDeferredException($"{ErrorCodes.GitHubRateLimited}: GitHub's rate limit is exhausted; the merge poll waits until {until:u}.", until);
        }

        var decision = MergePollRules.Decide(since, now, observation);
        LogPolled(logger, project.Id, observation, decision.Kind);
        switch (decision.Kind)
        {
            case MergePollDecisionKind.Wait:
                // §4.19: a GitLab rate limit running low pushes the next poll to its reset.
                var next = Domain.GitHosts.GitHostPollBudget.Later(decision.NextPollAt!.Value, notBefore);
                throw new JobDeferredException(
                    $"Waiting for the pull request to be merged (next poll {next:u}).",
                    next,
                    AideJson.Serialize(payload with { ETag = etag }));
            case MergePollDecisionKind.Fail:
                await completion.AbandonAsync(release.Id, decision.ErrorCode!, decision.Message!, cancellationToken);
                return;
            default:
                await CompleteAsync(release, cancellationToken);
                return;
        }
    }

    /// <summary>
    /// FR-140, §4.19: what the GitLab merge request shows now (<c>merged</c>, <c>closed</c> or still open), and the
    /// rate-limit back-off for the next poll. GitLab failing is a poll without news; a 429 waits for the reset (the job
    /// worker re-queues it without counting an attempt).
    /// </summary>
    private async Task<(MergePollObservation Observation, DateTimeOffset? NotBefore)> ObserveHostAsync(
        Project project, GitHostTarget host, int? pullRequestNumber, CancellationToken cancellationToken)
    {
        if (pullRequestNumber is not { } number)
        {
            return (MergePollObservation.Open, null);
        }

        var observed = await host.Provider.ObserveMergeRequestAsync(host.Connection, number, cancellationToken);
        if (observed.IsFailure)
        {
            LogNoAccess(logger, project.Id, observed.Error!.Message);
            return (MergePollObservation.Open, null);
        }

        return (observed.Value.Observation, observed.Value.NotBefore);
    }

    /// <summary>What the PR (GitHub) or the version branch (Local) shows now, and the PR's ETag for the next poll.</summary>
    private async Task<(MergePollObservation Observation, string? ETag)> ObserveAsync(
        Project project, int? pullRequestNumber, string? branch, string? etag, CancellationToken cancellationToken)
    {
        var target = await access.ResolveAsync(project.Id, cancellationToken);
        if (target.IsFailure)
        {
            LogNoAccess(logger, project.Id, target.Error!.Message);
            return (MergePollObservation.Open, etag);
        }

        if (target.Value is { } github)
        {
            if (pullRequestNumber is not { } number)
            {
                return (MergePollObservation.Open, etag);
            }

            try
            {
                var poll = await api.PollPullAsync(github.Token, github.Owner, github.Repo, number, etag, cancellationToken);
                if (poll.NotModified || poll.Pull is not { } pull)
                {
                    return (MergePollObservation.NotModified, poll.ETag ?? etag);
                }

                var observed = pull.Merged
                    ? MergePollObservation.Merged
                    : pull.IsClosedUnmerged ? MergePollObservation.ClosedUnmerged : MergePollObservation.Open;
                return (observed, poll.ETag);
            }
            catch (GitHubApiException ex)
            {
                // A failed poll is a poll without news; the 14-day ceiling still applies.
                LogUpstream(logger, project.Id, ex.Status);
                return (MergePollObservation.Open, etag);
            }
        }

        // Local: merged by someone else when the version branch head is in the default branch.
        var settings = await db.ProjectGitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == project.Id, cancellationToken);
        if (settings is null || branch is null)
        {
            return (MergePollObservation.Open, null);
        }

        try
        {
            var head = await workingCopies.ResolveCommitAsync(project.Id, $"refs/heads/{branch}", cancellationToken);
            var merged = head is not null
                && await workingCopies.FindMergeCommitAsync(project.Id, $"refs/heads/{settings.DefaultBranch}", head, cancellationToken) is not null;
            return (merged ? MergePollObservation.Merged : MergePollObservation.Open, null);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            LogLocalFailed(logger, project.Id, ex.GetType().Name);
            return (MergePollObservation.Open, null);
        }
    }

    /// <summary>
    /// Merged: tag the merge commit (the provider reads the PR's <c>merge_commit_sha</c>, or finds the Local merge; never
    /// merges), create the GitHub Release and complete the release. A tag on another commit fails it (<c>TAG_EXISTS</c>).
    /// </summary>
    private async Task CompleteAsync(Domain.Entities.Release release, CancellationToken cancellationToken)
    {
        var preparation = await preparer.TryPrepareAsync(release.ProjectId, cancellationToken);
        if (preparation.Result.IsFailure)
        {
            var error = preparation.Result.Error!;
            if (preparation.Transient)
            {
                throw new JobFailedException(error.Message, transient: true, ErrorCodes.GitError);
            }

            await completion.AbandonAsync(release.Id, ErrorCodes.GitError, error.Message, cancellationToken);
            return;
        }

        var working = preparation.Result.Value;
        var project = await db.Projects.AsNoTracking().Where(p => p.Id == release.ProjectId).Select(p => p.Name).FirstAsync(cancellationToken);
        var version = await db.ReleaseVersions.AsNoTracking()
            .Where(v => v.Id == release.ReleaseVersionId)
            .Select(v => new { v.Version, v.PullRequestNumber })
            .FirstOrDefaultAsync(cancellationToken);
        var request = new ReleaseRequest(
            working.Branch,
            release.Tag,
            version?.Version ?? string.Empty,
            project,
            version?.PullRequestNumber,
            (sha, ct) => RecordMergeAsync(release.Id, sha, ct))
        {
            Merge = false,
            Notes = await publisher.HostNotesAsync(release, working.Provider.Kind, working.Connection.RepoUrl, cancellationToken),
        };
        var outcome = await working.Provider.ReleaseAsync(working.Connection, request, working.Token, cancellationToken);
        switch (outcome.Kind)
        {
            case ReleaseOutcomeKind.Released:
                var url = outcome.ReleaseUrl ?? await publisher.PublishAsync(release, outcome.Tag ?? release.Tag, cancellationToken);
                await completion.SucceedAsync(release.Id, outcome, url, mergedExternally: true, cancellationToken);
                return;
            case ReleaseOutcomeKind.AwaitingMerge:
                // The merge isn't visible yet (e.g. GitHub's PR read raced the merge): poll again shortly.
                throw new JobDeferredException("The merge isn't visible in the repository yet.", time.GetUtcNow() + MergePollRules.FastInterval);
            case ReleaseOutcomeKind.Failed when outcome.Transient:
                throw new JobFailedException(outcome.Reason!, transient: true, outcome.ErrorCode);
            default:
                await completion.AbandonAsync(release.Id, outcome.ErrorCode ?? ErrorCodes.GitError, outcome.Reason ?? "The release could not be tagged.", cancellationToken);
                return;
        }
    }

    /// <summary>Puts the merge commit on the awaiting attempt as soon as it is known (before tagging).</summary>
    private async Task RecordMergeAsync(Guid releaseId, string mergeCommitSha, CancellationToken cancellationToken) =>
        await db.Releases
            .Where(r => r.Id == releaseId && r.Status == ReleaseStatus.AwaitingMerge)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.MergeCommitSha, mergeCommitSha), cancellationToken);

    private static MergePollJobPayload Payload(JobExecution job)
    {
        try
        {
            return AideJson.Deserialize<MergePollJobPayload>(job.Payload);
        }
        catch (JsonException ex)
        {
            throw new JobFailedException($"The merge poll payload can't be read: {ex.Message}", transient: false, ErrorCodes.Validation);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Merge poll did nothing: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Merge poll of project {ProjectId}: {Observation} → {Decision}")]
    private static partial void LogPolled(ILogger logger, Guid projectId, MergePollObservation observation, MergePollDecisionKind decision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Merge poll of project {ProjectId}: GitHub failed with HTTP {Status}; polling again later")]
    private static partial void LogUpstream(ILogger logger, Guid projectId, int? status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Merge poll of project {ProjectId} can't reach the repository: {Reason}")]
    private static partial void LogNoAccess(ILogger logger, Guid projectId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Merge poll of Local project {ProjectId} couldn't read the repository ({ErrorType})")]
    private static partial void LogLocalFailed(ILogger logger, Guid projectId, string errorType);
}

/// <summary>
/// A merge poll that ended without succeeding (its completion's retries ran out) fails the project's awaiting attempts with
/// its last (token-free) error and puts the project back to <c>releasing</c>, so an Owner can retry the release.
/// </summary>
public sealed class MergePollJobEndedHook(TimeProvider time) : IJobEndedHook
{
    public async Task OnJobEndedAsync(AideDbContext db, JobEnded job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobType != JobTypes.MergePoll || job.ProjectId is not { } projectId)
        {
            return;
        }

        var now = time.GetUtcNow();
        var awaiting = await db.Releases.Where(r => r.ProjectId == projectId && r.Status == ReleaseStatus.AwaitingMerge).ToListAsync(cancellationToken);
        if (awaiting.Count == 0)
        {
            return;
        }

        var provider = await ReleaseRecords.ProviderAsync(db, projectId, cancellationToken);
        foreach (var release in awaiting)
        {
            ReleaseRecords.Fail(db, release, ErrorCodes.GitError, job.Error ?? "The merge poll failed.", [], SystemActor.Name, now, job.DeadLettered, provider);
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is not null)
        {
            var writer = new LifecycleWriter(db, new JobEnqueuer(db, time));
            await ReleaseCompletion.BackToReleasingAsync(db, writer, project, $"Release failed: {ErrorCodes.GitError}", now, cancellationToken);
        }
    }
}
