using Aide.Api.Features.Databases;
using Aide.Api.Features.Git;
using Aide.Domain.Entities;
using Aide.Domain.Releases;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Release;

/// <summary>
/// Merge readiness for the production migrations: <paramref name="Known"/> is false when it couldn't be read (an error, a
/// rate limit, GitLab still computing it); <paramref name="Blocked"/> is why it hasn't passed (<see cref="ProductionMigrationRules.BlockingReason"/>).
/// </summary>
public sealed record MigrationReadiness(bool Known, MergeReadinessReasonCode? Blocked, IReadOnlyList<MergeReadinessReasonDto> Reasons);

/// <summary>§4.21: a merge on a head no pre-merge production run succeeded on.</summary>
public sealed record UnmigratedMerge(string? ExpectedHeadSha, string? MergedHeadSha);

/// <summary>
/// §4.21, FR-153: what the release job and the merge poll share about Supabase BaaS production migrations: the head a merge
/// takes, merge readiness, one recorded run, and whether a merge was of a migrated head.
/// </summary>
public sealed partial class ProductionMigrationGate(
    AideDbContext db,
    MergeReadinessService readiness,
    MigrationRunService migrations,
    IWorkingCopies workingCopies,
    ILogger<ProductionMigrationGate> logger)
{
    /// <summary>
    /// The commit a merge takes (as the GitHub, GitLab and Bitbucket providers resolve it): the fetched remote-tracking
    /// branch, else the local branch (Local Git has no remote).
    /// </summary>
    public async Task<string?> HeadAsync(Guid projectId, string branch, CancellationToken cancellationToken) =>
        await workingCopies.ResolveCommitAsync(projectId, $"refs/remotes/{GitRemote.Name}/{branch}", cancellationToken)
        ?? await workingCopies.ResolveCommitAsync(projectId, $"refs/heads/{branch}", cancellationToken);

    /// <summary>Merge readiness now. A failed read or a rate limit is unknown readiness, never an exception.</summary>
    public async Task<MigrationReadiness> ReadinessAsync(Guid projectId, CancellationToken cancellationToken)
    {
        try
        {
            var computed = await readiness.ComputeAsync(projectId, cancellationToken);
            if (computed.IsFailure)
            {
                return new MigrationReadiness(false, null, []);
            }

            var value = computed.Value;
            var provider = await db.ProjectGitSettings.AsNoTracking()
                .Where(s => s.ProjectId == projectId)
                .Select(s => (GitProvider?)s.Provider)
                .FirstOrDefaultAsync(cancellationToken);

            // §4.19: GitLab still computing the status says nothing about the merge request (its checks_pending isn't a verdict).
            if (provider is { } host && ProductionMigrationRules.ReadinessUnknown(host, value.MergeStateStatus))
            {
                return new MigrationReadiness(false, null, value.Reasons);
            }

            var reviewUnknown = provider is { } kind && ProductionMigrationRules.ReviewUnknown(kind, value.MergeStateStatus, value.ReviewDecision);
            return new MigrationReadiness(true, ProductionMigrationRules.BlockingReason(value.Reasons.Select(r => r.Code), reviewUnknown), value.Reasons);
        }
        catch (Exception ex) when (ex is Infrastructure.GitHub.GitHubRateLimitedException or Infrastructure.GitLab.GitLabRateLimitedException
            or Infrastructure.Bitbucket.BitbucketRateLimitedException)
        {
            LogRateLimited(logger, projectId);
            return new MigrationReadiness(false, null, []);
        }
    }

    /// <summary>
    /// Runs the migrations of <paramref name="head"/> on the production database and records the run on the attempt
    /// (<c>releases.migration_run_id</c>: its newest pre-merge run). <c>null</c>: there is no database to migrate any more.
    /// The attempt's status isn't touched: the caller decides what a failed run means.
    /// </summary>
    public async Task<DatabaseMigrationRun?> RunAsync(Domain.Entities.Release release, string head, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        DatabaseMigrationRun run;
        try
        {
            run = await migrations.RunAsync(release.ProjectId, DeploymentEnvironment.Production, release.ReleaseVersionId, release.Id, head, cancellationToken);
        }
        catch (MigrationTargetMissingException)
        {
            return null;
        }

        await db.Releases
            .Where(r => r.Id == release.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.MigrationRunId, run.Id), cancellationToken);
        db.ChangeTracker.Clear();
        return run;
    }

    /// <summary>
    /// §4.21: the merge's facts when no pre-merge run of the version succeeded on the merged head (logged as a warning); the
    /// caller records them with the release's completion. <c>null</c> when it was migrated, or the project has no migration
    /// step. <paramref name="mergedHead"/> is the head the host reports, else the fetched branch.
    /// </summary>
    public async Task<UnmigratedMerge?> UnmigratedMergeAsync(Domain.Entities.Release release, string? mergedHead, string branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!await migrations.HasMigrationStepAsync(release.ProjectId, DeploymentEnvironment.Production, cancellationToken))
        {
            return null;
        }

        var head = mergedHead ?? await HeadAsync(release.ProjectId, branch, cancellationToken);
        var migrated = await db.DatabaseMigrationRuns.AsNoTracking()
            .Where(r => r.ReleaseVersionId == release.ReleaseVersionId && r.ReleaseId != null
                && r.Environment == DeploymentEnvironment.Production && r.Status == MigrationRunStatus.Succeeded)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => r.GitSha)
            .ToListAsync(cancellationToken);
        if (!ProductionMigrationRules.MergedUnmigrated(head, migrated))
        {
            return null;
        }

        var expected = migrated.Count > 0 ? migrated[0] : null;
        LogUnmigratedMerge(logger, release.ProjectId, head, expected);
        return new UnmigratedMerge(expected, head);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Merge readiness of project {ProjectId} was rate limited; it counts as unknown for the production migrations")]
    private static partial void LogRateLimited(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: head {MergedHead} was merged without pre-merge production migrations (last migrated head: {ExpectedHead}); they first run in the production rollout")]
    private static partial void LogUnmigratedMerge(ILogger logger, Guid projectId, string? mergedHead, string? expectedHead);
}
