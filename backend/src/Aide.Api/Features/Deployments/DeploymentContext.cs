using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>The project's GitHub repository as a deployment needs it: <c>owner/name</c> and GitHub's id when recorded.</summary>
public sealed record DeploymentRepo(string? Owner, string? Name, long? RepoId)
{
    public string? FullName => Owner is null || Name is null ? null : $"{Owner}/{Name}";
}

/// <summary>
/// What a deployment of a project uses (FR-88): its settings, the bound Owner's Vercel connection and decrypted token (the
/// project acts on that Owner's behalf, §2, D-V2-10), the provider in use and the repository. Never logged. Every use is
/// Owner-gated or automatic: env var writes and deletes, promote and production redeploys need an Owner
/// (<c>ManageDeployments</c>); preview deployments (a writer, or AIDE after execution) and the automatic production
/// deployment also run with the binder's token, as §2 allows. Changing the provider or the Vercel project needs the
/// binder or the caller's own connection (<see cref="Domain.Deployments.DeploymentSettingsRules.CanUseBinding"/>).
/// </summary>
public sealed record DeploymentContext(
    ProjectDeploymentSettings Settings,
    VercelConnection Connection,
    DeploymentAuth Auth,
    IDeploymentProvider Provider,
    DeploymentRepo Repo)
{
    public override string ToString() => $"DeploymentContext {{ Project = {Settings.ProjectId} }}";
}

/// <summary>
/// FR-87, FR-88: resolves a project's <see cref="DeploymentContext"/> and records a Vercel token rejection (the connection
/// becomes <c>invalid</c> with <c>CONNECTION_INVALID</c>). Tokens are decrypted with <see cref="SecretPurposes.VercelToken"/>.
/// </summary>
public sealed class DeploymentAccess(AideDbContext db, ISecretProtectors protectors, IDeploymentProviderResolver providers)
{
    public ISecretProtector Tokens => protectors.ForPurpose(SecretPurposes.VercelToken);

    public ISecretProtector Values => protectors.ForPurpose(SecretPurposes.DeploymentEnvValue);

    public IDeploymentProvider Provider => providers.Current;

    public bool UsesFake => providers.UsesFake;

    /// <summary>
    /// The context for <paramref name="projectId"/>; <c>DEPLOYMENT_NOT_CONFIGURED</c> without settings, a bound connection
    /// or (unless <paramref name="requireProject"/> is false) a linked Vercel project; <c>CREDENTIAL_INVALID</c> when the
    /// token can't be decrypted.
    /// </summary>
    public async Task<Result<DeploymentContext>> ResolveAsync(Guid projectId, CancellationToken cancellationToken, bool requireProject = true)
    {
        var settings = await db.ProjectDeploymentSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);
        if (settings?.ConnectionId is not { } connectionId)
        {
            return NotConfigured("Deployment isn't set up for this project: bind a Vercel connection in Settings → Deployment.");
        }

        if (requireProject && string.IsNullOrEmpty(settings.VercelProjectId))
        {
            return NotConfigured("Link or create a Vercel project in Settings → Deployment first.");
        }

        var connection = await db.VercelConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            return NotConfigured("The bound Vercel connection no longer exists: bind another in Settings → Deployment.");
        }

        if (!Tokens.TryUnprotect(connection.TokenEncrypted, out var token) || token.Length == 0)
        {
            return Result.Failure<DeploymentContext>(new DomainError(ErrorCodes.CredentialInvalid, "The bound Vercel token can't be decrypted; its owner needs to add it again."));
        }

        return Result.Success(new DeploymentContext(settings, connection, new DeploymentAuth(token, connection.TeamId), providers.Current, await RepoAsync(projectId, cancellationToken)));
    }

    /// <summary>The project's GitHub repository (unknown parts <c>null</c>: a Local project, deployable only with the Fake).</summary>
    public async Task<DeploymentRepo> RepoAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var git = await db.ProjectGitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);
        if (git is null || !ProjectSettingsRules.IsGitHub(git.Provider))
        {
            return new DeploymentRepo(null, null, null);
        }

        var url = ProjectSettingsRules.RepoWebUrl(git.Provider, git.RepoUrl, git.RepoFullName);
        return ProjectSettingsRules.TryParseGitHubRepo(url, out var repo)
            ? new DeploymentRepo(repo.Owner, repo.Name, git.RepoId)
            : new DeploymentRepo(null, null, git.RepoId);
    }

    /// <summary>
    /// FR-87: Vercel rejected the token while in use: the connection becomes <c>invalid</c> and <c>CONNECTION_INVALID</c> is
    /// recorded for the project (no secret). Added to the caller's unit of work; the caller saves.
    /// </summary>
    public async Task MarkInvalidAsync(Guid connectionId, Guid? projectId, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await db.VercelConnections.FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            return;
        }

        connection.LastValidatedAt = now;
        if (connection.Status == VercelConnectionStatus.Invalid)
        {
            return;
        }

        connection.Status = VercelConnectionStatus.Invalid;
        db.AddEvent(EventTypes.ConnectionInvalid, projectId, new { connectionId, userId = connection.UserId, provider = "vercel" }, actor, now);
    }

    private static Result<DeploymentContext> NotConfigured(string message) =>
        Result.Failure<DeploymentContext>(new DomainError(ErrorCodes.DeploymentNotConfigured, message));
}
