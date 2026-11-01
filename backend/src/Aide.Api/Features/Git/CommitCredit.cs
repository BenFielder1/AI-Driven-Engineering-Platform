using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.GitHosts;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Git;

/// <summary>
/// FR-174–FR-176, §4.25: the one path from a commit, merge or tag to its <c>Co-authored-by</c> line: reads the candidates and
/// their identities, then asks the pure <see cref="CommitAttribution"/>. A failure here never fails the commit: it is logged
/// and the commit is made without a trailer.
/// </summary>
public sealed partial class CommitCredit(AideDbContext db, ISecretProtectors protectors, ILogger<CommitCredit> logger)
{
    private ISecretProtector EmailProtector => protectors.ForPurpose(SecretPurposes.GitHostCommitEmail);

    /// <summary>§4.25 step commit (also optimisation steps, which run on a plan of the same version).</summary>
    public Task<string?> ForStepAsync(Guid projectId, Guid planId, CancellationToken cancellationToken) =>
        SafelyAsync(projectId, async () =>
        {
            var settings = await SettingsAsync(projectId, cancellationToken);
            if (!MayCredit(settings))
            {
                return null;
            }

            var versionId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
            var approver = await db.ApprovalGates.AsNoTracking()
                .Where(g => g.ProjectId == projectId && g.ReleaseVersionId == versionId && g.Type == GateType.PlanApproval
                    && g.Status == GateStatus.Approved && (g.ArtifactId == planId || g.ArtifactId == null))
                .OrderByDescending(g => g.DecidedAt)
                .Select(g => g.ReviewerId)
                .FirstOrDefaultAsync(cancellationToken);
            var executionActor = await db.StateTransitions.AsNoTracking()
                .Where(t => t.ProjectId == projectId && t.ReleaseVersionId == versionId && t.ToPhase == Phase.Execution)
                .OrderByDescending(t => t.CreatedAt)
                .ThenByDescending(t => t.Id)
                .Select(t => t.Actor)
                .FirstOrDefaultAsync(cancellationToken);
            var startedBy = await db.ReleaseVersions.AsNoTracking()
                .Where(v => v.Id == versionId)
                .Select(v => v.StartedBy)
                .FirstOrDefaultAsync(cancellationToken);
            var candidates = CommitAttribution.StepCandidates(
                approver, executionActor, startedBy, settings!.BoundByUserId, await CreatorActorAsync(projectId, cancellationToken));
            return await ResolveAsync(settings, candidates, cancellationToken);
        }, cancellationToken);

