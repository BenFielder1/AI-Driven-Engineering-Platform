using System.Globalization;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Releases;
using Aide.Domain.Review;

namespace Aide.Domain.Execution;

/// <summary>
/// A component of the version's preview rollout as the build check sees it (§4.47): what it deploys, where, its current
/// deployment state (<c>null</c>: not started yet) and its link.
/// </summary>
public sealed record BuildCheckPreviewComponent(DeploymentComponent Component, DeploymentProvider Platform, DeploymentStatus? Status, string? Url = null);

/// <summary>
/// The version's newest preview rollout (§4.47): the commit it deploys (<c>null</c>: none queued yet), its overall state
/// (<c>null</c>: none yet) and the components it deploys (the configured preview targets; hosts without previews, such as
/// GitHub Pages, are left out as review leaves them out).
/// </summary>
public sealed record BuildCheckPreview(string? RolloutSha, RolloutState? State, IReadOnlyList<BuildCheckPreviewComponent> Components);

/// <summary>
/// The PR's CI checks as the build check read them (§4.47): the commit they were read for and the checks with
/// <see cref="CheckObservation.Required"/> already marked from the branch rules (<see cref="MergeReadinessRules.MarkRequired"/>,
/// the review collectors' rules).
/// </summary>
public sealed record BuildCheckChecks(string? Sha, IReadOnlyList<CheckObservation> Checks);

/// <summary>
/// What one poll of a build check saw (§4.47). <see cref="Preview"/> is <c>null</c> when the version has no preview target;
/// <see cref="Checks"/> is <c>null</c> when there is no checks host, PR or readable checks (the review collectors' rules).
/// <see cref="PreviewWaitSince"/> and <see cref="ChecksWaitSince"/> are the values the previous poll's decision returned
/// (kept in the job payload), <c>null</c> on the first poll.
/// </summary>
public sealed record BuildCheckObservation(string HeadSha, BuildCheckPreview? Preview, BuildCheckChecks? Checks)
{
    public DateTimeOffset? PreviewWaitSince { get; init; }

    public DateTimeOffset? ChecksWaitSince { get; init; }
}

/// <summary>
/// The result of one poll (§4.47). While <see cref="BuildCheckStatus.Waiting"/>, the job defers itself to
/// <see cref="RecheckAt"/> and stores <see cref="PreviewWaitSince"/>/<see cref="ChecksWaitSince"/> for the next poll.
/// <see cref="Sources"/> are the sources read for the head SHA with their results (for <see cref="BuildCheck.Sources"/>);
/// <see cref="TimedOut"/> are the failures of sources that didn't finish in time (each with a failure id as review gives
/// it; add them to the failures built from the sources' findings, <see cref="BuildCheckFailures.Collect"/>).
/// <see cref="Failing"/> names the sources that failed or timed out.
/// </summary>
public sealed record BuildCheckDecision(
    BuildCheckStatus Status,
    DateTimeOffset? RecheckAt,
    DateTimeOffset? PreviewWaitSince,
    DateTimeOffset? ChecksWaitSince,
    IReadOnlyList<BuildCheckSource> Sources,
    IReadOnlyList<BuildCheckFailure> TimedOut,
    IReadOnlyList<string> Failing)
{
    /// <summary>The check keeps waiting (defer the job, never sleep).</summary>
    public bool Wait => Status == BuildCheckStatus.Waiting;

    /// <summary><c>failed</c>: plan fix k+1 (§4.48).</summary>
    public bool PlanFix => Status == BuildCheckStatus.Failed;

    /// <summary><c>passed</c>, <c>skipped</c> or <c>exhausted</c>: the PR job's advance to Review runs, with no new rollout.</summary>
    public bool ToReview => BuildCheckRules.GoesToReview(Status);
}

