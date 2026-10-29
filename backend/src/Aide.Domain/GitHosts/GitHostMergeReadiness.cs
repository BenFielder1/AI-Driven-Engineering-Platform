using Aide.Domain.Entities;
using Aide.Domain.Releases;

namespace Aide.Domain.GitHosts;

/// <summary>
/// FR-138: a merge readiness answer from GitLab or Bitbucket: re-poll (GitLab hasn't finished computing the merge status:
/// <see cref="Verdict"/> is <c>null</c>), or the verdict in the v2 shape (FR-84).
/// </summary>
public sealed record HostMergeReadiness(bool RePoll, MergeReadinessVerdict? Verdict)
{
    public static HostMergeReadiness Pending { get; } = new(true, null);

    /// <summary>FR-138, FR-139: the head's CI statuses as checks (shown with the readiness); empty when not read.</summary>
    public IReadOnlyList<CheckObservation> Checks { get; init; } = [];

    /// <summary>The host's own merge status verbatim (GitLab <c>detailed_merge_status</c>), when known.</summary>
    public string? HostStatus { get; init; }
}

/// <summary>
/// FR-138, §4.19: what GitLab says about a merge request (<c>GET …/merge_requests/:iid</c>, <c>…/approvals</c>,
/// <c>GET /projects/:id/protected_branches/:name</c>, <c>GET /projects/:id</c>). Unknown values are <c>null</c>.
/// </summary>
public sealed record GitLabMergeFacts
{
    /// <summary>The MR's <c>state</c> is <c>opened</c>.</summary>
    public bool Open { get; init; } = true;

    /// <summary>The MR's <c>detailed_merge_status</c> verbatim (e.g. <c>mergeable</c>, <c>not_approved</c>).</summary>
    public string? DetailedMergeStatus { get; init; }

    /// <summary>The approvals endpoint's <c>approved</c>; <c>null</c> when it wasn't read.</summary>
    public bool? Approved { get; init; }

    /// <summary>The approvals endpoint's <c>approvals_left</c> (Premium only); <c>null</c> when absent.</summary>
    public int? ApprovalsLeft { get; init; }

    /// <summary>
    /// The target branch's protected branch <c>merge_access_levels[].access_level</c> (0 no one, 30 Developer, 40
    /// Maintainer, 60 Admin); <c>null</c> when the branch isn't protected (Developers may merge).
    /// </summary>
    public IReadOnlyCollection<int>? ProtectedBranchMergeAccessLevels { get; init; }

    /// <summary>The connection user's access level on the project (30 Developer, 40 Maintainer, 50 Owner); <c>null</c> unknown.</summary>
    public int? UserAccessLevel { get; init; }

    /// <summary>The MR's <c>head_pipeline.status</c> (<c>running</c>, <c>success</c>, <c>failed</c>, …); <c>null</c> without one.</summary>
    public string? HeadPipelineStatus { get; init; }

    /// <summary>The project's <c>only_allow_merge_if_pipeline_succeeds</c> ("pipelines must succeed").</summary>
    public bool PipelinesMustSucceed { get; init; }

    public bool HasConflicts { get; init; }

    /// <summary>The MR's <c>blocking_discussions_resolved</c>.</summary>
    public bool BlockingDiscussionsResolved { get; init; } = true;

    /// <summary>The MR's <c>draft</c>.</summary>
    public bool Draft { get; init; }
}

/// <summary>
/// FR-138, §4.19: GitLab merge readiness, mapped onto the v2 reason codes (FR-84). The merge status is re-polled while it
/// is transient (<c>unchecked</c>, <c>checking</c>, <c>preparing</c>, <c>approvals_syncing</c>). Pure.
/// </summary>
public static class GitLabMergeReadiness
{
    /// <summary>GitLab's Developer access level (push, MR, tags: §4.19).</summary>
    public const int DeveloperAccess = 30;

    /// <summary>GitLab's Maintainer access level (merging into a protected default branch, releases: §4.19).</summary>
    public const int MaintainerAccess = 40;

    /// <summary>§4.19: the <c>detailed_merge_status</c> values that mean GitLab is still computing it.</summary>
    public static IReadOnlySet<string> TransientStatuses { get; } =
        new HashSet<string>(["unchecked", "checking", "preparing", "approvals_syncing"], StringComparer.Ordinal);

    public static bool IsTransient(string? detailedMergeStatus) =>
        detailedMergeStatus is not null && TransientStatuses.Contains(detailedMergeStatus);

