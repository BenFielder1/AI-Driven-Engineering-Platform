using Aide.Api.Features.Git;
using Aide.Api.Jobs;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Releases;
using Aide.Domain.Review;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Review;

/// <summary>The PR head's checks as review used them (FR-85), how long it waited and whether it gave up waiting.</summary>
public sealed record CollectedChecks(IReadOnlyList<CheckObservation> Checks, int? WaitedSeconds, bool TimedOut)
{
    /// <summary>The <c>compliance_report.external_checks</c> JSON (the <see cref="ExternalChecksDto"/> shape; no preview yet).</summary>
    public string ToJson() => AideJson.Serialize(new
    {
        checks = Checks.Select(c => new ExternalCheckDto(c.Name, c.Status == CheckRunStatus.Completed ? c.Conclusion : null, c.Required, c.DetailsUrl)),
        checksWaitedSeconds = WaitedSeconds,
        checksTimedOut = TimedOut,
        preview = (object?)null,
    });
}

/// <summary>
/// FR-85: before review scoring, the check runs and commit statuses on the version PR's head (Checks / Commit statuses:
/// read), marked required from the default branch's rules. Pending checks are waited for: the review job defers itself every
/// 30 seconds (not a blocking sleep, not a counted attempt; when waiting started is kept in the job payload) for up to 15
/// minutes, then review proceeds with <c>checksTimedOut</c>. Local projects, a version without a PR, or checks GitHub won't
/// show (no permission) give <c>null</c> without waiting; GitHub failing is logged and review goes on without them.
/// </summary>
public sealed partial class ExternalChecksCollector(
    AideDbContext db,
    GitHubRepoAccess access,
    GitHubApi api,
    TimeProvider time,
    ILogger<ExternalChecksCollector> logger)
{
    /// <summary>The checks, or <c>null</c>; throws <see cref="JobDeferredException"/> while waiting for pending checks.</summary>
    public async Task<CollectedChecks?> CollectAsync(Project project, LifecycleJobPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(payload);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Review of project {ProjectId} waits for {Pending} pending check(s)")]
    private static partial void LogWaiting(ILogger logger, Guid projectId, int pending);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review of project {ProjectId} stopped waiting for pending checks after 15 minutes")]
    private static partial void LogTimedOut(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review of project {ProjectId} goes on without GitHub checks: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, Guid projectId, string reason);
}
