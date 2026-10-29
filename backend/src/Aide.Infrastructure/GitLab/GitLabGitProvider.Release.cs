using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Domain.Releases;
using Aide.Infrastructure.Git;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.GitLab;

/// <summary>
/// FR-138 (merge readiness), FR-139 (CI statuses), FR-140 (merge, GitLab Release, the <c>manual</c> strategy and the merge
/// poll) on GitLab, with the binding connection's token (FR-136).
/// </summary>
internal sealed partial class GitLabGitProvider
{
    /// <summary>FR-138: how often a transient <c>detailed_merge_status</c> is read again before answering "still checking".</summary>
    internal const int ReadinessRePolls = 2;

    /// <summary>FR-138: the pause between those reads.</summary>
    internal static readonly TimeSpan ReadinessRePollDelay = TimeSpan.FromSeconds(1);

    private static readonly int[] MergeRefusals = [403, 405, 406, 409, 422];

    /// <summary>
    /// FR-138: the merge request's <c>detailed_merge_status</c> (re-read while it is transient), the approvals, the target
    /// branch's protection, the connection's access level and the head's CI statuses, mapped by
    /// <see cref="GitLabMergeReadiness.Map"/>. The draft state AIDE opened it with doesn't count: AIDE marks it ready before
    /// merging, until it has (<see cref="GitLabMergeReadiness.IgnoringAideDraft"/>). A missing merge request is
    /// <c>pr_not_open</c>.
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

        var repoId = settings.ExternalRepoId!;
        GitLabMergeRequestDetails? mr = null;
        for (var read = 0; ; read++)
        {
            var current = await ApiAsync(settings, access, (c, t) => c.GetMergeRequestDetailsAsync(t, repoId, number, cancellationToken), cancellationToken);
            if (current.Failure is { } unreadable)
            {
                return Result.Failure<HostMergeReadiness>(ToError(unreadable));
            }

            mr = current.Value;
            if (mr is null || !GitLabMergeReadiness.IsTransient(mr.DetailedMergeStatus) || read >= ReadinessRePolls)
            {
                break;
            }

            await Task.Delay(ReadinessRePollDelay, time, cancellationToken);
        }

        if (mr is null)
        {
            return Result.Success(GitLabMergeReadiness.Map(new GitLabMergeFacts { Open = false }, strategy));
        }

        if (GitLabMergeReadiness.IsTransient(mr.DetailedMergeStatus))
        {
            return Result.Success(new HostMergeReadiness(false, GitLabMergeReadiness.StillChecking(strategy)) { HostStatus = mr.DetailedMergeStatus });
        }

        var project = await ApiAsync(settings, access, (c, t) => c.GetProjectAsync(t, repoId, cancellationToken), cancellationToken);
        var approvals = await ApiAsync(settings, access, (c, t) => c.GetApprovalsAsync(t, repoId, number, cancellationToken), cancellationToken);
        var protection = await ApiAsync(settings, access, (c, t) => c.GetProtectedBranchMergeLevelsAsync(t, repoId, settings.DefaultBranch, cancellationToken), cancellationToken);
        if ((project.Failure ?? approvals.Failure ?? protection.Failure) is { } partFailure)
        {
            return Result.Failure<HostMergeReadiness>(ToError(partFailure));
        }

        var mustSucceed = project.Value?.PipelinesMustSucceed ?? false;
        IReadOnlyList<CheckObservation> checks = [];
        if (mr.Sha is { } head)
        {
            var statuses = await ApiAsync(settings, access, (c, t) => c.ListCommitStatusesAsync(t, repoId, head, cancellationToken), cancellationToken);
            checks = statuses.Value is { } found ? CommitStatusFindings.FromGitLab(found, mustSucceed) : [];
        }

