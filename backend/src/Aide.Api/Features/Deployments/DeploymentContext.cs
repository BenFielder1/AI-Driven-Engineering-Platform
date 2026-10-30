using Aide.Api.Features.Git;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// What a deployment of one target uses (FR-88, FR-144): the target, the bound Owner's platform connection (<c>null</c> for
/// GitHub Pages, which uses the project's GitHub App installation or PAT, FR-143), the decrypted token, the provider for the
/// target's platform (the Fake in Fake mode) and the repository. The project acts on the binder's behalf (§2, D-V2-10).
/// Never logged.
/// </summary>
public sealed record DeploymentContext(
    DeploymentTarget Target,
    PlatformConnection? Connection,
    DeploymentAuth Auth,
    IDeploymentProvider Provider,
    DeploymentRepoRef Repo)
{
    /// <summary>The target as the provider sees it.</summary>
    public DeploymentTargetRef TargetRef => DeploymentAccess.RefOf(Target, Repo);

    public override string ToString() => $"DeploymentContext {{ Project = {Target.ProjectId}, Component = {Target.Component} }}";
}

/// <summary>
/// FR-88, FR-142–FR-144: resolves a target's <see cref="DeploymentContext"/> and records a token rejection (the connection
/// becomes <c>invalid</c> with <c>CONNECTION_INVALID</c>). Platform tokens are decrypted with
/// <see cref="SecretPurposes.PlatformToken"/>; GitHub Pages uses <see cref="GitHubRepoAccess"/>.
/// </summary>
public sealed class DeploymentAccess(AideDbContext db, ISecretProtectors protectors, IDeploymentProviderResolver providers, GitHubRepoAccess github)
{
    public ISecretProtector Tokens => protectors.ForPurpose(SecretPurposes.PlatformToken);

    public ISecretProtector Values => protectors.ForPurpose(SecretPurposes.DeploymentEnvValue);

    /// <summary>Vercel's client (the v2 Fake of it in Fake mode), for the wizard's Deployment step.</summary>
    public IVercelClient Vercel => providers.Vercel;

    public bool UsesFake => providers.UsesFake;

    public IDeploymentProvider ProviderFor(DeploymentProvider platform) => providers.ForPlatform(platform);

    /// <summary>
    /// The context of the project's <paramref name="component"/> target; <c>DEPLOYMENT_NOT_CONFIGURED</c> without a target,
    /// a bound connection (where one is needed) or (unless <paramref name="requireResource"/> is false) a resource;
    /// <c>CREDENTIAL_INVALID</c> when the token can't be decrypted; <c>UPSTREAM_ERROR</c> when GitHub can't mint a token.
    /// </summary>
    public async Task<Result<DeploymentContext>> ResolveAsync(
        Guid projectId, CancellationToken cancellationToken, bool requireResource = true, DeploymentComponent component = DeploymentComponent.Frontend)
    {
        var target = await TargetAsync(db, projectId, component, cancellationToken);
        if (target is null)
        {
            return NotConfigured("Deployment isn't set up for this project: set up a target in Settings → Deployment.");
        }

        return await ResolveAsync(target, cancellationToken, requireResource);
    }

    /// <summary>The context of <paramref name="target"/> (as <see cref="ResolveAsync(Guid, CancellationToken, bool, DeploymentComponent)"/>).</summary>
    public async Task<Result<DeploymentContext>> ResolveAsync(DeploymentTarget target, CancellationToken cancellationToken, bool requireResource = true)
    {
        ArgumentNullException.ThrowIfNull(target);
        var platform = DeploymentPlatforms.DisplayName(target.Provider);
        if (requireResource && string.IsNullOrEmpty(target.ResourceId))
        {
            return NotConfigured($"Create or link the {platform} resource in Settings → Deployment first.");
        }

        var repo = await RepoAsync(target.ProjectId, cancellationToken);
        var provider = providers.ForPlatform(target.Provider);
        if (!DeploymentPlatforms.NeedsConnection(target.Provider))
        {
            // FR-143: GitHub Pages acts with the project's GitHub App installation token or PAT (none needed in Fake mode).
            if (providers.UsesFake)
            {
                return Result.Success(new DeploymentContext(target, null, new DeploymentAuth("fake", null), provider, repo));
            }

            var resolved = await github.ResolveAsync(target.ProjectId, cancellationToken);
            if (resolved.IsFailure)
            {
                return Result.Failure<DeploymentContext>(resolved.Error!);
            }

            return resolved.Value is { } gh
                ? Result.Success(new DeploymentContext(target, null, new DeploymentAuth(gh.Token, null), provider, repo))
                : NotConfigured("GitHub Pages needs a GitHub repository.");
        }

        if (target.ConnectionId is not { } connectionId)
        {
            return NotConfigured($"Bind one of your {platform} connections in Settings → Deployment.");
        }

        var expected = DeploymentPlatforms.ConnectionProvider(target.Provider);
        var connection = await db.PlatformConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId && c.Provider == expected, cancellationToken);
        if (connection is null)
        {
            return NotConfigured($"The bound {platform} connection no longer exists: bind another in Settings → Deployment.");
        }

