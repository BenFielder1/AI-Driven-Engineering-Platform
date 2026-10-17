using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;

namespace Aide.Domain.Releases;

/// <summary>
/// FR-84: why AIDE can't merge the version's PR itself (wire values <c>review_required</c>, <c>changes_requested</c>,
/// <c>checks_failing</c>, <c>checks_pending</c>, <c>branch_protected</c>, <c>merge_conflict</c>, <c>no_permission</c>,
/// <c>merge_method_not_allowed</c>, <c>pr_not_open</c>, <c>manual_strategy</c>). Also recorded on an <c>awaiting_merge</c>
/// release attempt (FR-65).
/// </summary>
public enum MergeReadinessReasonCode
{
    /// <summary>Branch rules require approving reviews that the PR doesn't have yet.</summary>
    ReviewRequired = 0,

    /// <summary>A reviewer requested changes.</summary>
    ChangesRequested = 1,

    /// <summary>A required check failed.</summary>
    ChecksFailing = 2,

    /// <summary>A required check hasn't completed yet.</summary>
    ChecksPending = 3,

    /// <summary>Another branch rule (e.g. up to date with the base, signed commits, merge queue) blocks the merge.</summary>
    BranchProtected = 4,

    /// <summary>The PR conflicts with the default branch.</summary>
    MergeConflict = 5,

    /// <summary>AIDE's token may not merge into the repository.</summary>
    NoPermission = 6,

    /// <summary>The repository allows none of the merge methods AIDE uses.</summary>
    MergeMethodNotAllowed = 7,

    /// <summary>There is no open PR for the version (not opened yet, closed or already merged).</summary>
    PrNotOpen = 8,

    /// <summary>The project's merge strategy is <c>manual</c>: AIDE never merges (FR-65).</summary>
    ManualStrategy = 9,
}

/// <summary>FR-84, FR-85: a GitHub check run's (or commit status's) state. Wire values <c>queued</c>, <c>in_progress</c>, <c>completed</c>.</summary>
public enum CheckRunStatus
{
    Queued = 0,
    InProgress = 1,
    Completed = 2,
}

/// <summary>
/// FR-84, FR-85: a completed check's outcome. Wire values <c>success</c>, <c>failure</c>, <c>neutral</c>, <c>cancelled</c>,
/// <c>skipped</c>, <c>timed_out</c>, <c>action_required</c>. Commit statuses map <c>success</c>/<c>failure</c>/<c>error</c>
/// (→ <c>failure</c>); GitHub's <c>stale</c> and <c>startup_failure</c> map to <c>failure</c>.
/// </summary>
public enum CheckRunConclusion
{
    Success = 0,
    Failure = 1,
    Neutral = 2,
    Cancelled = 3,
    Skipped = 4,
    TimedOut = 5,
    ActionRequired = 6,
}

/// <summary>FR-84: when merge readiness may be checked, and what the Release page tells the user. Pure.</summary>
public static class MergeReadinessRules
{
    /// <summary>
    /// FR-84: readiness is checked when the pre-release gate is created (the project is still in <c>review</c>, with the
    /// version's PR open) and on demand in <c>release</c>. Any other phase is <c>WRONG_PHASE</c>.
    /// </summary>
    public static Result CanCheck(Phase phase) =>
        phase is Phase.Review or Phase.Release
            ? Result.Success()
            : Result.Failure(new DomainError(
                ErrorCodes.WrongPhase,
                $"Merge readiness is checked in the review and release phases, not in {phase.ToWire()}."));

    /// <summary>FR-65, FR-84: "AIDE will merge automatically" needs the <c>aide_merges</c> strategy and a mergeable PR.</summary>
    public static bool WillAutoMerge(MergeStrategy strategy, bool canMerge) =>
        strategy == MergeStrategy.AideMerges && canMerge;

    /// <summary>A completed check whose conclusion blocks a required check: failure, timed out, cancelled or action required.</summary>
    public static bool IsFailing(CheckObservation check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.Status == CheckRunStatus.Completed
            && check.Conclusion is CheckRunConclusion.Failure or CheckRunConclusion.TimedOut or CheckRunConclusion.Cancelled or CheckRunConclusion.ActionRequired;
    }

    /// <summary>A check that hasn't completed yet.</summary>
    public static bool IsPending(CheckObservation check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.Status != CheckRunStatus.Completed;
    }

