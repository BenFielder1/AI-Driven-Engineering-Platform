using Aide.Domain.Common;
using Aide.Domain.Entities;

namespace Aide.Domain.Releases;

/// <summary>What one merge poll saw (§4.14, FR-65).</summary>
public enum MergePollObservation
{
    /// <summary>The PR (or, for Local, the version branch) is still open and not merged.</summary>
    Open = 0,

    /// <summary>GitHub answered <c>304 Not Modified</c> to the conditional request: nothing changed since the last poll.</summary>
    NotModified = 1,

    /// <summary>The PR was merged (GitHub <c>merged = true</c>), or the Local branch is in the default branch.</summary>
    Merged = 2,

    /// <summary>The PR was closed without being merged.</summary>
    ClosedUnmerged = 3,
}

/// <summary>What the merge poll does next.</summary>
public enum MergePollDecisionKind
{
    /// <summary>Poll again at <see cref="MergePollDecision.NextPollAt"/>.</summary>
    Wait = 0,

    /// <summary>Tag the merge commit, create the GitHub Release and complete the release (FR-64).</summary>
    Complete = 1,

    /// <summary>The attempt fails with <see cref="MergePollDecision.ErrorCode"/>; the project stays in <c>release</c>.</summary>
    Fail = 2,
}

/// <summary>The merge poll's decision (§4.14).</summary>
public sealed record MergePollDecision(MergePollDecisionKind Kind, DateTimeOffset? NextPollAt, string? ErrorCode, string? Message)
{
    public static MergePollDecision WaitUntil(DateTimeOffset next) => new(MergePollDecisionKind.Wait, next, null, null);

    public static MergePollDecision Complete { get; } = new(MergePollDecisionKind.Complete, null, null, null);

    public static MergePollDecision Fail(string errorCode, string message) => new(MergePollDecisionKind.Fail, null, errorCode, message);
}

/// <summary>
/// §4.14, FR-65, D-V2-8: the merge poll of an <c>awaiting_merge</c> release attempt. Every 2 minutes for the first hour,
/// then every 10 minutes, for up to 14 days after the attempt started waiting; then <c>MERGE_TIMEOUT</c>. A merge
/// completes the release; a PR closed without merging is <c>PR_CLOSED</c>. Pure.
/// </summary>
public static class MergePollRules
{
    /// <summary>The poll interval during <see cref="FastPhase"/>.</summary>
    public static readonly TimeSpan FastInterval = TimeSpan.FromMinutes(2);

    /// <summary>How long after the attempt starts waiting the fast interval applies.</summary>
    public static readonly TimeSpan FastPhase = TimeSpan.FromHours(1);

    /// <summary>The poll interval after <see cref="FastPhase"/>.</summary>
    public static readonly TimeSpan SlowInterval = TimeSpan.FromMinutes(10);

    /// <summary>How long the poll waits for the merge in total (§4.14: 14 days).</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromDays(14);

    /// <summary>
    /// When to poll next after a poll at <paramref name="now"/> for an attempt waiting since <paramref name="since"/>: 2
    /// minutes later within the first hour, else 10 minutes later — never after the 14-day ceiling (the last poll runs at it).
    /// </summary>
    public static DateTimeOffset NextPollAt(DateTimeOffset since, DateTimeOffset now)
    {
        var elapsed = now - since;
        var next = now + (elapsed < FastPhase ? FastInterval : SlowInterval);
        var deadline = since + Ceiling;
        return next > deadline ? (now >= deadline ? now : deadline) : next;
    }

    /// <summary>The 14-day ceiling has passed (<see cref="Ceiling"/>).</summary>
    public static bool IsTimedOut(DateTimeOffset since, DateTimeOffset now) => now - since >= Ceiling;

    /// <summary>
    /// The decision after a poll: merged → complete (even at the ceiling: the merge happened); closed without merging →
    /// <c>PR_CLOSED</c>; still open or unchanged → wait, unless the 14 days are over (<c>MERGE_TIMEOUT</c>).
    /// </summary>
    public static MergePollDecision Decide(DateTimeOffset since, DateTimeOffset now, MergePollObservation observation) => observation switch
    {
        MergePollObservation.Merged => MergePollDecision.Complete,
        MergePollObservation.ClosedUnmerged => MergePollDecision.Fail(
            ErrorCodes.PrClosed,
            "The pull request was closed without being merged. Reopen it on GitHub, then retry the release."),
        _ when IsTimedOut(since, now) => MergePollDecision.Fail(
            ErrorCodes.MergeTimeout,
            "The pull request wasn't merged within 14 days. Merge it on GitHub (or fix what blocks it), then retry the release."),
        _ => MergePollDecision.WaitUntil(NextPollAt(since, now)),
    };
}

/// <summary>
/// FR-42, FR-43, FR-65: the release attempt's status machine. <c>in_progress → succeeded | failed | awaiting_merge</c>;
/// <c>awaiting_merge → succeeded</c> (merged on GitHub, §4.14) or <c>failed</c> (<c>PR_CLOSED</c>, <c>MERGE_TIMEOUT</c>).
/// Finished attempts never change. Pure.
/// </summary>
public static class ReleaseAttemptRules
{
    public static bool CanTransition(ReleaseStatus from, ReleaseStatus to) => (from, to) switch
    {
        (ReleaseStatus.InProgress, ReleaseStatus.Succeeded) => true,
        (ReleaseStatus.InProgress, ReleaseStatus.Failed) => true,
        (ReleaseStatus.InProgress, ReleaseStatus.AwaitingMerge) => true,
        (ReleaseStatus.AwaitingMerge, ReleaseStatus.Succeeded) => true,
        (ReleaseStatus.AwaitingMerge, ReleaseStatus.Failed) => true,
        _ => false,
    };

    /// <summary>An attempt still open: in progress, or awaiting its merge.</summary>
    public static bool IsOpen(ReleaseStatus status) => status is ReleaseStatus.InProgress or ReleaseStatus.AwaitingMerge;
}