        var facts = new GitLabMergeFacts
        {
            Open = mr.State == "opened",
            DetailedMergeStatus = mr.DetailedMergeStatus,
            Approved = approvals.Value?.Approved,
            ApprovalsLeft = approvals.Value?.ApprovalsLeft,
            ProtectedBranchMergeAccessLevels = protection.Value,
            UserAccessLevel = project.Value is { } p ? GitLabProjectRules.EffectiveAccessLevel(p.ProjectAccess, p.GroupAccess) : null,
            HeadPipelineStatus = mr.HeadPipelineStatus,
            PipelinesMustSucceed = mustSucceed,
            HasConflicts = mr.HasConflicts,
            BlockingDiscussionsResolved = mr.BlockingDiscussionsResolved,
            Draft = mr.Draft || GitLabProjectRules.IsDraftTitle(mr.Title),
        };
        var mapped = GitLabMergeReadiness.Map(GitLabMergeReadiness.IgnoringAideDraft(facts, markedReady), strategy);
        var readiness = mapped.RePoll ? new HostMergeReadiness(false, GitLabMergeReadiness.StillChecking(strategy)) : mapped;
        LogReadiness(logger, settings.ProjectId, number, mr.DetailedMergeStatus ?? "unknown", readiness.Verdict!.CanMerge);
        return Result.Success(readiness with { Checks = checks, HostStatus = mr.DetailedMergeStatus });
    }

    /// <summary>
    /// FR-139: the head's commit statuses (pipeline jobs and external statuses) as checks, required when the project
    /// requires pipelines to succeed and the job may not fail (<see cref="CommitStatusFindings.FromGitLab"/>).
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

        var repoId = settings.ExternalRepoId!;
        var project = await ApiAsync(settings, access, (c, t) => c.GetProjectAsync(t, repoId, cancellationToken), cancellationToken);
        var statuses = await ApiAsync(settings, access, (c, t) => c.ListCommitStatusesAsync(t, repoId, sha, cancellationToken), cancellationToken);
        if ((project.Failure ?? statuses.Failure) is { } readFailure)
        {
            return Result.Failure<IReadOnlyList<CheckObservation>>(ToError(readFailure));
        }

        return Result.Success(CommitStatusFindings.FromGitLab(statuses.Value ?? [], project.Value?.PipelinesMustSucceed ?? false));
    }

    /// <summary>FR-65, §4.19: the merge request's state for the merge poll, with the rate-limit back-off for the next poll.</summary>
    public async Task<Result<HostMergeObservation>> ObserveMergeRequestAsync(GitConnectionSettings settings, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            return Result.Failure<HostMergeObservation>(ToError(failure));
        }

        var read = await ApiAsync(
            settings,
            access,
            async (c, t) => (Mr: await c.GetMergeRequestDetailsAsync(t, settings.ExternalRepoId!, number, cancellationToken), Headers: c.LastRateLimit),
            cancellationToken);
        if (read.Failure is { } unreadable)
        {
            return Result.Failure<HostMergeObservation>(ToError(unreadable));
        }

        var (mr, headers) = read.Value;
        if (mr is null && await LostAccessAsync(settings, access, cancellationToken) is { } lost)
        {
            // FR-136: a 404 can mean the connection lost access, which isn't proof the merge request was closed.
            return Result.Failure<HostMergeObservation>(ToError(lost));
        }

        var observation = mr is null ? MergePollObservation.ClosedUnmerged : GitLabMergeResult.ObservationOf(mr.State);
        return Result.Success(new HostMergeObservation(observation, GitHostPollBudget.GitLabMergePollNotBefore(headers, time.GetUtcNow())));
    }

    /// <summary>
    /// FR-140: the GitLab Release for <see cref="HostReleaseRequest.Tag"/>: reused when it exists, else created with
    /// <c>POST /projects/:id/releases</c> (the annotated tag on <see cref="HostReleaseRequest.Commit"/>, the notes as the
    /// description). A 403 needs the Maintainer role (or the protected tag's access); a 429 (the tag creation limit) waits
    /// for the reset.
    /// </summary>
    public async Task<Result<string?>> PublishReleaseAsync(GitConnectionSettings settings, HostReleaseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            return Result.Failure<string?>(ToError(failure));
        }

        var published = await PublishAsync(settings, access, request, cancellationToken);
        return published.Failure is { } publishFailure ? Result.Failure<string?>(ToError(publishFailure)) : Result.Success(published.Url);
    }

    /// <summary>
    /// FR-140, FR-65: releases the version's merge request <see cref="ReleaseRequest.PullRequestNumber"/> (the <c>iid</c>).
    /// <list type="number">
    /// <item>The release tag already on the merged commit is the release already made (its GitLab Release is created if
    /// missing); on any other commit it is <c>TAG_EXISTS</c> before anything is merged.</item>
    /// <item>A merged MR (by someone else) releases its merged commit; a closed one is <c>PR_CLOSED</c>.</item>
    /// <item>Otherwise the draft MR is marked ready; under <c>manual</c> (or the merge poll) the release then awaits the merge.</item>
    /// <item>The merge is pinned to the fetched branch head, which must be the MR's head (else the release retries with a
    /// fresh fetch): <c>PUT …/merge {sha, squash per the project's squash_option, should_remove_source_branch: false}</c>.
    /// 405 (not mergeable) awaits the merge (or lists the conflicts); 403 awaits it with <c>no_permission</c>; 409 (the
    /// head moved since review) and 422 fail the attempt.</item>
    /// <item>The GitLab Release with the annotated tag on the merged commit (<see cref="GitLabMergeResult.Sha"/>).</item>
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

        if (request.PullRequestNumber is not { } iid)
        {
            return ReleaseOutcome.Failed(
                ErrorCodes.GitError,
                "No merge request is recorded for the project branch, so there is nothing to merge. Open it again from the project's Git section, then retry the release.",
                transient: false);
        }

        var repoId = settings.ExternalRepoId!;
        var read = await ApiAsync(settings, access, (c, t) => c.GetMergeRequestDetailsAsync(t, repoId, iid, cancellationToken), cancellationToken);
        if (read.Failure is { } unreadable)
        {
            return ToReleaseFailure(unreadable);
        }

        if (read.Value is not { } mr)
        {
            if (await LostAccessAsync(settings, access, cancellationToken) is { } lost)
            {
                return ToReleaseFailure(lost);
            }

            return ReleaseOutcome.Failed(ErrorCodes.GitError, $"Merge request !{iid} doesn't exist in {settings.RepoFullName}. Open it again, then retry the release.", transient: false);
        }

        var tagged = await ApiAsync(settings, access, (c, t) => c.FindTagCommitAsync(t, repoId, request.Tag, cancellationToken), cancellationToken);
        if (tagged.Failure is { } tagFailure)
        {
            return ToReleaseFailure(tagFailure);
        }

        var merged = mr.State == "merged" ? GitLabMergeResult.Sha(mr.MergeCommitSha, mr.SquashCommitSha, mr.Sha) : null;
        var existing = tagged.Value is { } tagCommit ? new ExistingReleaseTag(request.Tag, merged is not null && tagCommit == merged) : null;
        var decision = ReleaseRules.DecideTag(request.Tag, existing);
        if (decision.IsFailure)
        {
            return ReleaseOutcome.Failed(decision.Error!.Code, decision.Error.Message, transient: false);
        }

        if (decision.Value)
        {
            // The tag already holds the merged commit: make sure its GitLab Release exists too.
            var reused = await PublishAsync(settings, access, HostRelease(request, tagged.Value!), cancellationToken);
            return reused.Failure is { } reuseFailure
                ? ToReleaseFailure(reuseFailure)
                : ReleaseOutcome.Released(tagged.Value!, request.Tag, alreadyTagged: true) with { ReleaseUrl = reused.Url };
        }

        string mergeSha;
        var mergedExternally = false;
        if (mr.State == "merged")
        {
            if (merged is null)
            {
                return ReleaseOutcome.Failed(ErrorCodes.GitError, $"GitLab reports merge request !{iid} as merged but returned no merged commit.", transient: true);
            }

            mergeSha = merged;

            // A retry after AIDE's own merge (e.g. the tag limit, a crash) finds it recorded on the attempt.
            mergedExternally = !string.Equals(request.RecordedMergeCommitSha, merged, StringComparison.Ordinal);
        }
        else
        {
            if (mr.State is "closed")
            {
                return ReleaseOutcome.Failed(
                    ErrorCodes.PrClosed, $"Merge request !{iid} was closed without merging. Reopen it on GitLab, then retry the release.", transient: false);
            }

            // FR-65, FR-137: a draft MR can't be merged: mark it ready (both strategies).
            var stillDraft = false;
            if (mr.Draft || GitLabProjectRules.IsDraftTitle(mr.Title))
            {
                var ready = await ApiAsync(settings, access, (c, t) => c.UpdateMergeRequestAsync(t, repoId, iid, GitLabProjectRules.ReadyTitle(mr.Title), null, cancellationToken), cancellationToken);
                if (ready.Failure is { Transient: true } readyFailure)
                {
                    return ToReleaseFailure(readyFailure);
                }

                stillDraft = ready.Failure is not null;
                LogMarkedReady(logger, settings.ProjectId, iid, !stillDraft);
            }

            if (!request.Merge)
            {
                var manual = ReleaseOutcome.Awaiting(
                    stillDraft
                        ? $"The merge strategy is manual: AIDE never merges. AIDE couldn't mark merge request !{iid} ready; mark it ready and merge it on GitLab."
                        : $"The merge strategy is manual: AIDE never merges. Merge request !{iid} is ready; merge it on GitLab.",
                    refused: false);
                return manual with { StillDraft = stillDraft };
            }

            if (stillDraft)
            {
                // GitLab won't merge a draft: the merge needs someone to mark it ready on GitLab.
                var draft = ReleaseOutcome.Awaiting($"AIDE couldn't mark merge request !{iid} ready, so GitLab won't merge it.", refused: true);
                return draft with { StillDraft = true, RefusedReason = MergeReadinessReasonCode.Draft };
            }

            var head = await BranchHeadAsync(settings.ProjectId, request.Branch, cancellationToken);
            if (head is null)
            {
                return ReleaseOutcome.Failed(ErrorCodes.GitError, $"The project branch '{request.Branch}' isn't in the working copy; the release retries with a fresh fetch.", transient: true);
            }

            if (mr.HasConflicts && await ConflictAsync(settings, request, head, cancellationToken) is { } conflicted)
            {
                return conflicted;
            }

            // The merge is pinned to the head AIDE fetched (and reviewed): a different MR head means the branch moved.
            if (!string.Equals(mr.Sha, head, StringComparison.Ordinal))
            {
                return ReleaseOutcome.Failed(
                    ErrorCodes.GitError,
                    $"Merge request !{iid} points at a different commit than the fetched project branch ({head[..Math.Min(12, head.Length)]}); the release retries with a fresh fetch.",
                    transient: true);
            }

            var project = await ApiAsync(settings, access, (c, t) => c.GetProjectAsync(t, repoId, cancellationToken), cancellationToken);
            if (project.Failure is { } projectFailure)
            {
                return ToReleaseFailure(projectFailure);
            }

            var squash = GitLabMergeResult.Squash(project.Value?.SquashOption);
            var merge = await ApiAsync(
                settings,
                access,
                (c, t) => c.MergeAsync(t, repoId, iid, head, squash, ReleaseRules.MergeTitle(request.ProjectName, request.Tag), cancellationToken),
                cancellationToken,
                conflictStatuses: MergeRefusals);
            if (merge.Conflict)
            {
                var refusal = GitLabMergeResult.ClassifyMergeError(merge.ConflictStatus!.Value);
                LogMergeRefused(logger, settings.ProjectId, iid, merge.ConflictStatus.Value);
                var reason = Clean($"{refusal.Message} {merge.ConflictReason}");
                return refusal.Kind switch
                {
                    HostMergeFailureKind.AwaitingMerge when refusal.Reason is null =>
                        await ConflictAsync(settings, request, head, cancellationToken) ?? ReleaseOutcome.Awaiting(reason, refused: true),
                    HostMergeFailureKind.AwaitingMerge => ReleaseOutcome.Awaiting(reason, refused: true) with { RefusedReason = refusal.Reason },
                    _ => ReleaseOutcome.Failed(ErrorCodes.GitError, reason, transient: false),
                };
            }

            if (merge.Failure is { } mergeFailure)
            {
                return ToReleaseFailure(mergeFailure);
            }

            if (GitLabMergeResult.Sha(merge.Value!.MergeCommitSha, merge.Value.SquashCommitSha, merge.Value.Sha ?? head) is not { } sha)
            {
                return ReleaseOutcome.Failed(ErrorCodes.GitError, $"GitLab merged merge request !{iid} but returned no merged commit.", transient: true);
            }

            mergeSha = sha;
            LogMerged(logger, settings.ProjectId, iid, sha);
        }

        if (request.OnMerged is { } onMerged)
        {
            await onMerged(mergeSha, cancellationToken);
        }

        var release = await PublishAsync(settings, access, HostRelease(request, mergeSha), cancellationToken);
        if (release.Failure is { } releaseFailure)
        {
            return ToReleaseFailure(releaseFailure);
        }

        return ReleaseOutcome.Released(mergeSha, request.Tag, alreadyTagged: false) with { MergedExternally = mergedExternally, ReleaseUrl = release.Url };
    }

    /// <summary>The GitLab Release (find, else create; a create that races another finds it again).</summary>
    private async Task<(string? Url, GitOutcome? Failure)> PublishAsync(GitConnectionSettings settings, Access access, HostReleaseRequest request, CancellationToken cancellationToken)
    {
        var repoId = settings.ExternalRepoId!;
        var found = await ApiAsync(settings, access, (c, t) => c.FindReleaseAsync(t, repoId, request.Tag, cancellationToken), cancellationToken);
        if (found.Failure is { } findFailure)
        {
            return (null, findFailure);
        }

        if (found.Value.Exists)
        {
            LogReleaseReused(logger, settings.ProjectId, request.Tag);
            return (GitLabMergeResult.ReleaseUrl(found.Value.SelfLink, settings.RepoUrl, request.Tag), null);
        }

        var created = await ApiAsync(
            settings,
            access,
            (c, t) => c.CreateReleaseAsync(t, repoId, new GitLabNewRelease(request.Tag, request.Commit, request.Name, request.Notes, request.TagMessage), cancellationToken),
            cancellationToken,
            conflictStatuses: [403, 409, 422]);
        if (created.Conflict && created.ConflictStatus == 409)
        {
            var raced = await ApiAsync(settings, access, (c, t) => c.FindReleaseAsync(t, repoId, request.Tag, cancellationToken), cancellationToken);
            if (raced.Failure is { } racedFailure)
            {
                // A transient failure retries the job (the release is found then); a permanent one is the failure.
                return (null, racedFailure);
            }

            if (raced.Value.Exists)
            {
                return (GitLabMergeResult.ReleaseUrl(raced.Value.SelfLink, settings.RepoUrl, request.Tag), null);
            }
        }

        if (created.Conflict)
        {
            var reason = created.ConflictStatus == 403
                ? $"GitLab refused to create the release {request.Tag}: the bound account needs the Maintainer role (or access to the protected tag). {created.ConflictReason}"
                : $"GitLab refused to create the release {request.Tag}. {created.ConflictReason}";
            return (null, GitOutcome.Failed(Clean(reason), transient: false, ErrorCodes.GitError));
        }

        if (created.Failure is { } createFailure)
        {
            return (null, createFailure);
        }

        LogReleasePublished(logger, settings.ProjectId, request.Tag);
        return (GitLabMergeResult.ReleaseUrl(created.Value, settings.RepoUrl, request.Tag), null);
    }

    /// <summary>
    /// FR-136: after a merge request read 404, whether the binding connection lost access to the project
    /// (<c>GET /projects/:id</c> 404 or 403): <c>GIT_NOT_CONNECTED</c>; a failing read is returned as it is (transient
    /// retries); <c>null</c> when the project is still readable (the merge request itself is gone).
    /// </summary>
    private async Task<GitOutcome?> LostAccessAsync(GitConnectionSettings settings, Access access, CancellationToken cancellationToken)
    {
        var project = await ApiAsync(settings, access, (c, t) => c.GetProjectAsync(t, settings.ExternalRepoId!, cancellationToken), cancellationToken, conflictStatuses: [403]);
        if (project.Conflict || (project.Failure is null && project.Value is null))
        {
            return GitOutcome.Failed(
                $"The bound GitLab connection can no longer see {settings.RepoFullName}. An Owner with access can re-bind their own connection in Settings → Git.",
                transient: false,
                ErrorCodes.GitNotConnected);
        }

        return project.Failure;
    }

    private static HostReleaseRequest HostRelease(ReleaseRequest request, string commit) =>
        new(
            request.Tag,
            commit,
            ReleaseNotes.Name(string.IsNullOrEmpty(request.Version) ? request.Tag : request.Version),
            ReleaseRules.TagMessage(request.ProjectName, request.Tag, request.Version),
            request.Notes ?? ReleaseRules.TagMessage(request.ProjectName, request.Tag, request.Version));

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
                $"The merge request from {request.Branch} conflicts with {settings.DefaultBranch} in {files.Count} file(s). Resolve them on the project branch, then retry the release.");
    }

    private static ReleaseOutcome ToReleaseFailure(GitOutcome failure) =>
        ReleaseOutcome.Failed(failure.ErrorCode ?? ErrorCodes.GitError, failure.Reason ?? "GitLab failed.", failure.Transient);

    /// <summary>A failure for the request path: a cleared binding stays <c>GIT_NOT_CONNECTED</c>; anything else is GitLab failing.</summary>
    private static DomainError ToError(GitOutcome failure) =>
        failure.ErrorCode == ErrorCodes.GitNotConnected
            ? new DomainError(ErrorCodes.GitNotConnected, failure.Reason ?? "GitLab isn't connected.")
            : new DomainError(ErrorCodes.UpstreamError, $"GitLab failed or couldn't be reached. {failure.Reason}".Trim());

    [LoggerMessage(Level = LogLevel.Information, Message = "Merge readiness of GitLab project {ProjectId} MR !{Iid}: {DetailedMergeStatus}, canMerge={CanMerge}")]
    private static partial void LogReadiness(ILogger logger, Guid projectId, int iid, string detailedMergeStatus, bool canMerge);

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked merge request !{Iid} of project {ProjectId} ready: {Marked}")]
    private static partial void LogMarkedReady(ILogger logger, Guid projectId, int iid, bool marked);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitLab refused merging merge request !{Iid} of project {ProjectId} with HTTP {Status}")]
    private static partial void LogMergeRefused(ILogger logger, Guid projectId, int iid, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Merged merge request !{Iid} of project {ProjectId} as {Sha}")]
    private static partial void LogMerged(ILogger logger, Guid projectId, int iid, string sha);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the GitLab Release {Tag} of project {ProjectId}")]
    private static partial void LogReleasePublished(ILogger logger, Guid projectId, string tag);

    [LoggerMessage(Level = LogLevel.Information, Message = "The GitLab Release {Tag} of project {ProjectId} already exists; reusing it")]
    private static partial void LogReleaseReused(ILogger logger, Guid projectId, string tag);
}