/// <summary>
/// §4.47, FR-254–FR-258: the build check's state rules, pure. A check belongs to a release version, a review cycle and
/// an attempt: attempt 0 checks the commit pushed after the plan's own steps, attempt k the commit after the k-th fix.
/// With <c>maxBuildFixAttempts</c> = m a cycle has at most m fixes and m + 1 checks, and m = 0 turns the check off.
/// <list type="table">
/// <item><term>— → waiting</term><description>the PR job finishes after execution with m &gt; 0 (<see cref="ShouldStart"/>).</description></item>
/// <item><term>waiting → skipped</term><description>no preview target and no required checks.</description></item>
/// <item><term>waiting → passed</term><description>the head SHA's rollout and every required check on it succeeded.</description></item>
/// <item><term>waiting → failed</term><description>a source failed or timed out for the head SHA, and fixes used &lt; m.</description></item>
/// <item><term>waiting → exhausted</term><description>the same, and fixes used = m.</description></item>
/// <item><term>waiting | failed → exhausted</term><description>fix planning invalid after one retry, or a blocked fix step answered "continue" (<see cref="Exhaust"/>).</description></item>
/// <item><term>waiting | failed → cancelled</term><description>the version is invalidated or rolled back, or execution is stopped (<see cref="Cancel"/>).</description></item>
/// </list>
/// Results for an older SHA are ignored and the check keeps waiting. The waits are the existing ones: the preview rollout
/// follows <see cref="PreviewReviewRules"/> (10 s re-checks, 30 minutes), the required checks <see cref="ExternalCheckRules"/>
/// (30 s, 15 minutes). The check waits until every source has finished or timed out, so a fix sees every failure at once.
/// </summary>
public static class BuildCheckRules
{
    /// <summary>FR-258: the lowest <c>maxBuildFixAttempts</c> (0 turns the build check off).</summary>
    public const int MinAttempts = 0;

    /// <summary>FR-258: the highest <c>maxBuildFixAttempts</c>.</summary>
    public const int MaxAttempts = 10;

    /// <summary>The source name of a preview rollout with no component (shown in the panel).</summary>
    public const string PreviewSourceName = "Preview";

    /// <summary>The source name when the checks host never reported checks for the head commit (failure <c>failure:check:required-checks</c>).</summary>
    public const string HeadChecksSourceName = "required checks";

    /// <summary>The conclusion recorded for a source that didn't finish in time.</summary>
    public const string TimedOutConclusion = "timed_out";

    /// <summary>FR-258: a <c>maxBuildFixAttempts</c> value the settings accept (0–10; out of range is 400 <c>VALIDATION</c>).</summary>
    public static bool IsValidMaxAttempts(int value) => value is >= MinAttempts and <= MaxAttempts;

    /// <summary>FR-254, FR-258: whether a build check starts after execution; with 0 execution goes straight to Review (0.6.0).</summary>
    public static bool ShouldStart(int maxBuildFixAttempts) => maxBuildFixAttempts > 0;

    /// <summary>
    /// §4.47, FR-258: the attempt number of the next check of <paramref name="reviewCycle"/>: 0 when the cycle has no check
    /// in force yet (the count resets each review cycle, and after a rollback or invalidation supersedes the earlier
    /// checks), else one more than the cycle's highest attempt in force.
    /// </summary>
    public static int NextAttempt(IEnumerable<BuildCheck> versionChecks, int reviewCycle)
    {
        ArgumentNullException.ThrowIfNull(versionChecks);
        var inCycle = versionChecks.Where(c => c.ReviewCycle == reviewCycle && c.SupersededAt is null).Select(c => c.Attempt).ToList();
        return inCycle.Count == 0 ? 0 : inCycle.Max() + 1;
    }

    /// <summary>The terminal states: <c>passed</c>, <c>skipped</c>, <c>exhausted</c>, <c>cancelled</c>.</summary>
    public static bool IsFinal(BuildCheckStatus status) =>
        status is BuildCheckStatus.Passed or BuildCheckStatus.Skipped or BuildCheckStatus.Exhausted or BuildCheckStatus.Cancelled;

    /// <summary>§4.47: the states after which the PR job's advance to Review runs (no new rollout is queued).</summary>
    public static bool GoesToReview(BuildCheckStatus status) =>
        status is BuildCheckStatus.Passed or BuildCheckStatus.Skipped or BuildCheckStatus.Exhausted;

    /// <summary>
    /// §4.47: the allowed state changes. <c>waiting</c> may become any other state; <c>failed</c> (its fix is being
    /// planned or run) may become <c>exhausted</c> or <c>cancelled</c>; the final states never change. Staying in
    /// <c>waiting</c> (a poll without news) is allowed.
    /// </summary>
    public static bool CanTransition(BuildCheckStatus from, BuildCheckStatus to) => from switch
    {
        BuildCheckStatus.Waiting => true,
        BuildCheckStatus.Failed => to is BuildCheckStatus.Exhausted or BuildCheckStatus.Cancelled,
        _ => false,
    };