    /// <summary>
    /// FR-84, FR-85: the checks with <c>required</c> set from the branch rules' required status check contexts (exact
    /// names). Checks with the same name keep the first (GitHub lists the latest run first). With
    /// <paramref name="includeMissingRequired"/>, every required context that hasn't reported on the PR head yet is added as
    /// a queued placeholder (GitHub's "Expected — waiting for status").
    /// </summary>
    public static IReadOnlyList<CheckObservation> MarkRequired(
        IEnumerable<CheckObservation> checks, IEnumerable<string> requiredContexts, bool includeMissingRequired = false)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(requiredContexts);
        var required = requiredContexts.Where(c => !string.IsNullOrWhiteSpace(c)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CheckObservation>();
        foreach (var check in checks)
        {
            if (seen.Add(check.Name))
            {
                result.Add(check with { Required = required.Contains(check.Name) });
            }
        }

        if (includeMissingRequired)
        {
            result.AddRange(required.Where(r => !seen.Contains(r)).Order(StringComparer.Ordinal)
                .Select(r => new CheckObservation(r, CheckRunStatus.Queued, null, true, null)));
        }

        return result;
    }

    /// <summary>
    /// FR-84: whether AIDE's token may merge the version's PR now, and why not. Order: no open PR (alone); missing permission;
    /// merge method not allowed; conflicts; changes requested / review required (from <c>reviewDecision</c>, or — when
    /// GraphQL was unavailable — conservatively from the branch rules' required approvals); failing then pending required
    /// checks; and <c>branch_protected</c> when GitHub still reports the PR <c>BLOCKED</c> or <c>BEHIND</c> for another
    /// rule. <c>canMerge</c> is true without reasons. The <c>manual</c> strategy adds <c>manual_strategy</c> without
    /// changing <c>canMerge</c> (FR-65). The merge endpoint's own answer stays authoritative.
    /// </summary>
    public static MergeReadinessVerdict Evaluate(MergeReadinessFacts facts, MergeStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var reasons = new List<MergeReadinessReason>();
        if (!facts.PullRequestOpen)
        {
            reasons.Add(new(MergeReadinessReasonCode.PrNotOpen, "There is no open pull request for this version (it isn't opened yet, or it was closed or merged)."));
            return Verdict(reasons, strategy);
        }

        if (facts.CanWrite == false)
        {
            reasons.Add(new(MergeReadinessReasonCode.NoPermission, "AIDE's token may not merge into this repository (it needs Contents and Pull requests: read and write)."));
        }

        if (facts.AllowedMergeMethods is { } allowed && !allowed.Contains(facts.MergeMethod, StringComparer.Ordinal))
        {
            reasons.Add(new(
                MergeReadinessReasonCode.MergeMethodNotAllowed,
                $"The repository doesn't allow {facts.MergeMethod} merges, which AIDE uses (allowed: {(allowed.Count == 0 ? "none" : string.Join(", ", allowed.Order(StringComparer.Ordinal)))})."));
        }

        if (facts.HasConflicts || string.Equals(facts.MergeStateStatus, "DIRTY", StringComparison.Ordinal))
        {
            reasons.Add(new(MergeReadinessReasonCode.MergeConflict, "The pull request conflicts with the default branch. Resolve the conflicts on the version branch."));
        }

        var approvals = facts.RequiredApprovingReviews > 0
            ? $"{facts.RequiredApprovingReviews.ToString(System.Globalization.CultureInfo.InvariantCulture)} approving review{(facts.RequiredApprovingReviews == 1 ? " is" : "s are")} required."
            : "An approving review is required.";
        switch (facts.ReviewDecision)
        {
            case "CHANGES_REQUESTED":
                reasons.Add(new(MergeReadinessReasonCode.ChangesRequested, "A reviewer requested changes."));
                break;
            case "REVIEW_REQUIRED":
                reasons.Add(new(MergeReadinessReasonCode.ReviewRequired, approvals));
                break;
            case null when !facts.GraphQlAvailable && facts.RequiredApprovingReviews > 0:
                reasons.Add(new(MergeReadinessReasonCode.ReviewRequired, $"{approvals} GitHub couldn't tell whether the pull request has them yet."));
                break;
            default:
                break;
        }

        var failing = facts.Checks.Where(c => c.Required && IsFailing(c)).Select(c => c.Name).ToList();
        if (failing.Count > 0)
        {
            reasons.Add(new(MergeReadinessReasonCode.ChecksFailing, $"Required check{(failing.Count == 1 ? string.Empty : "s")} failed: {string.Join(", ", failing)}."));
        }

        var pending = facts.Checks.Where(c => c.Required && IsPending(c)).Select(c => c.Name).ToList();
        if (pending.Count > 0)
        {
            reasons.Add(new(MergeReadinessReasonCode.ChecksPending, $"Required check{(pending.Count == 1 ? " hasn't" : "s haven't")} finished: {string.Join(", ", pending)}."));
        }

        if (reasons.Count == 0 && facts.MergeStateStatus is "BLOCKED" or "BEHIND")
        {
            reasons.Add(new(
                MergeReadinessReasonCode.BranchProtected,
                facts.MergeStateStatus == "BEHIND"
                    ? "The branch rules require the version branch to be up to date with the default branch."
                    : "A branch rule blocks the merge (for example signed commits, a merge queue or required deployments)."));
        }

        return Verdict(reasons, strategy);
    }