        if (!Tokens.TryUnprotect(connection.SecretEncrypted, out var token) || token.Length == 0)
        {
            return Result.Failure<DeploymentContext>(new DomainError(ErrorCodes.CredentialInvalid, $"The bound {platform} token can't be decrypted; its owner needs to add it again."));
        }

        return Result.Success(new DeploymentContext(target, connection, new DeploymentAuth(token, connection.Metadata.TeamId), provider, repo));
    }

    /// <summary>
    /// The project's repository for platforms: GitHub (owner/name and id), GitLab (namespace path, project, project id) or
    /// Bitbucket (workspace, slug, UUID); <see cref="DeploymentRepoRef.Local"/> for a Local project (deployable only with the Fake).
    /// </summary>
    public async Task<DeploymentRepoRef> RepoAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var git = await db.ProjectGitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);
        if (git is null)
        {
            return DeploymentRepoRef.Local;
        }

        var branch = string.IsNullOrEmpty(git.DefaultBranch) ? "main" : git.DefaultBranch;
        if (ProjectSettingsRules.IsGitHub(git.Provider))
        {
            var url = ProjectSettingsRules.RepoWebUrl(git.Provider, git.RepoUrl, git.RepoFullName);
            return ProjectSettingsRules.TryParseGitHubRepo(url, out var repo)
                ? new DeploymentRepoRef("github", repo.Owner, repo.Name, git.RepoId, null, $"{repo.Owner}/{repo.Name}", branch)
                : new DeploymentRepoRef("github", null, null, git.RepoId, null, null, branch);
        }

        if (git.Provider is GitProvider.Gitlab or GitProvider.Bitbucket && git.RepoFullName is { } fullName)
        {
            var slash = fullName.LastIndexOf('/');
            return new DeploymentRepoRef(
                git.Provider == GitProvider.Gitlab ? "gitlab" : "bitbucket",
                slash > 0 ? fullName[..slash] : null,
                slash > 0 ? fullName[(slash + 1)..] : fullName,
                null,
                git.ExternalRepoId,
                fullName,
                branch);
        }

        return DeploymentRepoRef.Local with { DefaultBranch = branch };
    }

    /// <summary>
    /// FR-87, FR-142: the platform rejected the token while in use: the connection becomes <c>invalid</c> and
    /// <c>CONNECTION_INVALID</c> is recorded for the project (no secret), notifying its owner. The caller saves.
    /// </summary>
    public Task MarkInvalidAsync(Guid? connectionId, Guid? projectId, string actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        connectionId is { } id
            ? Connections.PlatformConnectionService.MarkInvalidAsync(db, id, projectId, actor, now, cancellationToken)
            : Task.CompletedTask;

    /// <summary>The target as a provider sees it.</summary>
    public static DeploymentTargetRef RefOf(DeploymentTarget target, DeploymentRepoRef repo)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new DeploymentTargetRef(
            target.ProjectId, target.Provider, target.Component, target.ResourceId, target.PreviewResourceId, target.Name, target.RootDirectory, target.BuildConfig, repo);
    }

    /// <summary>The project's <c>frontend</c> deployment target (v2's deployment settings), untracked; <c>null</c> when none.</summary>
    public static Task<DeploymentTarget?> FrontendTargetAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken) =>
        TargetAsync(db, projectId, DeploymentComponent.Frontend, cancellationToken);

    /// <summary>The project's target of <paramref name="component"/>, untracked; <c>null</c> when none.</summary>
    public static Task<DeploymentTarget?> TargetAsync(AideDbContext db, Guid projectId, DeploymentComponent component, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.DeploymentTargets.AsNoTracking().FirstOrDefaultAsync(t => t.ProjectId == projectId && t.Component == component, cancellationToken);
    }

    private static Result<DeploymentContext> NotConfigured(string message) =>
        Result.Failure<DeploymentContext>(new DomainError(ErrorCodes.DeploymentNotConfigured, message));
}
