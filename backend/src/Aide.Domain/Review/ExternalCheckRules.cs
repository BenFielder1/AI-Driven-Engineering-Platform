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
/// (−0.10, 0.1.0 §4.7) with the check name and details URL as evidence. Pure.
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

    /// <summary>
    /// FR-85: one major <c>testing</c> deterministic finding per failed required check (evidence: name and details URL).
    /// FR-236, FR-237: each is a failure finding (<c>failure:check:&lt;name&gt;</c>); when the host gave the check's details,
    /// <paramref name="excerpts"/> (by check name, built by <see cref="Excerpt"/> right after the fetch) is its evidence and
    /// first error line. Failing checks that aren't required stay dropped (D-0.6.1-8).
    /// </summary>
    public static IReadOnlyList<ReviewFinding> Findings(IEnumerable<CheckObservation> checks, IReadOnlyDictionary<string, FailureExcerptResult>? excerpts = null)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return
        [
            .. checks.Where(c => c.Required && MergeReadinessRules.IsFailing(c))
                .Select(c => Failure(c, Severity.Major, FailedMessage(c.Name), excerpts)),
        ];
    }

    /// <summary>FR-236, FR-237: the failure finding of a failed required check, with its excerpt when there is one.</summary>
    public static ReviewFinding Failure(CheckObservation check, Severity severity, string message, IReadOnlyDictionary<string, FailureExcerptResult>? excerpts)
    {
        ArgumentNullException.ThrowIfNull(check);
        var excerpt = excerpts is not null && excerpts.TryGetValue(check.Name, out var found) ? found : null;
        return new ReviewFinding(ReviewCategory.Testing, severity, message, Source: FindingSource.Deterministic, Evidence: excerpt?.Text ?? Head(check))
        {
            FailureId = FailureIds.Of(FailureIds.Check, check.Name),
            FailureErrorLine = excerpt?.ErrorLine,
        };
    }

    /// <summary>A failed check's evidence without details: <c>name: URL</c> (the name alone without a URL).</summary>
    public static string Head(CheckObservation check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return string.IsNullOrEmpty(check.DetailsUrl) ? check.Name : $"{check.Name}: {check.DetailsUrl}";
    }

    /// <summary>
    /// FR-236: a failed check's excerpt: <c>name: URL</c>, the check output's title and summary, then the §4.38 excerpt of its
    /// annotations and log; <c>null</c> when the host gave nothing beyond the name and URL.
    /// </summary>
    public static FailureExcerptResult? Excerpt(CheckObservation check, CheckFailureDetail detail)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(detail);
        if (!detail.HasContent)
        {
            return null;
        }

        var summary = detail.Summary is { Length: > FailureExcerpt.MaxSummaryLength } full ? full[..FailureExcerpt.MaxSummaryLength] + "…" : detail.Summary;
        return FailureExcerpt.Compose(detail.Log, detail.Annotations, [Head(check), detail.Title, summary]);
    }
}
