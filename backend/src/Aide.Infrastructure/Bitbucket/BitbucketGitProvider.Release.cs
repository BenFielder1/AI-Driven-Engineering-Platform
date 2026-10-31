using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Domain.Releases;
using Aide.Infrastructure.Git;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.Bitbucket;

/// <summary>
/// FR-138 (merge readiness), FR-139 (CI statuses), FR-140 (merge with merge tasks, the annotated tag, the <c>manual</c>
/// strategy and the merge poll) on Bitbucket, with the binding connection's token (FR-136) and within its budget (§4.19).
/// </summary>
internal sealed partial class BitbucketGitProvider
{
    /// <summary>FR-140: how often <c>merge/task-status</c> is read after a 202 before the attempt retries later.</summary>
    internal const int MergeTaskPolls = 10;

    /// <summary>FR-140: merge calls made in one attempt when Bitbucket answers 555 (its merge timeout) and the pull request is still open.</summary>
    internal const int MergeTimeoutAttempts = 3;

    private static readonly int[] MergeRefusals = [400, 403, 409];

    /// <summary>
    /// FR-138, §4.19: the single mergeability call (<c>GET …/pullrequests/{id}/mergeability/checks</c>), mapped by
    /// <see cref="MergeabilityChecks.Map"/>: ready means no check has <c>blocking: true</c>. The draft AIDE opened it as
    /// doesn't count until AIDE marked it ready (<see cref="MergeabilityChecks.IgnoringAideDraft"/>). A 404 is
    /// <c>pr_not_open</c>. An answer AIDE can't use is <c>UPSTREAM_ERROR</c>.
    /// </summary>
    public async Task<Result<HostMergeReadiness>> GetMergeReadinessAsync(
        GitConnectionSettings settings, int number, MergeStrategy strategy, CancellationToken cancellationToken, bool markedReady = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            return Result.Failure<HostMergeReadiness>(ToError(failure));
        }

        var read = await ApiAsync(settings, access, (c, a) => c.GetMergeabilityChecksAsync(a, settings.RepoFullName!, number, cancellationToken), cancellationToken);
        if (read.Failure is { } unreadable)
        {
            return Result.Failure<HostMergeReadiness>(ToError(unreadable));
        }

        if (read.Value is not { } checks)
        {
            return Result.Success(MergeabilityChecks.Map([], strategy, pullRequestOpen: false));
        }

