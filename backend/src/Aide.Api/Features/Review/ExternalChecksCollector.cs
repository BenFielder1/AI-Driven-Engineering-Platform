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
    /// The review findings: FR-139 for a GitLab project ("required pipeline job test:backend failed", major when required,
    /// else minor), FR-85 for GitHub ("required check ci / test failed").
    /// </summary>
    public IReadOnlyList<ReviewFinding> Findings() =>
        Host is { } host ? Domain.GitHosts.CommitStatusFindings.Findings(Checks, host) : ExternalCheckRules.Findings(Checks);

    /// <summary>The <c>compliance_report.external_checks</c> JSON (the <see cref="ExternalChecksDto"/> shape) without a preview.</summary>
    public string ToJson() => Compose(this, null)!;

    /// <summary>
    /// The <c>compliance_report.external_checks</c> JSON (the <see cref="ExternalChecksDto"/> shape): the PR head's checks
    /// (FR-85) and the preview deployment review waited for (FR-90); <c>null</c> when there is neither.
    /// </summary>
    public static string? Compose(CollectedChecks? checks, Deployments.PreviewCheckDto? preview) =>
        checks is null && preview is null
            ? null
            : AideJson.Serialize(new
            {
                checks = (checks?.Checks ?? []).Select(c => new ExternalCheckDto(c.Name, c.Status == CheckRunStatus.Completed ? c.Conclusion : null, c.Required, c.DetailsUrl)),
                checksWaitedSeconds = checks?.WaitedSeconds,
                checksTimedOut = checks?.TimedOut ?? false,
                preview,
            });
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
    public async Task<CollectedChecks?> CollectAsync(Project project, LifecycleJobPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(payload);
        if (await hosts.ResolveAsync(project.Id, cancellationToken) is { } host)
        {
            return await HostAsync(project, payload, host, cancellationToken);
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
            if (await api.FindPullAsync(github.Token, github.Owner, github.Repo, pullNumber, cancellationToken) is not { HeadSha: { } head }
                || await api.ListChecksAsync(github.Token, github.Owner, github.Repo, head, cancellationToken) is not { } checks)
            {
                return null;
            }

            var rules = await api.GetBranchRulesAsync(github.Token, github.Owner, github.Repo, github.DefaultBranch, cancellationToken);
            var marked = MergeReadinessRules.MarkRequired(checks, rules.RequiredChecks);
            var decision = ExternalCheckRules.Decide(marked, payload.ChecksWaitSince, now);
            if (decision.Wait)
            {
                var pending = marked.Count(MergeReadinessRules.IsPending);
                LogWaiting(logger, project.Id, pending);
                throw new JobDeferredException(
                    $"Waiting for {pending} pending check(s) on the pull request before scoring (up to 15 minutes).",
                    decision.RecheckAt!.Value,
                    AideJson.Serialize(payload with { ChecksWaitSince = decision.WaitingSince }));
            }

            if (decision.TimedOut)
            {
                LogTimedOut(logger, project.Id);
            }

            return new CollectedChecks(marked, decision.WaitedSeconds, decision.TimedOut);
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
            return null;
        }
    }

    /// <summary>
    /// FR-139: a GitLab or Bitbucket project's statuses on the version branch head (GitLab pipeline jobs and commit statuses,
    /// required when the project requires pipelines to succeed; Bitbucket commit statuses, required when the pull request's
    /// mergeability checks show <c>require_passing_builds_to_merge</c>), waited for like GitHub's checks (30 s re-checks, 15
    /// minutes). A version without a merge/pull request or a pushed branch has none; the host failing is logged and review
    /// goes on without them; a rate limit waits for the reset.
    /// </summary>
    private async Task<CollectedChecks?> HostAsync(Project project, LifecycleJobPayload payload, GitHostTarget host, CancellationToken cancellationToken)
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
            return null;
        }

        var checks = statuses.Value;
        var decision = Domain.GitHosts.CommitStatusFindings.Wait(checks, payload.ChecksWaitSince, now);
        if (decision.Wait)
        {
            var pending = checks.Count(MergeReadinessRules.IsPending);
            LogWaiting(logger, project.Id, pending);
            throw new JobDeferredException(
                $"Waiting for {pending} pending pipeline job(s) or status(es) on the merge request before scoring (up to 15 minutes).",
                decision.RecheckAt!.Value,
                AideJson.Serialize(payload with { ChecksWaitSince = decision.WaitingSince }));
        }

        if (decision.TimedOut)
        {
            LogTimedOut(logger, project.Id);
        }

        var source = host.Provider.Kind == GitProvider.Bitbucket ? Domain.GitHosts.CommitStatusHost.Bitbucket : Domain.GitHosts.CommitStatusHost.Gitlab;
        return new CollectedChecks(checks, decision.WaitedSeconds, decision.TimedOut) { Host = source };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Review of project {ProjectId} waits for {Pending} pending check(s)")]
    private static partial void LogWaiting(ILogger logger, Guid projectId, int pending);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review of project {ProjectId} stopped waiting for pending checks after 15 minutes")]
    private static partial void LogTimedOut(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review of project {ProjectId} goes on without the Git host's checks: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, Guid projectId, string reason);
}