    /// <summary>
    /// FR-138: whether AIDE may merge the MR now, and why not. A closed MR is <c>pr_not_open</c> alone; a transient merge
    /// status re-polls. Otherwise every reason the facts and the status show, in the v2 order: permission, conflicts,
    /// rebase, draft, review, discussions, failing then pending pipeline, and <c>branch_protected</c> for any other
    /// GitLab block (e.g. <c>merge_request_blocked</c>, <c>locked_paths</c>) when nothing else explains it. The
    /// <c>manual</c> strategy adds <c>manual_strategy</c> without changing <c>canMerge</c> (FR-65).
    /// </summary>
    public static HostMergeReadiness Map(GitLabMergeFacts facts, MergeStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var reasons = new ReasonList();
        if (!facts.Open || facts.DetailedMergeStatus == "not_open")
        {
            reasons.Add(MergeReadinessReasonCode.PrNotOpen, "There is no open merge request for this version (it isn't opened yet, or it was closed or merged).");
            return new HostMergeReadiness(false, reasons.Verdict(strategy, "GitLab"));
        }

        if (IsTransient(facts.DetailedMergeStatus))
        {
            return HostMergeReadiness.Pending;
        }

        var status = facts.DetailedMergeStatus;
        if (!MayMerge(facts.UserAccessLevel, facts.ProtectedBranchMergeAccessLevels))
        {
            reasons.Add(MergeReadinessReasonCode.NoPermission, "AIDE's GitLab connection may not merge into the target branch (it needs the Maintainer role, or the access the protected branch allows).");
        }

        if (facts.HasConflicts || status == "conflict")
        {
            reasons.Add(MergeReadinessReasonCode.MergeConflict, "The merge request conflicts with the target branch. Resolve the conflicts on the version branch.");
        }

        if (status == "need_rebase")
        {
            reasons.Add(MergeReadinessReasonCode.RebaseRequired, "The project's merge method needs the version branch rebased onto the target branch first.");
        }

        if (facts.Draft || status == "draft_status")
        {
            reasons.Add(MergeReadinessReasonCode.Draft, "The merge request is still a draft.");
        }

        if (status == "requested_changes")
        {
            reasons.Add(MergeReadinessReasonCode.ChangesRequested, "A reviewer requested changes.");
        }

        if (status == "not_approved" || facts.Approved == false)
        {
            reasons.Add(MergeReadinessReasonCode.ReviewRequired, facts.ApprovalsLeft is > 0 and var left
                ? $"{left.ToString(System.Globalization.CultureInfo.InvariantCulture)} more approval{(left == 1 ? " is" : "s are")} required."
                : "An approval is required.");
        }

        if (!facts.BlockingDiscussionsResolved || status == "discussions_not_resolved")
        {
            reasons.Add(MergeReadinessReasonCode.DiscussionsUnresolved, "Blocking discussions on the merge request aren't resolved.");
        }

        var pipeline = PipelineStateOf(facts.HeadPipelineStatus);
        var pipelineBlocks = facts.PipelinesMustSucceed || status is "ci_must_pass" or "ci_still_running";
        if (pipelineBlocks && pipeline == PipelineState.Failed)
        {
            reasons.Add(MergeReadinessReasonCode.ChecksFailing, "The head pipeline failed, and the project requires pipelines to succeed.");
        }
        else if (status == "ci_still_running" || (pipelineBlocks && pipeline == PipelineState.Pending))
        {
            reasons.Add(MergeReadinessReasonCode.ChecksPending, "The head pipeline hasn't finished.");
        }
        else if (status == "ci_must_pass")
        {
            reasons.Add(
                pipeline == PipelineState.None ? MergeReadinessReasonCode.ChecksPending : MergeReadinessReasonCode.ChecksFailing,
                pipeline == PipelineState.None ? "The project requires a successful pipeline, and none has run on the head yet." : "The project requires the head pipeline to succeed.");
        }

        if (status == "status_checks_must_pass")
        {
            reasons.Add(MergeReadinessReasonCode.ChecksFailing, "External status checks must pass before merging.");
        }

        if (reasons.Count == 0 && status is not (null or "mergeable"))
        {
            reasons.Add(MergeReadinessReasonCode.BranchProtected, $"GitLab blocks the merge ({status}).");
        }

        return new HostMergeReadiness(false, reasons.Verdict(strategy, "GitLab"));
    }

    /// <summary>
    /// FR-138, FR-65, FR-137: AIDE opens the merge request as a draft and marks it ready itself before merging (under both
    /// strategies), so until it has (<paramref name="markedReady"/> false) the draft doesn't block AIDE (as v2 does for
    /// GitHub drafts): the facts without <see cref="GitLabMergeFacts.Draft"/>, and without GitLab's <c>draft_status</c>
    /// (which hides the other merge checks; the approvals endpoint, conflicts, discussions and the head pipeline still say
    /// what blocks). Once AIDE marked it ready, a draft (made again by someone, or never cleared) blocks: facts unchanged.
    /// </summary>
    public static GitLabMergeFacts IgnoringAideDraft(GitLabMergeFacts facts, bool markedReady = false)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (markedReady)
        {
            return facts;
        }