    /// <summary>
    /// §4.47: the version was invalidated or rolled back, or execution was stopped: a <c>waiting</c> or <c>failed</c> check
    /// becomes <c>cancelled</c>; a final one keeps its state.
    /// </summary>
    public static BuildCheckStatus Cancel(BuildCheckStatus current) =>
        CanTransition(current, BuildCheckStatus.Cancelled) ? BuildCheckStatus.Cancelled : current;

    /// <summary>
    /// §4.47, FR-258: fix planning stayed invalid after one retry, or a blocked build-fix step's escalation was answered
    /// "continue": a <c>waiting</c> or <c>failed</c> check becomes <c>exhausted</c> (then Review starts with the failures).
    /// </summary>
    public static BuildCheckStatus Exhaust(BuildCheckStatus current) =>
        CanTransition(current, BuildCheckStatus.Exhausted) ? BuildCheckStatus.Exhausted : current;

    /// <summary>
    /// §4.48 step 4: after an invalid fix plan, whether to retry once with the reasons (<paramref name="retriesUsed"/> = 0)
    /// or end the check <c>exhausted</c>.
    /// </summary>
    public static bool RetryFixPlanning(int retriesUsed) => retriesUsed < 1;

    /// <summary>
    /// §4.47: one poll. <paramref name="attempt"/> is the fixes used in this cycle so far (<see cref="FixesUsed"/>; the check's
    /// attempt number when no check was superseded or cancelled) and
    /// <paramref name="maxFixes"/> the project's <c>maxBuildFixAttempts</c>. See the type summary for the table.
    /// </summary>
    public static BuildCheckDecision Decide(BuildCheckObservation observation, int attempt, int maxFixes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        var head = observation.HeadSha;
        var checks = observation.Checks;
        var checksCurrent = checks is not null && SameSha(checks.Sha, head);
        var required = checksCurrent ? checks!.Checks.Where(c => c.Required).ToList() : [];

        // No preview target and no required checks (on the head SHA): nothing to wait for.
        if (observation.Preview is null && (checks is null || (checksCurrent && required.Count == 0)))
        {
            return new BuildCheckDecision(BuildCheckStatus.Skipped, null, null, null, [], [], []);
        }

        var sources = new List<BuildCheckSource>();
        var timedOut = new List<BuildCheckFailure>();
        var failing = new List<string>();
        DateTimeOffset? recheck = null;
        DateTimeOffset? previewSince = null;
        DateTimeOffset? checksSince = null;

        if (observation.Preview is { } preview)
        {
            var wait = Preview(preview, head, observation.PreviewWaitSince, now, sources, timedOut, failing);
            previewSince = wait?.Since;
            recheck = Earliest(recheck, wait?.RecheckAt);
        }

        if (checks is not null)
        {
            var wait = Checks(checksCurrent, required, observation.ChecksWaitSince, now, sources, timedOut, failing);
            checksSince = wait?.Since;
            recheck = Earliest(recheck, wait?.RecheckAt);
        }

        if (recheck is not null)
        {
            return new BuildCheckDecision(BuildCheckStatus.Waiting, recheck, previewSince, checksSince, sources, [], []);
        }

        var status = failing.Count == 0
            ? BuildCheckStatus.Passed
            : attempt < maxFixes ? BuildCheckStatus.Failed : BuildCheckStatus.Exhausted;
        return new BuildCheckDecision(status, null, null, null, sources, timedOut, failing);
    }