    private static MergeReadinessVerdict Verdict(List<MergeReadinessReason> reasons, MergeStrategy strategy)
    {
        var canMerge = reasons.Count == 0;
        if (strategy == MergeStrategy.Manual)
        {
            reasons.Add(new(MergeReadinessReasonCode.ManualStrategy, "The project's merge strategy is manual: AIDE never merges; merge the pull request on GitHub."));
        }

        return new MergeReadinessVerdict(canMerge, WillAutoMerge(strategy, canMerge), reasons);
    }

    /// <summary>A GitHub check run's <c>status</c> (<c>queued</c>, <c>in_progress</c>, <c>completed</c>; anything else is queued).</summary>
    public static CheckRunStatus CheckStatusOf(string? status) => status switch
    {
        "completed" => CheckRunStatus.Completed,
        "in_progress" => CheckRunStatus.InProgress,
        _ => CheckRunStatus.Queued,
    };

    /// <summary>A GitHub check run's <c>conclusion</c> (<c>stale</c> and <c>startup_failure</c> count as failure); <c>null</c> when unknown.</summary>
    public static CheckRunConclusion? CheckConclusionOf(string? conclusion) => conclusion switch
    {
        "success" => CheckRunConclusion.Success,
        "failure" or "stale" or "startup_failure" => CheckRunConclusion.Failure,
        "neutral" => CheckRunConclusion.Neutral,
        "cancelled" => CheckRunConclusion.Cancelled,
        "skipped" => CheckRunConclusion.Skipped,
        "timed_out" => CheckRunConclusion.TimedOut,
        "action_required" => CheckRunConclusion.ActionRequired,
        _ => null,
    };

    /// <summary>A commit status (<c>success</c>, <c>failure</c>, <c>error</c>, <c>pending</c>) as a check.</summary>
    public static (CheckRunStatus Status, CheckRunConclusion? Conclusion) CommitStatusOf(string? state) => state switch
    {
        "success" => (CheckRunStatus.Completed, CheckRunConclusion.Success),
        "failure" or "error" => (CheckRunStatus.Completed, CheckRunConclusion.Failure),
        _ => (CheckRunStatus.InProgress, null),
    };
}

/// <summary>FR-84, FR-85: a check run or commit status on the PR head; <c>required</c> when the default branch's rules require it.</summary>
public sealed record CheckObservation(string Name, CheckRunStatus Status, CheckRunConclusion? Conclusion, bool Required, string? DetailsUrl);

/// <summary>FR-84: one reason AIDE can't merge the version's PR itself.</summary>
public sealed record MergeReadinessReason(MergeReadinessReasonCode Code, string Message);

/// <summary>FR-84: the verdict: whether AIDE may merge, whether it will (strategy), and the reasons.</summary>
public sealed record MergeReadinessVerdict(bool CanMerge, bool WillAutoMerge, IReadOnlyList<MergeReadinessReason> Reasons);

/// <summary>
/// FR-84: what merge readiness read from GitHub (or the Local repository). <see cref="CanWrite"/> is <c>null</c> when
/// unknown; <see cref="AllowedMergeMethods"/> <c>null</c> when unknown; <see cref="MergeStateStatus"/> and
/// <see cref="ReviewDecision"/> are GitHub's GraphQL values (<c>null</c> when unknown, e.g. when
/// <see cref="GraphQlAvailable"/> is false for a token GraphQL refuses).
/// </summary>
public sealed record MergeReadinessFacts
{
    public bool PullRequestOpen { get; init; } = true;

    public bool? CanWrite { get; init; }

    public IReadOnlyCollection<string>? AllowedMergeMethods { get; init; }

    /// <summary>The merge method AIDE uses (<c>GitHub:ReleaseMergeMethod</c>, default <c>merge</c>).</summary>
    public string MergeMethod { get; init; } = "merge";

    /// <summary>The branch rules' required approving review count (0 when none).</summary>
    public int RequiredApprovingReviews { get; init; }

    public string? MergeStateStatus { get; init; }

    public string? ReviewDecision { get; init; }

    public bool GraphQlAvailable { get; init; } = true;

    public bool HasConflicts { get; init; }

    public IReadOnlyList<CheckObservation> Checks { get; init; } = [];
}