        return facts with
        {
            Draft = false,
            DetailedMergeStatus = facts.DetailedMergeStatus == "draft_status" ? null : facts.DetailedMergeStatus,
        };
    }

    /// <summary>
    /// FR-138: the verdict while GitLab is still computing the merge status after the re-polls
    /// (<see cref="TransientStatuses"/>): not mergeable yet, with <c>checks_pending</c>.
    /// </summary>
    public static MergeReadinessVerdict StillChecking(MergeStrategy strategy)
    {
        var reasons = new ReasonList();
        reasons.Add(MergeReadinessReasonCode.ChecksPending, "GitLab is still checking whether the merge request can be merged. Refresh in a moment.");
        return reasons.Verdict(strategy, "GitLab");
    }

    /// <summary>
    /// §4.19: whether a user with <paramref name="userAccessLevel"/> may merge into the target branch: an unprotected
    /// branch needs Developer; a protected one needs at least one of its merge access levels (0 = no one). Unknown access is
    /// assumed allowed (GitLab's own status and the merge call stay authoritative).
    /// </summary>
    public static bool MayMerge(int? userAccessLevel, IReadOnlyCollection<int>? protectedBranchMergeAccessLevels)
    {
        if (userAccessLevel is not { } level)
        {
            return true;
        }

        if (protectedBranchMergeAccessLevels is null)
        {
            return level >= DeveloperAccess;
        }

        return protectedBranchMergeAccessLevels.Any(allowed => allowed > 0 && level >= allowed);
    }

    /// <summary>
    /// FR-139: a GitLab pipeline or job status as a check: <c>success</c>; <c>failed</c> and <c>canceled</c> fail;
    /// <c>skipped</c> and <c>manual</c> (a job nobody has run, e.g. <c>deploy:prod</c>) are neutral, neither failing nor
    /// pending; <c>created</c>, <c>waiting_for_resource</c>, <c>preparing</c>, <c>pending</c> and <c>scheduled</c> are queued;
    /// everything else (<c>running</c>, <c>canceling</c>) is still running.
    /// </summary>
    public static (CheckRunStatus Status, CheckRunConclusion? Conclusion) StatusOf(string? status) => status switch
    {
        "success" => (CheckRunStatus.Completed, CheckRunConclusion.Success),
        "failed" => (CheckRunStatus.Completed, CheckRunConclusion.Failure),
        "canceled" or "cancelled" => (CheckRunStatus.Completed, CheckRunConclusion.Cancelled),
        "skipped" => (CheckRunStatus.Completed, CheckRunConclusion.Skipped),
        "manual" => (CheckRunStatus.Completed, CheckRunConclusion.Neutral),
        "created" or "waiting_for_resource" or "preparing" or "pending" or "scheduled" => (CheckRunStatus.Queued, null),
        _ => (CheckRunStatus.InProgress, null),
    };

    private enum PipelineState
    {
        None,
        Pending,
        Passed,
        Failed,
    }

    private static PipelineState PipelineStateOf(string? status)
    {
        if (status is null)
        {
            return PipelineState.None;
        }

        var check = new CheckObservation("pipeline", StatusOf(status).Status, StatusOf(status).Conclusion, true, null);
        return MergeReadinessRules.IsFailing(check) ? PipelineState.Failed
            : MergeReadinessRules.IsPending(check) ? PipelineState.Pending
            : PipelineState.Passed;
    }
}

/// <summary>FR-138, §4.19: one Bitbucket mergeability check (<c>GET …/pullrequests/{id}/mergeability/checks</c>).</summary>
/// <param name="Type">e.g. <c>standard_merge_check</c>.</param>
/// <param name="Key">The check's key or name (e.g. <c>require_passing_builds_to_merge</c>, <c>require_approvals_to_merge</c>).</param>
/// <param name="Status"><c>PASSED</c>, <c>FAILED</c>, <c>PENDING</c>, <c>SKIPPED</c> or <c>UNKNOWN</c>.</param>
/// <param name="Required">The check is configured as required on the destination branch.</param>
/// <param name="Blocking">The check blocks the merge now.</param>
public sealed record MergeabilityCheck(string Type, string? Key, string Status, bool Required, bool Blocking);

