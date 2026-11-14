using Aide.Api.Features.Git;
using Aide.Api.Jobs;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Releases;
using Aide.Domain.Review;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Review;

/// <summary>The PR head's checks as review used them (FR-85), how long it waited and whether it gave up waiting.</summary>
public sealed record CollectedChecks(IReadOnlyList<CheckObservation> Checks, int? WaitedSeconds, bool TimedOut)
{
    /// <summary>FR-139: the Git host the statuses came from (GitLab, Bitbucket); <c>null</c> for GitHub's check runs (FR-85).</summary>
    public Domain.GitHosts.CommitStatusHost? Host { get; init; }

    /// <summary>
    /// FR-236: the excerpt of each failed required check the host described (output, annotations, log), by check name; built
    /// right after each fetch, so whole logs are never kept.
    /// </summary>
    public IReadOnlyDictionary<string, FailureExcerptResult> Excerpts { get; init; } = new Dictionary<string, FailureExcerptResult>(StringComparer.Ordinal);

    /// <summary>FR-255: the commit the checks were read for (the PR head, or the branch head for GitLab/Bitbucket).</summary>
    public string? HeadSha { get; init; }

    /// <summary>
    /// The review findings: FR-139 for a GitLab project ("required pipeline job test:backend failed", major when required,
    /// else minor), FR-85 for GitHub ("required check ci / test failed"). FR-236: a failed required check's evidence is the
    /// excerpt in <see cref="Excerpts"/>.
    /// </summary>
    public IReadOnlyList<ReviewFinding> Findings() =>
        Host is { } host ? Domain.GitHosts.CommitStatusFindings.Findings(Checks, host, Excerpts) : ExternalCheckRules.Findings(Checks, Excerpts);

    /// <summary>The <c>compliance_report.external_checks</c> JSON (the <see cref="ExternalChecksDto"/> shape) without a preview.</summary>
    public string ToJson() => Compose(this, null)!;

    /// <summary>
    /// The <c>compliance_report.external_checks</c> JSON (the <see cref="ExternalChecksDto"/> shape): the PR head's checks
    /// (FR-85) and the preview deployment review waited for (FR-90); <c>null</c> when there is neither. FR-236, FR-240: with
    /// failure findings, also <c>failures</c> (not part of the API; read back by <see cref="StoredFailures"/>): each failure's
    /// id, source, excerpt (the finding's evidence), fingerprint (§4.37) and consecutive cycles, which the next cycle compares with.
    /// FR-249: with <paramref name="waivers"/>, also <c>waivers</c> (not part of the API; read back by <see cref="StoredWaivers"/>):
    /// the waivers in force after the cycle (id, message, reason, cycle). With waivers but no checks and no preview, the JSON
    /// holds only <c>waivers</c> (read as no external checks).
    /// </summary>
    public static string? Compose(
        CollectedChecks? checks,
        Deployments.PreviewCheckDto? preview,
        IReadOnlyList<ReviewFinding>? findings = null,
        IReadOnlyList<FailureRecord>? records = null,
        IReadOnlyList<FindingWaiver>? waivers = null)
    {
        var storedWaivers = (waivers ?? []).Select(w => new StoredWaiver(w.Id, w.Message, w.Reason, w.Cycle)).ToList();
        if (checks is null && preview is null)
        {
            return storedWaivers.Count == 0 ? null : AideJson.Serialize(new { waivers = storedWaivers });
        }

        var failures = (records ?? [])
            .Select(r => new StoredFailure(
                r.Id,
                FailureIds.SourceOf(r.Id) ?? string.Empty,
                findings?.FirstOrDefault(f => f.FailureId == r.Id)?.Evidence,
                r.Fingerprint,
                r.Cycles))
            .ToList();
        return AideJson.Serialize(new
        {
            checks = (checks?.Checks ?? []).Select(c => new ExternalCheckDto(c.Name, c.Status == CheckRunStatus.Completed ? c.Conclusion : null, c.Required, c.DetailsUrl)),
            checksWaitedSeconds = checks?.WaitedSeconds,
            checksTimedOut = checks?.TimedOut ?? false,
            preview,
            failures = failures.Count == 0 ? null : failures,
            waivers = storedWaivers.Count == 0 ? null : storedWaivers,
        });
    }

