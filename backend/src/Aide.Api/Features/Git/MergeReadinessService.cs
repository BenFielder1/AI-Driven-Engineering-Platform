using System.Collections.Concurrent;
using System.Text.Json;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Releases;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aide.Api.Features.Git;

/// <summary>
/// FR-84: merge readiness of the version's PR. GitHub: the token's permission and the allowed merge methods
/// (<c>GET /repos/{o}/{r}</c>), the default branch's rules (<c>GET /rules/branches/{default}</c>: required approvals and
/// status checks), the PR's <c>mergeStateStatus</c> and <c>reviewDecision</c> (GraphQL; without GraphQL for the token it
/// degrades: logged, and required approvals count as missing) and the check runs and commit statuses on the PR head;
/// evaluated by <see cref="MergeReadinessRules.Evaluate"/>. GitLab (FR-138): through the project's provider
/// (<see cref="GitHostTargets"/>). Local: AIDE can merge unless a dry-run merge conflicts. Each
/// check is stored as <c>MERGE_READINESS_CHECKED</c>; reads return the latest one while it is fresh
/// (<see cref="FreshFor"/>).
/// </summary>
public sealed partial class MergeReadinessService(
    AideDbContext db,
    GitHubRepoAccess access,
    GitHostTargets hosts,
    GitHubApi api,
    IWorkingCopies workingCopies,
    IOptions<GitHubGitOptions> gitOptions,
    MergeReadinessThrottle throttle,
    TimeProvider time,
    ILogger<MergeReadinessService> logger)
{
    /// <summary>How long a stored readiness check is returned without asking GitHub again.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The latest readiness of the project's version in development while it is fresh, else (or with
    /// <paramref name="refresh"/>) a new check (stored). <c>WRONG_PHASE</c> outside review and release; <c>NOT_FOUND</c>.
    /// </summary>
    public async Task<Result<MergeReadinessDto>> GetAsync(Guid projectId, bool refresh, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<MergeReadinessDto>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        var allowed = MergeReadinessRules.CanCheck(project.CurrentPhase);
        if (allowed.IsFailure)
        {
            return Result.Failure<MergeReadinessDto>(allowed.Error!);
        }

        // Any member can ask for refresh=true: within 30 seconds of the last check the result is reused, so GitHub is asked
        // at most once per 30 seconds per project (for the same version and merge strategy).
        if (throttle.TryGet(projectId, project.CurrentReleaseVersionId, project.MergeStrategy, time.GetUtcNow(), out var recent))
        {
            return Result.Success(recent);
        }

        // A stored result is reused while fresh and still for the current merge strategy (its reasons depend on it).
        if (!refresh && await LatestAsync(project, cancellationToken) is { } stored
            && stored.MergeStrategy == project.MergeStrategy && time.GetUtcNow() - stored.CheckedAt < FreshFor)
        {
            return Result.Success(stored);
        }

        return await CheckAsync(projectId, cancellationToken);
    }

    /// <summary>
    /// Checks readiness now and stores it (<c>MERGE_READINESS_CHECKED</c>, saved) unless it is the same as the last stored
    /// result (apart from <c>checkedAt</c>). Failures are token-free.
    /// </summary>
    public async Task<Result<MergeReadinessDto>> CheckAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var computed = await ComputeAsync(projectId, cancellationToken);
        if (computed.IsFailure)
        {
            return computed;
        }

        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, cancellationToken);
        throttle.Record(projectId, project.CurrentReleaseVersionId, computed.Value);
        if (await LatestAsync(project, cancellationToken) is { } last && SameResult(last, computed.Value))
        {
            return computed;
        }

        db.AddEvent(
            EventTypes.MergeReadinessChecked,
            projectId,
            new { releaseVersionId = project.CurrentReleaseVersionId, readiness = computed.Value, provider = await Release.ReleaseRecords.ProviderAsync(db, projectId, cancellationToken) },
            SystemActor.Name,
            computed.Value.CheckedAt);
        await db.SaveChangesAsync(cancellationToken);
        return computed;
    }

    /// <summary>Computes readiness without storing it (the release job records the reasons on the attempt, FR-65).</summary>
    public async Task<Result<MergeReadinessDto>> ComputeAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<MergeReadinessDto>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        var now = time.GetUtcNow();
        var versionId = project.CurrentReleaseVersionId ?? await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
        var version = versionId is { } id
            ? await db.ReleaseVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, cancellationToken)
            : null;
        if (await hosts.ResolveAsync(projectId, cancellationToken) is { } host)
        {
            try
            {
                return await HostAsync(project, version, host, now, cancellationToken);
            }
            catch (Exception ex) when (ex is Infrastructure.GitLab.GitLabApiException or HttpRequestException or System.Text.Json.JsonException)
            {
                // FR-138: a GitLab answer the provider didn't turn into a result is an upstream failure (502), never a 500.
                LogHostFailed(logger, projectId, ex.GetType().Name);
                return Result.Failure<MergeReadinessDto>(new DomainError(ErrorCodes.UpstreamError, "GitLab failed or returned an unreadable answer. Try again later."));
            }
        }

        var target = await access.ResolveAsync(projectId, cancellationToken);
        if (target.IsFailure)
        {
            return Result.Failure<MergeReadinessDto>(target.Error!);
        }

        if (target.Value is not { } github)
        {
            return Result.Success(await LocalAsync(project, version, now, cancellationToken));
        }

        try
        {
            return Result.Success(await GitHubAsync(project, version, github, now, cancellationToken));
        }
        catch (GitHubApiException ex)
        {
            LogUpstream(logger, projectId, ex.Status);
            return Result.Failure<MergeReadinessDto>(new DomainError(ErrorCodes.UpstreamError, $"GitHub failed or couldn't be reached. {ex.Message}"));
        }
    }

    private async Task<MergeReadinessDto> GitHubAsync(Project project, ReleaseVersion? version, GitHubRepoTarget github, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var facts = new MergeReadinessFacts { MergeMethod = gitOptions.Value.ReleaseMergeMethod };
        GitHubPullSnapshot? pull = version?.PullRequestNumber is { } number
            ? await api.FindPullAsync(github.Token, github.Owner, github.Repo, number, cancellationToken)
            : null;
        if (pull is null || !pull.IsOpen)
        {
            var closed = MergeReadinessRules.Evaluate(facts with { PullRequestOpen = false }, project.MergeStrategy);
            return Dto(closed, project.MergeStrategy, [], null, null, now);
        }

        var repo = await api.GetRepoMergeSettingsAsync(github.Token, github.Owner, github.Repo, cancellationToken);
        var rules = await api.GetBranchRulesAsync(github.Token, github.Owner, github.Repo, github.DefaultBranch, cancellationToken);
        var graph = await api.GetPullGraphAsync(github.Token, github.Owner, github.Repo, pull.Number, cancellationToken);
        if (graph is null)
        {
            // §11: GraphQL may be unavailable for the token; readiness degrades instead of failing.
            LogGraphQlUnavailable(logger, project.Id);
        }

        var checks = pull.HeadSha is { } head
            ? await api.ListChecksAsync(github.Token, github.Owner, github.Repo, head, cancellationToken) ?? []
            : [];
        var marked = MergeReadinessRules.MarkRequired(checks, rules.RequiredChecks, includeMissingRequired: true);
        facts = facts with
        {
            CanWrite = github.InstallationCanMerge ?? repo?.CanPush,
            AllowedMergeMethods = repo?.AllowedMergeMethods,
            RequiredApprovingReviews = rules.RequiredApprovingReviews,
            MergeStateStatus = graph?.MergeStateStatus,
            ReviewDecision = graph?.ReviewDecision,
            GraphQlAvailable = graph is not null,
            HasConflicts = pull.Mergeable == false,
            Checks = marked,
        };
        var verdict = MergeReadinessRules.Evaluate(facts, project.MergeStrategy);
        LogChecked(logger, project.Id, verdict.CanMerge, verdict.Reasons.Count);
        return Dto(verdict, project.MergeStrategy, marked, graph?.MergeStateStatus, graph?.ReviewDecision, now);
    }

    /// <summary>
    /// FR-138: GitLab or Bitbucket readiness through the project's provider (the binding connection). GitLab: the merge
    /// request's <c>detailed_merge_status</c>, approvals, branch protection, access level and head pipeline, with the head's
    /// CI statuses as checks; <c>mergeStateStatus</c> carries GitLab's <c>detailed_merge_status</c>. Bitbucket: the single
    /// mergeability-checks call (§4.19 budget; no checks or status listed). <c>reviewDecision</c> is <c>null</c>. Without a
    /// recorded merge/pull request it is <c>pr_not_open</c>.
    /// </summary>
    private async Task<Result<MergeReadinessDto>> HostAsync(Project project, ReleaseVersion? version, GitHostTarget host, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (version?.PullRequestNumber is not { } number)
        {
            var closed = host.Provider.Kind == GitProvider.Bitbucket
                ? Domain.GitHosts.MergeabilityChecks.Map([], project.MergeStrategy, pullRequestOpen: false)
                : Domain.GitHosts.GitLabMergeReadiness.Map(new Domain.GitHosts.GitLabMergeFacts { Open = false }, project.MergeStrategy);
            return Result.Success(Dto(closed.Verdict!, project.MergeStrategy, [], null, null, now));
        }

        // FR-138: once a release attempt of this version awaited its merge, AIDE has marked the draft ready: a draft blocks again.
        var markedReady = await db.Releases.AsNoTracking().AnyAsync(r => r.ReleaseVersionId == version.Id && r.AwaitingMergeSince != null, cancellationToken);
        var readiness = await host.Provider.GetMergeReadinessAsync(host.Connection, number, project.MergeStrategy, cancellationToken, markedReady);
        if (readiness.IsFailure)
        {
            LogHostFailed(logger, project.Id, readiness.Error!.Code);
            return Result.Failure<MergeReadinessDto>(readiness.Error);
        }

        var verdict = readiness.Value.Verdict ?? Domain.GitHosts.GitLabMergeReadiness.StillChecking(project.MergeStrategy);
        LogChecked(logger, project.Id, verdict.CanMerge, verdict.Reasons.Count);
        return Result.Success(Dto(verdict, project.MergeStrategy, readiness.Value.Checks, readiness.Value.HostStatus, null, now));
    }

    /// <summary>Local (D12): AIDE can merge unless a dry-run merge of the version branch into the default branch conflicts.</summary>
    private async Task<MergeReadinessDto> LocalAsync(Project project, ReleaseVersion? version, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var conflicts = false;
        var settings = await db.ProjectGitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == project.Id, cancellationToken);
        if (settings is not null && version?.BranchName is { } branch)
        {
            try
            {
                if (await workingCopies.ResolveCommitAsync(project.Id, $"refs/heads/{branch}", cancellationToken) is not null)
                {
                    var files = await workingCopies.TrialMergeConflictsAsync(project.Id, $"refs/heads/{settings.DefaultBranch}", $"refs/heads/{branch}", cancellationToken);
                    conflicts = files.Count > 0;
                }
            }
            catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
            {
                // The merge itself stays authoritative; an unreadable working copy says nothing about conflicts.
                LogLocalCheckFailed(logger, project.Id, ex.GetType().Name);
            }
        }

        var verdict = MergeReadinessRules.Evaluate(new MergeReadinessFacts { HasConflicts = conflicts }, project.MergeStrategy);
        return Dto(verdict, project.MergeStrategy, [], null, null, now);
    }

    /// <summary>The latest stored readiness of the project's current version, or <c>null</c>.</summary>
    private async Task<MergeReadinessDto?> LatestAsync(Project project, CancellationToken cancellationToken)
    {
        var payload = await db.Events.AsNoTracking()
            .Where(e => e.ProjectId == project.Id && e.Type == EventTypes.MergeReadinessChecked)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => e.Payload)
            .FirstOrDefaultAsync(cancellationToken);
        if (payload is null)
        {
            return null;
        }

        try
        {
            var stored = AideJson.Deserialize<StoredReadiness>(payload);
            return stored.ReleaseVersionId == project.CurrentReleaseVersionId ? stored.Readiness : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MergeReadinessDto Dto(
        MergeReadinessVerdict verdict, MergeStrategy strategy, IReadOnlyList<CheckObservation> checks, string? mergeStateStatus, string? reviewDecision, DateTimeOffset now) =>
        new(
            verdict.CanMerge,
            verdict.WillAutoMerge,
            strategy,
            [.. verdict.Reasons.Select(r => new MergeReadinessReasonDto(r.Code, r.Message))],
            [.. checks.Select(c => new CheckRunDto(c.Name, c.Status, c.Conclusion, c.Required, c.DetailsUrl))],
            mergeStateStatus,
            reviewDecision,
            now);

    private sealed record StoredReadiness(Guid? ReleaseVersionId, MergeReadinessDto? Readiness);

    /// <summary>The same readiness apart from when it was checked.</summary>
    internal static bool SameResult(MergeReadinessDto left, MergeReadinessDto right) =>
        left.CanMerge == right.CanMerge
        && left.WillAutoMerge == right.WillAutoMerge
        && left.MergeStrategy == right.MergeStrategy
        && left.MergeStateStatus == right.MergeStateStatus
        && left.ReviewDecision == right.ReviewDecision
        && left.Reasons.SequenceEqual(right.Reasons)
        && left.Checks.SequenceEqual(right.Checks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Merge readiness of project {ProjectId}: canMerge={CanMerge} with {ReasonCount} reasons")]
    private static partial void LogChecked(ILogger logger, Guid projectId, bool canMerge, int reasonCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GraphQL is unavailable for project {ProjectId}'s GitHub token; merge readiness is read without mergeStateStatus and reviewDecision")]
    private static partial void LogGraphQlUnavailable(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Merge readiness of project {ProjectId}: GitHub failed with HTTP {Status}")]
    private static partial void LogUpstream(ILogger logger, Guid projectId, int? status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Merge readiness of project {ProjectId} couldn't be read from its Git host ({ErrorCode})")]
    private static partial void LogHostFailed(ILogger logger, Guid projectId, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Merge readiness of Local project {ProjectId}: the dry-run merge failed ({ErrorType})")]
    private static partial void LogLocalCheckFailed(ILogger logger, Guid projectId, string errorType);
}

/// <summary>
/// FR-84: the last readiness check per project in this process, so <c>refresh=true</c> asks GitHub at most once per
/// <see cref="Window"/> (for the same release version and merge strategy). Singleton; lost on restart (then the stored
/// result applies).
/// </summary>
public sealed class MergeReadinessThrottle
{
    /// <summary>How long a check is reused even with <c>refresh=true</c>.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, (Guid? VersionId, MergeReadinessDto Readiness)> _last = new();

    public bool TryGet(Guid projectId, Guid? versionId, MergeStrategy strategy, DateTimeOffset now, out MergeReadinessDto readiness)
    {
        if (_last.TryGetValue(projectId, out var last) && last.VersionId == versionId
            && last.Readiness.MergeStrategy == strategy && now - last.Readiness.CheckedAt < Window)
        {
            readiness = last.Readiness;
            return true;
        }

        readiness = null!;
        return false;
    }

    public void Record(Guid projectId, Guid? versionId, MergeReadinessDto readiness) => _last[projectId] = (versionId, readiness);

    /// <summary>Drops the project's last check (tests; a new version starts fresh anyway).</summary>
    public void Forget(Guid projectId) => _last.TryRemove(projectId, out _);
}