    /// <summary>§4.25 release merge and tag.</summary>
    public Task<string?> ForReleaseAsync(Domain.Entities.Release release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        return SafelyAsync(release.ProjectId, async () =>
        {
            var settings = await SettingsAsync(release.ProjectId, cancellationToken);
            if (!MayCredit(settings))
            {
                return null;
            }

            var approver = await db.ApprovalGates.AsNoTracking()
                .Where(g => g.ProjectId == release.ProjectId && g.ReleaseVersionId == release.ReleaseVersionId
                    && g.Type == GateType.PreRelease && g.Status == GateStatus.Approved)
                .OrderByDescending(g => g.DecidedAt)
                .Select(g => g.ReviewerId)
                .FirstOrDefaultAsync(cancellationToken);

            // RELEASE_STARTED is written with the attempt, in one transaction, so the first one since it was created is its own.
            var starter = await db.Events.AsNoTracking()
                .Where(e => e.ProjectId == release.ProjectId && e.Type == EventTypes.ReleaseStarted && e.CreatedAt >= release.CreatedAt)
                .OrderBy(e => e.CreatedAt)
                .ThenBy(e => e.Id)
                .Select(e => e.Actor)
                .FirstOrDefaultAsync(cancellationToken);
            var candidates = CommitAttribution.ReleaseCandidates(
                approver, starter, settings!.BoundByUserId, await CreatorActorAsync(release.ProjectId, cancellationToken));
            return await ResolveAsync(settings, candidates, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// §4.25 initial commit of a repository AIDE creates on Bitbucket (FR-135), before any project exists: the person creating
    /// it is the project creator-to-be, and there is no project setting yet (it defaults to on).
    /// </summary>
    public Task<string?> ForBitbucketInitialCommitAsync(Guid userId, Guid connectionId, CancellationToken cancellationToken) =>
        SafelyAsync(null, async () =>
        {
            var instanceId = await db.GitConnections.AsNoTracking()
                .Where(c => c.Id == connectionId && c.UserId == userId && c.Provider == GitHostKind.Bitbucket)
                .Select(c => (Guid?)c.InstanceId)
                .FirstOrDefaultAsync(cancellationToken);
            var settings = new ProjectGitSettings { Provider = GitProvider.Bitbucket, InstanceId = instanceId };
            return await ResolveAsync(settings, CommitAttribution.InitialCandidates(userId), cancellationToken);
        }, cancellationToken);

    private static bool MayCredit(ProjectGitSettings? settings) =>
        settings is { CommitCreditEnabled: true, Provider: not GitProvider.Local };

    private Task<ProjectGitSettings?> SettingsAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.ProjectGitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);

    private Task<string?> CreatorActorAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Type == EventTypes.ProjectCreated)
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Actor)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<string?> ResolveAsync(ProjectGitSettings settings, IReadOnlyList<Guid?> candidates, CancellationToken cancellationToken)
    {
        var ids = candidates.OfType<Guid>().Distinct().ToList();
        var instanceUrl = settings.Provider == GitProvider.Gitlab && settings.InstanceId is { } gitlab
            ? await db.GitHostInstances.AsNoTracking().Where(i => i.Id == gitlab).Select(i => i.BaseUrl).FirstOrDefaultAsync(cancellationToken)
            : null;
        var host = CommitHost.From(settings.Provider, instanceUrl);
        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Name, u.CommitCreditOptOut })
            .ToListAsync(cancellationToken);
        var identities = await IdentitiesAsync(host.Kind, settings.InstanceId, ids, cancellationToken);
        var people = users.ToDictionary(
            u => u.Id,
            u => new CommitPerson(u.Name, u.CommitCreditOptOut, identities.GetValueOrDefault(u.Id)));
        return CommitAttribution.Resolve(new CommitCreditContext(true, host, candidates, people))?.CoAuthorLine;
    }

    /// <summary>FR-176: each user's identity from their connection on the project's host (and instance); none means skipped.</summary>
    private async Task<Dictionary<Guid, CommitIdentity>> IdentitiesAsync(CommitHostKind host, Guid? instanceId, List<Guid> ids, CancellationToken cancellationToken)
    {
        if (host == CommitHostKind.GitHub)
        {
            var github = await db.GitHubConnections.AsNoTracking()
                .Where(c => ids.Contains(c.UserId))
                .Select(c => new { c.UserId, c.GithubUserId, c.Login })
                .ToListAsync(cancellationToken);
            return github.ToDictionary(
                c => c.UserId,
                c => new CommitIdentity(c.Login, c.GithubUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (host is not (CommitHostKind.GitLab or CommitHostKind.Bitbucket) || instanceId is null)
        {
            return [];
        }

        var kind = host == CommitHostKind.GitLab ? GitHostKind.Gitlab : GitHostKind.Bitbucket;
        var connections = await db.GitConnections.AsNoTracking()
            .Where(c => ids.Contains(c.UserId) && c.Provider == kind && c.InstanceId == instanceId)
            .Select(c => new { c.UserId, c.ExternalUserId, c.Login, c.Status, c.UpdatedAt, c.CommitEmailEncrypted })
            .ToListAsync(cancellationToken);
        var identities = new Dictionary<Guid, CommitIdentity>();

        // A user with several connections on the host: a valid one first, then the newest. A Bitbucket one needs its email.
        foreach (var connection in connections
            .OrderByDescending(c => c.Status == GitConnectionStatus.Valid)
            .ThenByDescending(c => c.UpdatedAt))
        {
            if (identities.ContainsKey(connection.UserId))
            {
                continue;
            }

            if (host == CommitHostKind.GitLab)
            {
                identities[connection.UserId] = new CommitIdentity(connection.Login, connection.ExternalUserId);
            }
            else if (connection.CommitEmailEncrypted is { } encrypted && EmailProtector.TryUnprotect(encrypted, out var email))
            {
                identities[connection.UserId] = new CommitIdentity(connection.Login, connection.ExternalUserId, email);
            }
        }

        return identities;
    }

    private async Task<string?> SafelyAsync(Guid? projectId, Func<Task<string?>> resolve, CancellationToken cancellationToken)
    {
        try
        {
            return await resolve();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // FR-174: "Given no credited person, the commit succeeds": a read that fails is no credited person.
            LogResolutionFailed(logger, ex.GetType().Name, projectId);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resolving commit credit failed ({Failure}) for project {ProjectId}; committing without a co-author")]
    private static partial void LogResolutionFailed(ILogger logger, string failure, Guid? projectId);
}