/// <summary>
/// FR-138, §4.19: Bitbucket merge readiness from the single mergeability call: ready means no check has
/// <c>blocking: true</c>. Blocking checks map to the v2 reason codes by their key: builds (<c>FAILED</c> →
/// <c>checks_failing</c>, <c>PENDING</c>/<c>UNKNOWN</c> → <c>checks_pending</c>), approvals and default reviewers →
/// <c>review_required</c>, changes requested, tasks → <c>discussions_unresolved</c>, conflicts, draft, write access →
/// <c>no_permission</c>; anything else → <c>branch_protected</c>. Pure.
/// </summary>
public static class MergeabilityChecks
{
    public static HostMergeReadiness Map(IEnumerable<MergeabilityCheck> checks, MergeStrategy strategy, bool pullRequestOpen = true, bool draft = false)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var reasons = new ReasonList();
        if (!pullRequestOpen)
        {
            reasons.Add(MergeReadinessReasonCode.PrNotOpen, "There is no open pull request for this version (it isn't opened yet, or it was closed or merged).");
            return new HostMergeReadiness(false, reasons.Verdict(strategy, "Bitbucket"));
        }

        if (draft)
        {
            reasons.Add(MergeReadinessReasonCode.Draft, "The pull request is still a draft.");
        }

        foreach (var check in checks.Where(c => c.Blocking))
        {
            var (code, message) = ReasonOf(check);
            reasons.Add(code, message);
        }

        return new HostMergeReadiness(false, reasons.Verdict(strategy, "Bitbucket"));
    }

    /// <summary>
    /// FR-139: whether the destination branch requires passing builds (<c>require_passing_builds_to_merge</c> seen in the
    /// mergeability checks, required or blocking).
    /// </summary>
    public static bool RequiresPassingBuilds(IEnumerable<MergeabilityCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return checks.Any(c => (c.Required || c.Blocking) && IsBuildCheck(c));
    }

    private static bool IsBuildCheck(MergeabilityCheck check) => Has(check, "build");

    private static (MergeReadinessReasonCode Code, string Message) ReasonOf(MergeabilityCheck check)
    {
        var name = check.Key ?? check.Type;
        if (IsBuildCheck(check))
        {
            return check.Status is "PENDING" or "UNKNOWN"
                ? (MergeReadinessReasonCode.ChecksPending, "Required builds haven't finished.")
                : (MergeReadinessReasonCode.ChecksFailing, "Required builds failed.");
        }

        if (Has(check, "changes_requested"))
        {
            return (MergeReadinessReasonCode.ChangesRequested, "A reviewer requested changes.");
        }

        if (Has(check, "approval") || Has(check, "reviewer"))
        {
            return (MergeReadinessReasonCode.ReviewRequired, "Required approvals are missing.");
        }

        if (Has(check, "task"))
        {
            return (MergeReadinessReasonCode.DiscussionsUnresolved, "Open tasks on the pull request aren't resolved.");
        }

        if (Has(check, "conflict"))
        {
            return (MergeReadinessReasonCode.MergeConflict, "The pull request conflicts with the destination branch. Resolve the conflicts on the version branch.");
        }

        if (Has(check, "draft"))
        {
            return (MergeReadinessReasonCode.Draft, "The pull request is still a draft.");
        }

        if (Has(check, "permission") || Has(check, "write_access") || Has(check, "merge_access"))
        {
            return (MergeReadinessReasonCode.NoPermission, "AIDE's Bitbucket connection may not merge into the destination branch.");
        }

        return (MergeReadinessReasonCode.BranchProtected, $"A Bitbucket merge check blocks the merge ({name}).");
    }

    private static bool Has(MergeabilityCheck check, string part) =>
        (check.Key ?? string.Empty).Contains(part, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reasons de-duplicated by code (first message wins), then the v2 verdict (FR-84, FR-65).</summary>
internal sealed class ReasonList
{
    private readonly List<MergeReadinessReason> _reasons = [];

    public int Count => _reasons.Count;

    public void Add(MergeReadinessReasonCode code, string message)
    {
        if (_reasons.All(r => r.Code != code))
        {
            _reasons.Add(new MergeReadinessReason(code, message));
        }
    }

    public MergeReadinessVerdict Verdict(MergeStrategy strategy, string host)
    {
        var canMerge = _reasons.Count == 0;
        if (strategy == MergeStrategy.Manual)
        {
            _reasons.Add(new(MergeReadinessReasonCode.ManualStrategy, $"The project's merge strategy is manual: AIDE never merges; merge it on {host}."));
        }

        return new MergeReadinessVerdict(canMerge, MergeReadinessRules.WillAutoMerge(strategy, canMerge), [.. _reasons]);
    }
}
