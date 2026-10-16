using Aide.Api.Features.Execution;
using Aide.Domain.Agents;
using Aide.Domain.Entities;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.RepoTools;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Agents;

/// <summary>Which repository an agent's read-only repo tools see (FR-71).</summary>
public enum RepoScope
{
    /// <summary>No repo tools (the agent isn't one of the six, or doesn't need them).</summary>
    None = 0,

    /// <summary>The release version's base commit: Repository Analyst, refinement, spec and plan.</summary>
    BaseCommit = 1,

    /// <summary>The project branch's working tree: code generation and review.</summary>
    WorkingTree = 2,
}

/// <summary>
/// An opened repo context: the snapshot (dispose it when the run is done) and the tools over it, or none, with the
/// warning to record when a snapshot should have existed but couldn't be made.
/// </summary>
public sealed class RepoToolContext : IDisposable
{
    public static RepoToolContext Empty { get; } = new(null, null);

    private readonly RepoSnapshot? _snapshot;

    public RepoToolContext(RepoSnapshot? snapshot, string? warning, TimeProvider? time = null)
    {
        _snapshot = snapshot;
        Warning = warning;
        Tools = snapshot is null ? null : new RepoToolSession(snapshot.Root, time);
    }

    public RepoToolSession? Tools { get; }

    public string? Warning { get; }

    public void Dispose() => _snapshot?.Dispose();
}

/// <summary>
/// FR-71, D-V2-9: opens the credential-free snapshot an agent run's repo tools work on.
/// <list type="bullet">
/// <item><see cref="RepoScope.BaseCommit"/>: the current release version's <c>base_commit_sha</c>. No base commit means the
/// version has no existing code (a new project): no tools and no warning. A GitHub commit not yet in the working copy is
/// fetched by preparing the working copy through the project's provider first (the branch name is not recorded: that is
/// done when execution prepares it).</item>
/// <item><see cref="RepoScope.WorkingTree"/>: the project branch as checked out in the working copy.</item>
/// </list>
/// A snapshot that can't be made gives no tools and the <c>snapshot_unavailable</c> warning; the run goes on context-only.
/// </summary>
public sealed partial class AgentRepoContext(
    AideDbContext db,
    IRepoSnapshotProvider snapshots,
    IGitProviderResolver providers,
    ISecretProtector secrets,
    TimeProvider time,
    ILogger<AgentRepoContext> logger)
{
    public async Task<RepoToolContext> OpenAsync(Guid projectId, RepoScope scope, CancellationToken cancellationToken)
    {
        switch (scope)
        {
            case RepoScope.WorkingTree:
                return Opened(await snapshots.ForWorkingTreeAsync(projectId, cancellationToken), projectId);
            case RepoScope.BaseCommit:
                var versionId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
                var baseCommit = versionId is { } id
                    ? await db.ReleaseVersions.AsNoTracking().Where(v => v.Id == id).Select(v => v.BaseCommitSha).FirstOrDefaultAsync(cancellationToken)
                    : null;
                if (string.IsNullOrWhiteSpace(baseCommit))
                {
                    return RepoToolContext.Empty;
                }

                var outcome = await snapshots.ForCommitAsync(projectId, baseCommit, null, cancellationToken);
                if (!outcome.Ok && await FetchAsync(projectId, cancellationToken))
                {
                    outcome = await snapshots.ForCommitAsync(projectId, baseCommit, null, cancellationToken);
                }

                return Opened(outcome, projectId);
            default:
                return RepoToolContext.Empty;
        }
    }

    private RepoToolContext Opened(RepoSnapshotOutcome outcome, Guid projectId)
    {
        if (outcome.Ok)
        {
            try
            {
                return new RepoToolContext(outcome.Snapshot, null, time);
            }
            catch (DirectoryNotFoundException)
            {
                // The snapshot folder vanished (e.g. removed by hand): run without tools rather than fail the run.
                outcome.Snapshot!.Dispose();
                outcome = RepoSnapshotOutcome.Failed("The snapshot folder no longer exists.");
            }
        }

        LogUnavailable(logger, projectId, outcome.Reason);
        return new RepoToolContext(null, AgentRunWarnings.SnapshotUnavailable);
    }

    /// <summary>GitHub projects: clone or fetch the repository into the working copy (Local repositories are already there).</summary>
    private async Task<bool> FetchAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var settings = await db.ProjectGitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);
        if (settings is null || settings.Provider == GitProvider.Local)
        {
            return false;
        }

        string? token = null;
        if (settings.TokenEncrypted is { } encrypted && !secrets.TryUnprotect(encrypted, out token))
        {
            return false;
        }

        var project = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Slug, p.DevelopmentVersion, p.CurrentReleaseVersionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            return false;
        }

        var recorded = project.CurrentReleaseVersionId is { } versionId
            ? await db.ReleaseVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => v.BranchName).FirstOrDefaultAsync(cancellationToken)
            : null;
        var branch = WorkingCopyPreparer.BranchFor(recorded, project.Slug, projectId, project.DevelopmentVersion);
        var prepared = await providers.Resolve(settings.Provider)
            .PrepareWorkingCopyAsync(GitConnectionSettings.From(settings), branch, token, cancellationToken);
        return prepared.Ok;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No repo tool snapshot for project {ProjectId}: {Reason}; the run continues without repo tools")]
    private static partial void LogUnavailable(ILogger logger, Guid projectId, string? reason);
}