        var readiness = MergeabilityChecks.Map(MergeabilityChecks.IgnoringAideDraft(checks, markedReady), strategy);
        var blocking = checks.Count(c => c.Blocking);
        LogReadiness(logger, settings.ProjectId, number, blocking, readiness.Verdict!.CanMerge);
        return Result.Success(readiness);
    }

    /// <summary>
    /// FR-139: the commit's statuses (<c>GET …/commit/{sha}/statuses</c>) as checks, required when the destination branch
    /// requires passing builds (<c>require_passing_builds_to_merge</c> in the version's pull request mergeability checks,
    /// read only when <paramref name="pullRequestNumber"/> is known; otherwise none is required).
    /// </summary>
    public async Task<Result<IReadOnlyList<CheckObservation>>> GetCommitStatusesAsync(
        GitConnectionSettings settings, string sha, CancellationToken cancellationToken, int? pullRequestNumber = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(sha);
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            return Result.Failure<IReadOnlyList<CheckObservation>>(ToError(failure));
        }

        var statuses = await ApiAsync(settings, access, (c, a) => c.ListCommitStatusesAsync(a, settings.RepoFullName!, sha, cancellationToken), cancellationToken);
        if (statuses.Failure is { } readFailure)
        {
            return Result.Failure<IReadOnlyList<CheckObservation>>(ToError(readFailure));
        }

        var required = false;
        if (pullRequestNumber is { } number && statuses.Value is { Count: > 0 })
        {
            var checks = await ApiAsync(settings, access, (c, a) => c.GetMergeabilityChecksAsync(a, settings.RepoFullName!, number, cancellationToken), cancellationToken);
            if (checks.Failure is { } checksFailure)
            {
                // Whether builds are required can't be told: fail like the statuses read (review goes on without them).
                return Result.Failure<IReadOnlyList<CheckObservation>>(ToError(checksFailure));
            }

            required = checks.Value is { } found && MergeabilityChecks.RequiresPassingBuilds(found);
        }

        return Result.Success(CommitStatusFindings.FromBitbucket(statuses.Value ?? [], required));
    }

    /// <summary>
    /// FR-65, §4.19: the pull request's state for the merge poll (at most once a minute: the next poll is never sooner). A 404
    /// first checks the connection still sees the repository (else <c>GIT_NOT_CONNECTED</c>).
    /// </summary>
    public async Task<Result<HostMergeObservation>> ObserveMergeRequestAsync(GitConnectionSettings settings, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            return Result.Failure<HostMergeObservation>(ToError(failure));
        }

        var read = await ApiAsync(settings, access, (c, a) => c.GetPullRequestAsync(a, settings.RepoFullName!, number, cancellationToken), cancellationToken);
        if (read.Failure is { } unreadable)
        {
            return Result.Failure<HostMergeObservation>(ToError(unreadable));
        }

        if (read.Value is null && await LostAccessAsync(settings, access, cancellationToken) is { } lost)
        {
            // FR-136: a 404 can mean the connection lost access, which isn't proof the pull request was closed.
            return Result.Failure<HostMergeObservation>(ToError(lost));
        }

        var observation = read.Value is { } pr ? BitbucketProjectRules.ObservationOf(pr.State) : MergePollObservation.ClosedUnmerged;
        var now = time.GetUtcNow();
        return Result.Success(new HostMergeObservation(observation, GitHostPollBudget.NextBitbucketPullRequestPollAt(now, now)));
    }

    /// <summary>FR-140: the annotated tag (<see cref="HostReleaseRequest.Tag"/> on <see cref="HostReleaseRequest.Commit"/>, the notes as its message); its web page.</summary>
    public async Task<Result<string?>> PublishReleaseAsync(GitConnectionSettings settings, HostReleaseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            return Result.Failure<string?>(ToError(failure));
        }

        var published = await PublishTagAsync(settings, access, request, cancellationToken);
        return published.Failure is { } publishFailure ? Result.Failure<string?>(ToError(publishFailure)) : Result.Success(published.Url);
    }

    /// <summary>
    /// FR-140, FR-65: releases the version's pull request <see cref="ReleaseRequest.PullRequestNumber"/>.
    /// <list type="number">
    /// <item>The release tag already on the merged commit is the release already made; on any other commit it is
    /// <c>TAG_EXISTS</c> before anything is merged.</item>
    /// <item>A merged pull request (by someone else, or AIDE's own merge before a retry: <see cref="ReleaseRequest.RecordedMergeCommitSha"/>)
    /// releases its <c>merge_commit.hash</c>; a declined one is <c>PR_CLOSED</c>.</item>
    /// <item>Otherwise the draft is marked ready (<c>draft: false</c>); under <c>manual</c> (or the merge poll) the release
    /// then awaits the merge.</item>
    /// <item>The pull request's head must be the fetched (reviewed) branch head, else the release retries with a fresh fetch.
    /// <c>POST …/merge {merge_strategy, close_source_branch: false, message}</c>: 200 merged; 202 followed through
    /// <c>merge/task-status</c>; 555 re-read and retried; 400 (not mergeable) awaits the merge with the readiness reasons (or
    /// lists the conflicts); 403 awaits it with <c>no_permission</c>; 409 (refs changed since review) fails the attempt for a
    /// new review.</item>
    /// <item>The annotated tag on the merged commit (its full hash), with the release notes as its message.</item>
    /// </list>
    /// </summary>
    public async Task<ReleaseOutcome> ReleaseAsync(GitConnectionSettings settings, ReleaseRequest request, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            return ToReleaseFailure(failure);
        }

        if (request.PullRequestNumber is not { } id)
        {
            return ReleaseOutcome.Failed(
                ErrorCodes.GitError,
                "No pull request is recorded for the project branch, so there is nothing to merge. Open it again from the project's Git section, then retry the release.",
                transient: false);
        }

        var repo = settings.RepoFullName!;
        var read = await ApiAsync(settings, access, (c, a) => c.GetPullRequestAsync(a, repo, id, cancellationToken), cancellationToken);
        if (read.Failure is { } unreadable)
        {
            return ToReleaseFailure(unreadable);
        }

        if (read.Value is not { } pr)
        {
            if (await LostAccessAsync(settings, access, cancellationToken) is { } lost)
            {
                return ToReleaseFailure(lost);
            }

            return ReleaseOutcome.Failed(ErrorCodes.GitError, $"Pull request #{id} doesn't exist in {repo}. Open it again, then retry the release.", transient: false);
        }

        var tagged = await ApiAsync(settings, access, (c, a) => c.GetTagAsync(a, repo, request.Tag, cancellationToken), cancellationToken);
        if (tagged.Failure is { } tagFailure)
        {
            return ToReleaseFailure(tagFailure);
        }

        var mergedState = pr.State == "MERGED";
        var existing = tagged.Value is { } tag
            ? new ExistingReleaseTag(request.Tag, mergedState && BitbucketProjectRules.SameCommit(tag.Hash, pr.MergeCommit))
            : null;
        var decision = ReleaseRules.DecideTag(request.Tag, existing);
        if (decision.IsFailure)
        {
            return ReleaseOutcome.Failed(decision.Error!.Code, decision.Error.Message, transient: false);
        }

        if (decision.Value)
        {
            // The tag already holds the merged commit: the release was made.
            var tagCommit = tagged.Value!.Hash!;
            var tagUrl = BitbucketProjectRules.TagUrl(tagged.Value.WebUrl, settings.RepoUrl, request.Tag);
            return ReleaseOutcome.Released(tagCommit, request.Tag, alreadyTagged: true) with { ReleaseUrl = tagUrl };
        }

        string mergeSha;
        var mergedExternally = false;
        string? mergedHeadMismatch = null;
        if (mergedState)
        {
            if (pr.MergeCommit is not { } merged)
            {
                return ReleaseOutcome.Failed(ErrorCodes.GitError, $"Bitbucket reports pull request #{id} as merged but returned no merge commit.", transient: true);
            }

            mergeSha = merged;

            // A retry after AIDE's own merge (e.g. the tag call failed, a crash, a merge task still pending) finds it recorded
            // on the attempt, or at least the attempt's submitted merge.
            mergedExternally = !request.MergeSubmitted && !BitbucketProjectRules.SameCommit(request.RecordedMergeCommitSha, merged);
        }
        else
        {
            if (pr.State is "DECLINED" or "SUPERSEDED")
            {
                return ReleaseOutcome.Failed(
                    ErrorCodes.PrClosed, $"Pull request #{id} was declined without merging. Reopen it on Bitbucket, then retry the release.", transient: false);
            }

            // FR-65, FR-137: a draft can't be merged: mark it ready (both strategies).
            var stillDraft = false;
            if (pr.Draft)
            {
                var ready = await ApiAsync(settings, access, (c, a) => c.UpdatePullRequestAsync(a, repo, id, null, null, false, cancellationToken), cancellationToken);
                if (ready.Failure is { Transient: true } readyFailure)
                {
                    return ToReleaseFailure(readyFailure);
                }

                stillDraft = ready.Failure is not null;
                LogMarkedReady(logger, settings.ProjectId, id, !stillDraft);
            }

            if (!request.Merge)
            {
                var manual = ReleaseOutcome.Awaiting(
                    stillDraft
                        ? $"The merge strategy is manual: AIDE never merges. AIDE couldn't mark pull request #{id} ready; mark it ready and merge it on Bitbucket."
                        : $"The merge strategy is manual: AIDE never merges. Pull request #{id} is ready; merge it on Bitbucket.",
                    refused: false);
                return manual with { StillDraft = stillDraft };
            }

            if (stillDraft)
            {
                var draft = ReleaseOutcome.Awaiting($"AIDE couldn't mark pull request #{id} ready, so Bitbucket won't merge it.", refused: true);
                return draft with { StillDraft = true, RefusedReason = MergeReadinessReasonCode.Draft };
            }

            var head = await BranchHeadAsync(settings.ProjectId, request.Branch, cancellationToken);
            if (head is null)
            {
                return ReleaseOutcome.Failed(ErrorCodes.GitError, $"The project branch '{request.Branch}' isn't in the working copy; the release retries with a fresh fetch.", transient: true);
            }

            // §4.21: with pre-merge production migrations, a pull request head Bitbucket doesn't report can't be checked.
            if (pr.SourceCommit is null && request.ExpectedHeadSha is { Length: > 0 })
            {
                return ReleaseOutcome.Failed(
                    ErrorCodes.GitError,
                    $"Bitbucket didn't report the head of pull request #{id}, so AIDE can't confirm it is the commit the production migrations ran on; the release retries.",
                    transient: true);
            }

            // The merge is of the head AIDE fetched (and reviewed): a different pull request head means the branch moved.
            if (pr.SourceCommit is { } prHead && !BitbucketProjectRules.SameCommit(prHead, head))
            {
                return ReleaseOutcome.Failed(
                    ErrorCodes.GitError,
                    $"Pull request #{id} points at a different commit than the fetched project branch ({head[..Math.Min(12, head.Length)]}); the release retries with a fresh fetch.",
                    transient: true);
            }

            // §4.21: only the commit the production migrations ran on is merged.
            if (request.HeadMovedSinceMigrations(head) is { } moved)
            {
                return moved;
            }

            var merge = await MergeAsync(settings, access, request, id, head, cancellationToken);
            if (merge.Outcome is { } stopped)
            {
                return stopped;
            }

            mergeSha = merge.Sha!;

            // §4.21: Bitbucket's merge call can't pin the head; a different merged head is recorded (the production
            // rollout's post-merge run on the merge commit migrates whatever it added).
            if (request.ExpectedHeadSha is { Length: > 0 } expected && merge.Source is { } mergedHead && !BitbucketProjectRules.SameCommit(mergedHead, expected))
            {
                LogMergedHeadMismatch(logger, settings.ProjectId, id, mergedHead, expected);
                mergedHeadMismatch = mergedHead;
            }
        }

        // The merge is on record as soon as it is known (before resolving the full hash and tagging).
        if (request.OnMerged is { } onMerged)
        {
            await onMerged(mergeSha, cancellationToken);
        }

        // §4.19: the tag needs the full hash; Bitbucket's merge_commit.hash is often a short one.
        var full = await FullHashAsync(settings, access, mergeSha, cancellationToken);
        if (full.Failure is { } hashFailure)
        {
            return ToReleaseFailure(hashFailure);
        }

        if (!string.Equals(full.Sha, mergeSha, StringComparison.Ordinal) && request.OnMerged is { } recordFull)
        {
            await recordFull(full.Sha!, cancellationToken);
        }

        mergeSha = full.Sha!;

        var release = await PublishTagAsync(settings, access, HostRelease(request, mergeSha), cancellationToken);
        if (release.Failure is { } releaseFailure)
        {
            return ToReleaseFailure(releaseFailure);
        }

        return ReleaseOutcome.Released(mergeSha, request.Tag, alreadyTagged: false) with
        {
            MergedExternally = mergedExternally,
            ReleaseUrl = release.Url,
            MergedHeadMismatch = mergedHeadMismatch,
        };
    }

    /// <summary>
    /// FR-140: AIDE's merge of pull request <paramref name="id"/>: the merged commit, or the outcome that ends the attempt
    /// (awaiting the merge, the conflicts, a failure).
    /// </summary>
    private async Task<(string? Sha, string? Source, ReleaseOutcome? Outcome)> MergeAsync(
        GitConnectionSettings settings, Access access, ReleaseRequest request, int id, string head, CancellationToken cancellationToken)
    {
        var repo = settings.RepoFullName!;
        var strategy = BitbucketProjectRules.MergeStrategyParameter(request.Strategy);
        var message = ReleaseRules.MergeTitle(request.ProjectName, request.Tag);

        // FR-140: a merge may complete after this call (202, 555): the attempt records it first, so a rollback waits.
        if (request.OnMergeSubmitting is { } submitting)
        {
            await submitting(cancellationToken);
        }

        for (var attempt = 1; ; attempt++)
        {
            var merge = await ApiAsync(
                settings, access, (c, a) => c.MergePullRequestAsync(a, repo, id, strategy, message, cancellationToken), cancellationToken, conflictStatuses: MergeRefusals);
            if (merge.Conflict)
            {
                var refusal = BitbucketMergeResult.ClassifyMergeError(merge.ConflictStatus!.Value);
                LogMergeRefused(logger, settings.ProjectId, id, merge.ConflictStatus.Value);
                var reason = Clean($"{refusal.Message} {merge.ConflictReason}");
                return (null, null, refusal.Kind switch
                {
                    HostMergeFailureKind.AwaitingMerge when refusal.Reason is null =>
                        await ConflictAsync(settings, request, head, cancellationToken) ?? ReleaseOutcome.Awaiting(reason, refused: true),
                    HostMergeFailureKind.AwaitingMerge => ReleaseOutcome.Awaiting(reason, refused: true) with { RefusedReason = refusal.Reason },
                    _ => ReleaseOutcome.Failed(ErrorCodes.GitError, reason, transient: false),
                });
            }

            if (merge.Failure is { } mergeFailure)
            {
                if (merge.Status == BitbucketMergeResult.MergeTimeoutStatus)
                {
                    // 555: Bitbucket timed out merging; the merge may still have happened. Look before trying again.
                    LogMergeTimedOut(logger, settings.ProjectId, id, attempt);
                    var after = await ApiAsync(settings, access, (c, a) => c.GetPullRequestAsync(a, repo, id, cancellationToken), cancellationToken);
                    if (after.Value is { State: "MERGED", MergeCommit: { } done })
                    {
                        LogMerged(logger, settings.ProjectId, id, done);
                        return (done, after.Value.SourceCommit, null);
                    }

                    if (attempt < MergeTimeoutAttempts && after.Value is { State: "OPEN" })
                    {
                        continue;
                    }
                }

                return (null, null, ToReleaseFailure(mergeFailure));
            }

            var answer = merge.Value!;
            var mergedPr = answer.PullRequest;
            if (mergedPr is null)
            {
                var task = await FollowMergeTaskAsync(settings, access, id, answer.TaskPath, cancellationToken);
                if (task.Outcome is { } taskOutcome)
                {
                    return (null, null, taskOutcome);
                }

                mergedPr = task.PullRequest;
            }

            if (mergedPr?.MergeCommit is not { } sha)
            {
                return (null, null, ReleaseOutcome.Failed(ErrorCodes.GitError, $"Bitbucket merged pull request #{id} but returned no merge commit.", transient: true));
            }

            LogMerged(logger, settings.ProjectId, id, sha);
            return (sha, mergedPr.SourceCommit, null);
        }
    }

    /// <summary>
    /// FR-140: follows a 202 merge through <c>merge/task-status</c> (<see cref="MergeTaskPolls"/> reads,
    /// <see cref="BitbucketOptions.MergeTaskPollDelay"/> apart); without a usable <c>Location</c>, the pull request itself is read. Still
    /// pending afterwards: the attempt retries (a transient failure), and the retry finds the pull request merged.
    /// </summary>
    private async Task<(BitbucketPullRequest? PullRequest, ReleaseOutcome? Outcome)> FollowMergeTaskAsync(
        GitConnectionSettings settings, Access access, int id, string? taskPath, CancellationToken cancellationToken)
    {
        var repo = settings.RepoFullName!;
        for (var poll = 0; poll < MergeTaskPolls; poll++)
        {
            if (poll > 0 && options.CurrentValue.MergeTaskPollDelay > TimeSpan.Zero)
            {
                await Task.Delay(options.CurrentValue.MergeTaskPollDelay, time, cancellationToken);
            }

            if (taskPath is null)
            {
                var pr = await ApiAsync(settings, access, (c, a) => c.GetPullRequestAsync(a, repo, id, cancellationToken), cancellationToken);
                if (pr.Value is { State: "MERGED" } merged)
                {
                    return (merged, null);
                }

                continue;
            }

            var status = await ApiAsync(settings, access, (c, a) => c.GetMergeTaskAsync(a, taskPath, cancellationToken), cancellationToken);
            if (status.Failure is { Transient: false } taskFailure)
            {
                // The task can't be read (e.g. 404): the pull request says whether the merge happened.
                var after = await ApiAsync(settings, access, (c, a) => c.GetPullRequestAsync(a, repo, id, cancellationToken), cancellationToken);
                if (after.Value is { State: "MERGED" } done)
                {
                    return (done, null);
                }

                if (after.Value is { State: "OPEN" })
                {
                    taskPath = null;
                    continue;
                }

                return (null, ToReleaseFailure(after.Failure ?? taskFailure));
            }

            switch (status.Value?.State)
            {
                case BitbucketMergeTaskState.Succeeded:
                    if (status.Value.Result is { MergeCommit: not null } result)
                    {
                        return (result, null);
                    }

                    var reread = await ApiAsync(settings, access, (c, a) => c.GetPullRequestAsync(a, repo, id, cancellationToken), cancellationToken);
                    return (reread.Value, reread.Failure is { } rereadFailure ? ToReleaseFailure(rereadFailure) : null);
                case BitbucketMergeTaskState.Failed:
                    return (null, ReleaseOutcome.Failed(ErrorCodes.GitError, $"Bitbucket couldn't merge pull request #{id} (its merge task failed).", transient: false));
            }
        }

        LogMergeTaskPending(logger, settings.ProjectId, id);
        return (null, ReleaseOutcome.Failed(ErrorCodes.GitError, $"Bitbucket is still merging pull request #{id}; the release checks again shortly.", transient: true) with { MergePending = true });
    }

    /// <summary>The full hash of <paramref name="sha"/> (unchanged when it is one; else <c>GET …/commit/{sha}</c>).</summary>
    private async Task<(string? Sha, GitOutcome? Failure)> FullHashAsync(GitConnectionSettings settings, Access access, string sha, CancellationToken cancellationToken)
    {
        if (BitbucketProjectRules.IsFullHash(sha))
        {
            return (sha, null);
        }

        var resolved = await ApiAsync(settings, access, (c, a) => c.GetCommitHashAsync(a, settings.RepoFullName!, sha, cancellationToken), cancellationToken);
        if (resolved.Failure is { } failure)
        {
            return (null, failure);
        }

        return resolved.Value is { } full && BitbucketProjectRules.IsFullHash(full)
            ? (full, null)
            : (null, GitOutcome.Failed($"Bitbucket didn't return the full hash of the merged commit {sha}.", transient: true, ErrorCodes.GitError));
    }

    /// <summary>
    /// FR-140: the annotated tag on <see cref="HostReleaseRequest.Commit"/>: reused when it already holds that commit (a
    /// retry), <c>TAG_EXISTS</c> on another commit, else created (<c>POST …/refs/tags</c>, the notes as its message); a
    /// create that races another (400/409) looks again.
    /// </summary>
    private async Task<(string? Url, GitOutcome? Failure)> PublishTagAsync(GitConnectionSettings settings, Access access, HostReleaseRequest request, CancellationToken cancellationToken)
    {
        var repo = settings.RepoFullName!;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var found = await ApiAsync(settings, access, (c, a) => c.GetTagAsync(a, repo, request.Tag, cancellationToken), cancellationToken);
            if (found.Failure is { } findFailure)
            {
                return (null, findFailure);
            }

            if (found.Value is { } existing)
            {
                var decision = ReleaseRules.DecideTag(request.Tag, new ExistingReleaseTag(request.Tag, BitbucketProjectRules.SameCommit(existing.Hash, request.Commit)));
                if (decision.IsFailure)
                {
                    return (null, GitOutcome.Failed(decision.Error!.Message, transient: false, decision.Error.Code));
                }

                LogTagReused(logger, settings.ProjectId, request.Tag);
                return (BitbucketProjectRules.TagUrl(existing.WebUrl, settings.RepoUrl, request.Tag), null);
            }

            if (attempt > 0)
            {
                break;
            }

            var created = await ApiAsync(
                settings,
                access,
                (c, a) => c.CreateTagAsync(a, repo, request.Tag, request.Commit, request.Notes, cancellationToken),
                cancellationToken,
                conflictStatuses: [400, 409]);
            if (created.Failure is { } createFailure)
            {
                return (null, createFailure);
            }

            if (!created.Conflict)
            {
                LogTagCreated(logger, settings.ProjectId, request.Tag);
                return (BitbucketProjectRules.TagUrl(created.Value!.WebUrl, settings.RepoUrl, request.Tag), null);
            }

            LogTagRefused(logger, settings.ProjectId, request.Tag, created.ConflictStatus ?? 0);
            if (attempt == 0 && created.ConflictReason is { } why && !why.Contains("exist", StringComparison.OrdinalIgnoreCase))
            {
                return (null, GitOutcome.Failed(Clean($"Bitbucket refused to create the tag {request.Tag}. {why}"), transient: false, ErrorCodes.GitError));
            }
        }

        return (null, GitOutcome.Failed($"Bitbucket refused to create the tag {request.Tag}, and no tag with that name was found.", transient: false, ErrorCodes.GitError));
    }

    /// <summary>
    /// FR-136: after a pull request read 404, whether the binding connection lost access to the repository (404 or 403):
    /// <c>GIT_NOT_CONNECTED</c>; a failing read is returned as it is (transient retries); <c>null</c> when the repository is
    /// still readable (the pull request itself is gone).
    /// </summary>
    private async Task<GitOutcome?> LostAccessAsync(GitConnectionSettings settings, Access access, CancellationToken cancellationToken)
    {
        var (workspace, slug) = BitbucketProjectRules.SplitFullName(settings.RepoFullName!);
        var repo = await ApiAsync(settings, access, (c, a) => c.GetRepositoryAsync(a, workspace, slug, cancellationToken), cancellationToken, conflictStatuses: [403]);
        if (repo.Conflict || (repo.Failure is null && repo.Value is null))
        {
            return GitOutcome.Failed(
                $"The bound Bitbucket connection can no longer see {settings.RepoFullName}. An Owner with access can re-bind their own connection in Settings → Git.",
                transient: false,
                ErrorCodes.GitNotConnected);
        }

        return repo.Failure;
    }

    /// <summary>The tag's message is AIDE's release notes (the full notes stay on the AIDE release too).</summary>
    private static HostReleaseRequest HostRelease(ReleaseRequest request, string commit)
    {
        var tagMessage = ReleaseRules.TagMessage(request.ProjectName, request.Tag, request.Version);
        return new HostReleaseRequest(
            request.Tag,
            commit,
            ReleaseNotes.Name(string.IsNullOrEmpty(request.Version) ? request.Tag : request.Version),
            tagMessage,
            string.IsNullOrWhiteSpace(request.Notes) ? tagMessage : request.Notes);
    }

    /// <summary>The fetched project branch head (the remote-tracking ref, else the local branch).</summary>
    private async Task<string?> BranchHeadAsync(Guid projectId, string branch, CancellationToken cancellationToken)
    {
        try
        {
            return await workingCopies.ResolveCommitAsync(projectId, $"refs/remotes/{GitRemote.Name}/{branch}", cancellationToken)
                ?? await workingCopies.ResolveCommitAsync(projectId, $"refs/heads/{branch}", cancellationToken);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            LogGitFailed(logger, projectId, "resolve branch", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>FR-43: the conflicting files from a trial merge of the fetched branch head into the default branch; <c>null</c> when it merges cleanly.</summary>
    private async Task<ReleaseOutcome?> ConflictAsync(GitConnectionSettings settings, ReleaseRequest request, string head, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> files;
        try
        {
            files = await workingCopies.TrialMergeConflictsAsync(settings.ProjectId, ComparisonBaseRef(settings), head, cancellationToken);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            LogGitFailed(logger, settings.ProjectId, "trial merge", ex.GetType().Name);
            return null;
        }

        return files.Count == 0
            ? null
            : ReleaseOutcome.Conflicted(
                files,
                $"The pull request from {request.Branch} conflicts with {settings.DefaultBranch} in {files.Count} file(s). Resolve them on the project branch, then retry the release.");
    }

    private static ReleaseOutcome ToReleaseFailure(GitOutcome failure) =>
        ReleaseOutcome.Failed(failure.ErrorCode ?? ErrorCodes.GitError, failure.Reason ?? "Bitbucket failed.", failure.Transient);

    /// <summary>A failure for the request path: a cleared binding stays <c>GIT_NOT_CONNECTED</c>; anything else is Bitbucket failing.</summary>
    private static DomainError ToError(GitOutcome failure) =>
        failure.ErrorCode == ErrorCodes.GitNotConnected
            ? new DomainError(ErrorCodes.GitNotConnected, failure.Reason ?? "Bitbucket isn't connected.")
            : new DomainError(ErrorCodes.UpstreamError, $"Bitbucket failed or couldn't be reached. {failure.Reason}".Trim());

    [LoggerMessage(Level = LogLevel.Information, Message = "Merge readiness of Bitbucket project {ProjectId} PR #{Id}: {Blocking} blocking check(s), canMerge={CanMerge}")]
    private static partial void LogReadiness(ILogger logger, Guid projectId, int id, int blocking, bool canMerge);

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked pull request #{Id} of project {ProjectId} ready: {Marked}")]
    private static partial void LogMarkedReady(ILogger logger, Guid projectId, int id, bool marked);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bitbucket refused merging pull request #{Id} of project {ProjectId} with HTTP {Status}")]
    private static partial void LogMergeRefused(ILogger logger, Guid projectId, int id, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bitbucket timed out merging pull request #{Id} of project {ProjectId} (attempt {Attempt})")]
    private static partial void LogMergeTimedOut(ILogger logger, Guid projectId, int id, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bitbucket is still merging pull request #{Id} of project {ProjectId}; the release retries")]
    private static partial void LogMergeTaskPending(ILogger logger, Guid projectId, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Merged pull request #{Id} of project {ProjectId} as {Sha}")]
    private static partial void LogMerged(ILogger logger, Guid projectId, int id, string sha);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bitbucket merged pull request #{Id} of project {ProjectId} at head {MergedHead}, not the migrated head {ExpectedHead}; the post-merge production run migrates it")]
    private static partial void LogMergedHeadMismatch(ILogger logger, Guid projectId, int id, string mergedHead, string expectedHead);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the annotated tag {Tag} of Bitbucket project {ProjectId}")]
    private static partial void LogTagCreated(ILogger logger, Guid projectId, string tag);

    [LoggerMessage(Level = LogLevel.Information, Message = "The tag {Tag} of Bitbucket project {ProjectId} already holds the release; reusing it")]
    private static partial void LogTagReused(ILogger logger, Guid projectId, string tag);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bitbucket refused creating the tag {Tag} of project {ProjectId} with HTTP {Status}")]
    private static partial void LogTagRefused(ILogger logger, Guid projectId, string tag, int status);
}