    /// <summary>
    /// FR-249: <paramref name="externalChecks"/> with its <c>waivers</c> replaced by <paramref name="waivers"/> (every other
    /// property kept); <c>null</c> stays <c>null</c> without waivers. Unreadable JSON is kept as it is.
    /// </summary>
    public static string? ReplaceWaivers(string? externalChecks, IReadOnlyList<FindingWaiver> waivers)
    {
        ArgumentNullException.ThrowIfNull(waivers);
        var stored = waivers.Select(w => new StoredWaiver(w.Id, w.Message, w.Reason, w.Cycle)).ToList();
        if (string.IsNullOrWhiteSpace(externalChecks))
        {
            return stored.Count == 0 ? externalChecks : AideJson.Serialize(new { waivers = stored });
        }

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(externalChecks) is not System.Text.Json.Nodes.JsonObject root)
            {
                return externalChecks;
            }

            root["waivers"] = stored.Count == 0 ? null : System.Text.Json.Nodes.JsonNode.Parse(AideJson.Serialize(stored));
            return root.ToJsonString();
        }
        catch (System.Text.Json.JsonException)
        {
            return externalChecks;
        }
    }

    /// <summary>FR-249: the waivers in force a report stored in <c>external_checks.waivers</c>; empty for older reports or unreadable JSON.</summary>
    public static IReadOnlyList<FindingWaiver> StoredWaivers(string? externalChecks)
    {
        if (string.IsNullOrWhiteSpace(externalChecks))
        {
            return [];
        }

        try
        {
            var stored = AideJson.Deserialize<StoredWaiverList>(externalChecks);
            return [.. (stored.Waivers ?? []).OfType<StoredWaiver>()
                .Where(w => !string.IsNullOrEmpty(w.Id) && w.Message is not null && !string.IsNullOrEmpty(w.Reason))
                .Select(w => new FindingWaiver(w.Id, w.Message!, w.Reason!, w.Cycle))];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>FR-240: the failure records a report stored in <c>external_checks.failures</c>; empty for older reports or unreadable JSON.</summary>
    public static IReadOnlyList<FailureRecord> StoredFailures(string? externalChecks)
    {
        if (string.IsNullOrWhiteSpace(externalChecks))
        {
            return [];
        }

        try
        {
            var stored = AideJson.Deserialize<StoredFailureList>(externalChecks);
            return [.. (stored.Failures ?? []).OfType<StoredFailure>()
                .Where(f => !string.IsNullOrEmpty(f.Id) && !string.IsNullOrEmpty(f.Fingerprint))
                .Select(f => new FailureRecord(f.Id, f.Fingerprint!, Math.Max(1, f.Cycles)))];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>One entry of <c>external_checks.failures</c>.</summary>
    private sealed record StoredFailure(string Id, string Source, string? Excerpt, string? Fingerprint, int Cycles);

    private sealed record StoredFailureList(List<StoredFailure?>? Failures);

    /// <summary>One entry of <c>external_checks.waivers</c>.</summary>
    private sealed record StoredWaiver(string Id, string? Message, string? Reason, int Cycle);

    private sealed record StoredWaiverList(List<StoredWaiver?>? Waivers);
}

/// <summary>
/// FR-85: before review scoring, the check runs and commit statuses on the version PR's head (Checks / Commit statuses:
/// read), marked required from the default branch's rules. Pending checks are waited for: the review job defers itself every
/// 30 seconds (not a blocking sleep, not a counted attempt; when waiting started is kept in the job payload) for up to 15
/// minutes, then review proceeds with <c>checksTimedOut</c>. Local projects, a version without a PR, or checks GitHub won't
/// show (no permission) give <c>null</c> without waiting; GitHub failing is logged and review goes on without them. GitLab
/// projects (FR-139) read the head's pipeline jobs and commit statuses through their provider.
/// </summary>
public sealed partial class ExternalChecksCollector(
    AideDbContext db,
    GitHubRepoAccess access,
    GitHostTargets hosts,
    IWorkingCopies workingCopies,
    GitHubApi api,
    TimeProvider time,
    ILogger<ExternalChecksCollector> logger)
{
    /// <summary>The checks, or <c>null</c>; throws <see cref="JobDeferredException"/> while waiting for pending checks.</summary>
    public Task<CollectedChecks?> CollectAsync(Project project, LifecycleJobPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return ReadCoreAsync(project, payload, excerpts: true, cancellationToken);
    }

    /// <summary>
    /// FR-255: the checks as they are now, without waiting (the build check decides the waits, §4.47), or <c>null</c> under
    /// the same rules as <see cref="CollectAsync"/>. Failure excerpts and logs are fetched only with <paramref name="excerpts"/>
    /// (the build check asks once its decision is final) and once no required check is pending. A rate limit still defers the
    /// job; a GitHub or Git host error, or a pull request that can't be read, is a transient failure (the job retries) rather
    /// than "no checks", which would skip the check.
    /// </summary>
    public Task<CollectedChecks?> ReadAsync(Project project, bool excerpts, CancellationToken cancellationToken) => ReadCoreAsync(project, null, excerpts, cancellationToken);

    private static JobFailedException Unreadable(string reason) =>
        new($"The checks of the head commit couldn't be read: {reason}", transient: true);

    private async Task<CollectedChecks?> ReadCoreAsync(Project project, LifecycleJobPayload? payload, bool excerpts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (await hosts.ResolveAsync(project.Id, cancellationToken) is { } host)
        {
            return await HostAsync(project, payload, excerpts, host, cancellationToken);
        }

        var target = await access.ResolveAsync(project.Id, cancellationToken);
        if (target.IsFailure)
        {
            LogUnavailable(logger, project.Id, target.Error!.Message);
            return null;
        }

        if (target.Value is not { } github)
        {
            return null; // Local: no checks, no wait
        }

        var versionId = project.CurrentReleaseVersionId ?? await db.CurrentReleaseVersionIdAsync(project.Id, cancellationToken);
        var number = versionId is { } id
            ? await db.ReleaseVersions.AsNoTracking().Where(v => v.Id == id).Select(v => v.PullRequestNumber).FirstOrDefaultAsync(cancellationToken)
            : null;
        if (number is not { } pullNumber)
        {
            return null;
        }

        var now = time.GetUtcNow();
        try
        {
            if (await api.FindPullAsync(github.Token, github.Owner, github.Repo, pullNumber, cancellationToken) is not { HeadSha: { } head })
            {
                return payload is null ? throw Unreadable("the pull request was not found") : null;
            }

            if (await api.ListChecksAsync(github.Token, github.Owner, github.Repo, head, cancellationToken) is not { } checks)
            {
                return null;
            }

            var rules = await api.GetBranchRulesAsync(github.Token, github.Owner, github.Repo, github.DefaultBranch, cancellationToken);
            // FR-255: for the build check (no review payload) a required context GitHub hasn't registered for the head yet
            // counts as pending, so the check waits for it (and fails it at 15 minutes) instead of passing or skipping.
            var marked = MergeReadinessRules.MarkRequired(checks, rules.RequiredChecks, includeMissingRequired: payload is null);
            var decision = payload is null ? null : ExternalCheckRules.Decide(marked, payload.ChecksWaitSince, now);
            if (decision is { Wait: true })
            {
                var pending = marked.Count(MergeReadinessRules.IsPending);
                LogWaiting(logger, project.Id, pending);
                throw new JobDeferredException(
                    $"Waiting for {pending} pending check(s) on the pull request before scoring (up to 15 minutes).",
                    decision.RecheckAt!.Value,
                    AideJson.Serialize(payload! with { ChecksWaitSince = decision.WaitingSince }));
            }

            if (decision is { TimedOut: true })
            {
                LogTimedOut(logger, project.Id);
            }

            if (payload is null && (!excerpts || RequiredPending(marked)))
            {
                return new CollectedChecks(marked, null, false) { HeadSha = head };
            }

            // FR-236: the failed required checks' output, annotations and job logs (each part optional, never failing the review).
            // Each part (output, annotations, log) has its own 10-second timeout, so a slow log keeps the parts already read.
            var failed = FailedRequired(marked);
            var outcomes = failed.Count == 0
                ? []
                : await CheckFailureLogs.FanOutAsync(
                    failed,
                    async (check, ct) =>
                    {
                        var fetched = await api.GetCheckFailureAsync(github.Token, github.Owner, github.Repo, check, ct);
                        return (fetched.Detail, fetched.Warnings);
                    },
                    ExternalCheckRules.Excerpt,
                    timeout: null,
                    cancellationToken);
            return new CollectedChecks(marked, decision?.WaitedSeconds, decision?.TimedOut ?? false) { Excerpts = Excerpts(project.Id, outcomes), HeadSha = head };
        }
        catch (GitHubRateLimitedException ex)
        {
            // §4.14: back off until the reset; waiting is never a failed attempt.
            throw new JobDeferredException(
                $"{ErrorCodes.GitHubRateLimited}: GitHub's rate limit is exhausted; review waits for the reset.",
                JobPolicy.DeferUntil(now, ex.ResetAt));
        }
        catch (GitHubApiException ex)
        {
            LogUnavailable(logger, project.Id, ex.Message);
            return payload is null ? throw Unreadable(ex.Message) : null;
        }
    }

    /// <summary>
    /// FR-139: a GitLab or Bitbucket project's statuses on the version branch head (GitLab pipeline jobs and commit statuses,
    /// required when the project requires pipelines to succeed; Bitbucket commit statuses, required when the pull request's
    /// mergeability checks show <c>require_passing_builds_to_merge</c>), waited for like GitHub's checks (30 s re-checks, 15
    /// minutes). A version without a merge/pull request or a pushed branch has none; the host failing is logged and review
    /// goes on without them; a rate limit waits for the reset.
    /// </summary>
    private async Task<CollectedChecks?> HostAsync(Project project, LifecycleJobPayload? payload, bool excerpts, GitHostTarget host, CancellationToken cancellationToken)
    {
        var versionId = project.CurrentReleaseVersionId ?? await db.CurrentReleaseVersionIdAsync(project.Id, cancellationToken);
        var version = versionId is { } id
            ? await db.ReleaseVersions.AsNoTracking().Where(v => v.Id == id).Select(v => new { v.PullRequestNumber, v.BranchName }).FirstOrDefaultAsync(cancellationToken)
            : null;
        if (version is not { PullRequestNumber: not null, BranchName: { } branch })
        {
            return null;
        }

        string? head;
        try
        {
            head = await workingCopies.ResolveCommitAsync(project.Id, $"refs/heads/{branch}", cancellationToken);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            LogUnavailable(logger, project.Id, ex.GetType().Name);
            return null;
        }

        if (head is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        Result<IReadOnlyList<CheckObservation>> statuses;
        try
        {
            statuses = await host.Provider.GetCommitStatusesAsync(host.Connection, head, cancellationToken, version.PullRequestNumber);
        }
        catch (Infrastructure.GitLab.GitLabRateLimitedException ex)
        {
            throw new JobDeferredException(
                $"{ErrorCodes.GitLabRateLimited}: GitLab is rate limiting requests; review waits for the reset.",
                JobPolicy.DeferUntil(now, ex.ResetAt));
        }
        catch (Infrastructure.Bitbucket.BitbucketRateLimitedException ex)
        {
            // §4.19: Bitbucket's 429, or the connection's budget: wait without counting an attempt.
            throw new JobDeferredException(
                $"{ErrorCodes.BitbucketRateLimited}: Bitbucket is rate limiting requests; review waits until {ex.ResetAt:u}.",
                JobPolicy.DeferUntil(now, ex.ResetAt));
        }

        if (statuses.IsFailure)
        {
            LogUnavailable(logger, project.Id, statuses.Error!.Message);
            return payload is null ? throw Unreadable(statuses.Error.Message) : null;
        }

        var checks = statuses.Value;
        var decision = payload is null ? null : Domain.GitHosts.CommitStatusFindings.Wait(checks, payload.ChecksWaitSince, now);
        if (decision is { Wait: true })
        {
            var pending = checks.Count(MergeReadinessRules.IsPending);
            LogWaiting(logger, project.Id, pending);
            throw new JobDeferredException(
                $"Waiting for {pending} pending pipeline job(s) or status(es) on the merge request before scoring (up to 15 minutes).",
                decision.RecheckAt!.Value,
                AideJson.Serialize(payload! with { ChecksWaitSince = decision.WaitingSince }));
        }

        if (decision is { TimedOut: true })
        {
            LogTimedOut(logger, project.Id);
        }

        var source = host.Provider.Kind == GitProvider.Bitbucket ? Domain.GitHosts.CommitStatusHost.Bitbucket : Domain.GitHosts.CommitStatusHost.Gitlab;
        if (payload is null && (!excerpts || RequiredPending(checks)))
        {
            return new CollectedChecks(checks, null, false) { Host = source, HeadSha = head };
        }

        // FR-236: the failed required jobs' traces (GitLab) or failed step logs (Bitbucket Pipelines).
        // The provider reads the connection once, then fetches in parallel over HTTP only (the scoped DbContext isn't shared).
        var failed = FailedRequired(checks);
        IReadOnlyList<CheckFailureOutcome> outcomes = [];
        if (failed.Count > 0)
        {
            try
            {
                var fetched = await host.Provider.GetCheckFailuresAsync(host.Connection, failed, ExternalCheckRules.Excerpt, cancellationToken);
                if (fetched.IsFailure)
                {
                    LogDetailsUnavailable(logger, project.Id, fetched.Error!.Message);
                }

                outcomes = fetched.IsSuccess ? fetched.Value : [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The review never fails because a log fetch failed (a rate limit here doesn't defer either).
                LogDetailsUnavailable(logger, project.Id, ex.GetType().Name);
            }
        }

        return new CollectedChecks(checks, decision?.WaitedSeconds, decision?.TimedOut ?? false) { Host = source, Excerpts = Excerpts(project.Id, outcomes), HeadSha = head };
    }

    private static bool RequiredPending(IReadOnlyList<CheckObservation> checks) => checks.Any(c => c.Required && MergeReadinessRules.IsPending(c));

    /// <summary>The most failed required checks whose details are fetched in one review.</summary>
    public const int MaxDetailedChecks = 10;

    /// <summary>FR-236: the failed required checks whose details are fetched (one per name, at most <see cref="MaxDetailedChecks"/>).</summary>
    private static List<CheckObservation> FailedRequired(IReadOnlyList<CheckObservation> checks) =>
        [.. checks.Where(c => c.Required && MergeReadinessRules.IsFailing(c)).DistinctBy(c => c.Name, StringComparer.Ordinal).Take(MaxDetailedChecks)];

    /// <summary>
    /// FR-236, §8: the excerpts by check name; each warning (a part the host refused, a timeout, any other failure) is logged,
    /// and that check keeps its name and URL evidence. The excerpts are redacted, ANSI-stripped and redacted again (§4.38).
    /// </summary>
    private Dictionary<string, FailureExcerptResult> Excerpts(Guid projectId, IReadOnlyList<CheckFailureOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            foreach (var warning in outcome.Warnings)
            {
                LogDetailSkipped(logger, projectId, outcome.Check.Name, warning);
            }
        }

        return outcomes.Where(o => o.Excerpt is not null).ToDictionary(o => o.Check.Name, o => o.Excerpt!, StringComparer.Ordinal);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review of project {ProjectId} keeps its failed checks without logs: {Reason}")]
    private static partial void LogDetailsUnavailable(ILogger logger, Guid projectId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Review of project {ProjectId} waits for {Pending} pending check(s)")]
    private static partial void LogWaiting(ILogger logger, Guid projectId, int pending);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review of project {ProjectId} stopped waiting for pending checks after 15 minutes")]
    private static partial void LogTimedOut(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review of project {ProjectId} keeps check {CheckName} without its log or details: {Reason}")]
    private static partial void LogDetailSkipped(ILogger logger, Guid projectId, string checkName, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review of project {ProjectId} goes on without the Git host's checks: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, Guid projectId, string reason);
}