    /// <summary>
    /// Whether two commit SHAs are the same commit: equal ignoring case, or one a prefix of the other of at least 7
    /// characters (a host may report the short SHA). An unknown SHA never matches.
    /// </summary>
    public static bool SameSha(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        a = a.Trim();
        b = b.Trim();
        if (a.Length == b.Length)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        var (shorter, longer) = a.Length < b.Length ? (a, b) : (b, a);
        return shorter.Length >= 7 && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The <c>failure:preview:&lt;platform&gt;</c> id of a preview component (as review gives it: component-qualified when the
    /// rollout deploys more than one component to the platform, <see cref="RolloutFindings.FailureId"/>).
    /// </summary>
    public static string PreviewFailureId(BuildCheckPreviewComponent component, IReadOnlyList<BuildCheckPreviewComponent> components)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(components);
        var outcomes = components.Select(Outcome).ToList();
        return RolloutFindings.FailureId(Outcome(component), outcomes);
    }

    /// <summary>The message of a preview that didn't finish within <see cref="PreviewReviewRules.WaitLimit"/>.</summary>
    public static string PreviewTimedOutMessage(DeploymentProvider platform) =>
        string.Create(CultureInfo.InvariantCulture, $"{DeploymentPlatforms.DisplayName(platform)} preview didn't finish within {(int)PreviewReviewRules.WaitLimit.TotalMinutes} minutes");

    /// <summary>The message when no checks were reported for the head commit within <see cref="ExternalCheckRules.WaitLimit"/>.</summary>
    public static string HeadChecksTimedOutMessage =>
        string.Create(CultureInfo.InvariantCulture, $"no checks were reported for the head commit within {(int)ExternalCheckRules.WaitLimit.TotalMinutes} minutes");

    /// <summary>
    /// §4.47, FR-258: the fixes used in <paramref name="reviewCycle"/> before <paramref name="current"/>: the checks in force
    /// (not superseded by a rollback or invalidation, not cancelled) that fix steps were planned from. This, not the attempt
    /// number, is what <see cref="Decide"/> compares with <c>maxBuildFixAttempts</c>.
    /// </summary>
    public static int FixesUsed(IEnumerable<BuildCheck> versionChecks, int reviewCycle, Guid current, IReadOnlySet<Guid> checksWithFixSteps)
    {
        ArgumentNullException.ThrowIfNull(versionChecks);
        ArgumentNullException.ThrowIfNull(checksWithFixSteps);
        return versionChecks.Count(c => c.ReviewCycle == reviewCycle && c.Id != current && c.SupersededAt is null
            && c.Status != BuildCheckStatus.Cancelled && checksWithFixSteps.Contains(c.Id));
    }

    /// <summary>The message of a required check that didn't finish within <see cref="ExternalCheckRules.WaitLimit"/>.</summary>
    public static string CheckTimedOutMessage(string name) =>
        string.Create(CultureInfo.InvariantCulture, $"required check {name} didn't finish within {(int)ExternalCheckRules.WaitLimit.TotalMinutes} minutes");

    private static Wait? Preview(
        BuildCheckPreview preview, string head, DateTimeOffset? since, DateTimeOffset now,
        List<BuildCheckSource> sources, List<BuildCheckFailure> timedOut, List<string> failing)
    {
        var current = SameSha(preview.RolloutSha, head) && preview.State is { } state ? state : (RolloutState?)null;
        var failed = current is RolloutState.Failed or RolloutState.Blocked
            || (current is not null && preview.Components.Any(c => c.Status == DeploymentStatus.Error));
        var succeeded = current == RolloutState.Succeeded && !failed;
        if (failed || succeeded)
        {
            foreach (var c in preview.Components)
            {
                sources.Add(new BuildCheckSource(Name(c), BuildCheckSourceKind.Preview, ComponentConclusion(c, failed), c.Url));
            }

            if (preview.Components.Count == 0)
            {
                sources.Add(new BuildCheckSource(PreviewSourceName, BuildCheckSourceKind.Preview, failed ? "failure" : "success", null));
            }

            if (failed)
            {
                var broken = preview.Components.Where(c => c.Status == DeploymentStatus.Error).Select(Name).ToList();
                failing.AddRange(broken.Count > 0 ? broken : preview.Components.Count > 0 ? preview.Components.Select(Name) : [PreviewSourceName]);
            }

            return null;
        }

        // Running, cancelled (a newer rollout replaces it), not queued yet, or a rollout of an older SHA: keep waiting.
        var decision = PreviewReviewRules.Decide(DeploymentStatus.Building, since, now);
        if (decision.Wait)
        {
            foreach (var c in preview.Components)
            {
                sources.Add(new BuildCheckSource(Name(c), BuildCheckSourceKind.Preview, null, current is null ? null : c.Url));
            }

            return new Wait(decision.RecheckAt!.Value, decision.WaitingSince!.Value);
        }

        // Timed out (30 minutes): the components that hadn't finished (all of them when none is known to be unfinished).
        var unfinished = current is null
            ? preview.Components.ToList()
            : preview.Components.Where(c => c.Status is not { } s || !DeploymentStates.IsTerminal(s)).ToList();
        if (unfinished.Count == 0)
        {
            unfinished = [.. preview.Components];
        }

        foreach (var c in preview.Components)
        {
            sources.Add(new BuildCheckSource(Name(c), BuildCheckSourceKind.Preview, unfinished.Contains(c) ? TimedOutConclusion : ComponentConclusion(c, false), current is null ? null : c.Url));
        }

        foreach (var c in unfinished)
        {
            var id = PreviewFailureId(c, preview.Components);
            timedOut.Add(new BuildCheckFailure(id, id, PreviewTimedOutMessage(c.Platform), FailureIds.Preview, null));
            failing.Add(Name(c));
        }

        if (preview.Components.Count == 0)
        {
            sources.Add(new BuildCheckSource(PreviewSourceName, BuildCheckSourceKind.Preview, TimedOutConclusion, null));
            var id = FailureIds.Of(FailureIds.Preview, "preview");
            timedOut.Add(new BuildCheckFailure(id, id, string.Create(CultureInfo.InvariantCulture, $"Preview didn't finish within {(int)PreviewReviewRules.WaitLimit.TotalMinutes} minutes"), FailureIds.Preview, null));
            failing.Add(PreviewSourceName);
        }

        return null;
    }

    private static Wait? Checks(
        bool current, List<CheckObservation> required, DateTimeOffset? since, DateTimeOffset now,
        List<BuildCheckSource> sources, List<BuildCheckFailure> timedOut, List<string> failing)
    {
        // Checks read for an older SHA count as pending: the head SHA's checks haven't been reported yet.
        var pending = current ? required.Where(MergeReadinessRules.IsPending).ToList() : [];
        var waitFor = current ? pending : [new CheckObservation("head", CheckRunStatus.Queued, null, true, null)];
        var decision = ExternalCheckRules.Decide(waitFor, since, now);
        if (decision.Wait)
        {
            sources.AddRange(required.Select(c => new BuildCheckSource(c.Name, BuildCheckSourceKind.Check, Conclusion(c), c.DetailsUrl)));
            return new Wait(decision.RecheckAt!.Value, decision.WaitingSince!.Value);
        }

        if (!current && decision.TimedOut)
        {
            // The host never reported checks for the head SHA within the wait: a failure, never a pass.
            sources.Add(new BuildCheckSource(HeadChecksSourceName, BuildCheckSourceKind.Check, TimedOutConclusion, null));
            var id = FailureIds.Of(FailureIds.Check, HeadChecksSourceName);
            timedOut.Add(new BuildCheckFailure(id, id, HeadChecksTimedOutMessage, FailureIds.Check, null));
            failing.Add(HeadChecksSourceName);
            return null;
        }

        foreach (var check in required)
        {
            var timed = decision.TimedOut && MergeReadinessRules.IsPending(check);
            sources.Add(new BuildCheckSource(check.Name, BuildCheckSourceKind.Check, timed ? TimedOutConclusion : Conclusion(check), check.DetailsUrl));
            if (timed)
            {
                var id = FailureIds.Of(FailureIds.Check, check.Name);
                timedOut.Add(new BuildCheckFailure(id, id, CheckTimedOutMessage(check.Name), FailureIds.Check, null));
                failing.Add(check.Name);
            }
            else if (MergeReadinessRules.IsFailing(check))
            {
                failing.Add(check.Name);
            }
        }

        return null;
    }

    private static string? Conclusion(CheckObservation check) =>
        MergeReadinessRules.IsPending(check) ? null : check.Conclusion?.ToWire() ?? "neutral";

    private static string? ComponentConclusion(BuildCheckPreviewComponent c, bool rolloutFailed) => c.Status switch
    {
        DeploymentStatus.Ready => "success",
        DeploymentStatus.Error => "failure",
        DeploymentStatus.Canceled => "cancelled",
        _ when rolloutFailed => "skipped",
        _ => "success",
    };

    private static string Name(BuildCheckPreviewComponent c) =>
        $"{DeploymentPlatforms.DisplayName(c.Platform)} preview ({c.Component.ToWire()})";

    private static RolloutComponentOutcome Outcome(BuildCheckPreviewComponent c) =>
        new(c.Component, c.Platform, c.Status ?? DeploymentStatus.Queued, null, null, null, c.Url);

    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;

    private sealed record Wait(DateTimeOffset RecheckAt, DateTimeOffset Since);
}

/// <summary>FR-258: the review prompt's note about a build check that ended exhausted. Pure.</summary>
public static class BuildCheckText
{
    /// <summary>The <c>REVIEW_CYCLE</c> line after the cycle number: the build check was exhausted after <paramref name="attempts"/> fix attempts.</summary>
    public static string Exhausted(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"The build check before this review was exhausted after {attempts} fix {(attempts == 1 ? "attempt" : "attempts")}: the preview build or a required check still fails. Its failures are in the deterministic findings.");
    }
}
