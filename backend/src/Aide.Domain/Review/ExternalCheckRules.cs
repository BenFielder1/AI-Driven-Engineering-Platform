using System.Globalization;
using Aide.Domain.Releases;

namespace Aide.Domain.Review;

/// <summary>Whether review waits for pending checks, or goes on (and whether it gave up waiting).</summary>
public sealed record ChecksWaitDecision(bool Wait, DateTimeOffset? RecheckAt, DateTimeOffset? WaitingSince, int? WaitedSeconds, bool TimedOut)
{
    public static ChecksWaitDecision Proceed(int? waitedSeconds, bool timedOut) => new(false, null, null, waitedSeconds, timedOut);
}

/// <summary>
/// FR-85, §4.14: GitHub check runs and commit statuses on the PR head as a deterministic review input. Pending checks are
/// waited for (the job re-queues itself every 30 seconds; waiting is never a failed attempt) for up to 15 minutes, then
/// review proceeds with a warning (<c>checksTimedOut</c>). A failed required check is a major <c>testing</c> finding
/// (−0.10, v1 §4.7) with the check name and details URL as evidence. Pure.
/// </summary>
public static class ExternalCheckRules
{
    /// <summary>How long review waits for pending checks before scoring (FR-85).</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(15);

    /// <summary>How often pending checks are looked at again while waiting (§4.14).</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// With no pending check: proceed (<c>waitedSeconds</c> is how long review waited, <c>null</c> when it didn't). With
    /// pending checks: wait — starting now when not waiting yet — until 15 minutes have passed since waiting started; then
    /// proceed with <c>timedOut</c>.
    /// </summary>
    public static ChecksWaitDecision Decide(IEnumerable<CheckObservation> checks, DateTimeOffset? waitingSince, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var since = waitingSince ?? now;
        var waited = waitingSince is null ? (int?)null : (int)Math.Max(0, Math.Round((now - since).TotalSeconds));
        if (!checks.Any(MergeReadinessRules.IsPending))
        {
            return ChecksWaitDecision.Proceed(waited, timedOut: false);
        }

        return now - since >= WaitLimit
            ? ChecksWaitDecision.Proceed(waited, timedOut: true)
            : new ChecksWaitDecision(true, now + RecheckInterval, since, waited, false);
    }

    /// <summary>The finding text for a failed required check (FR-85).</summary>
    public static string FailedMessage(string name) => string.Create(CultureInfo.InvariantCulture, $"required check {name} failed");

    /// <summary>FR-85: one major <c>testing</c> deterministic finding per failed required check (evidence: name and details URL).</summary>
    public static IReadOnlyList<ReviewFinding> Findings(IEnumerable<CheckObservation> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return
        [
            .. checks.Where(c => c.Required && MergeReadinessRules.IsFailing(c))
                .Select(c => new ReviewFinding(
                    ReviewCategory.Testing,
                    Severity.Major,
                    FailedMessage(c.Name),
                    Source: FindingSource.Deterministic,
                    Evidence: string.IsNullOrEmpty(c.DetailsUrl) ? c.Name : $"{c.Name}: {c.DetailsUrl}")),
        ];
    }
}
