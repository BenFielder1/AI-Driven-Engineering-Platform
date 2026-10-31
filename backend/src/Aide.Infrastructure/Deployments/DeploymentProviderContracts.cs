using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// The project's repository as a platform needs it: the host (<c>github</c>, <c>gitlab</c>, <c>bitbucket</c>; <c>null</c> for
/// Local), owner and name (GitHub; GitLab's namespace path and project; Bitbucket's workspace and slug), GitHub's numeric id,
/// the host's own id (GitLab project id, Bitbucket UUID), the full name and the default branch.
/// </summary>
public sealed record DeploymentRepoRef(
    string? Host, string? Owner, string? Name, long? GitHubRepoId, string? ExternalId, string? FullName, string DefaultBranch)
{
    public static DeploymentRepoRef Local { get; } = new(null, null, null, null, null, null, "main");
}

/// <summary>A target as a provider sees it (§4.20): the platform, component, resources, build configuration and repository.</summary>
public sealed record DeploymentTargetRef(
    Guid AideProjectId,
    DeploymentProvider Platform,
    DeploymentComponent Component,
    string? ResourceId,
    string? PreviewResourceId,
    string? Name,
    string RootDirectory,
    DeploymentBuildConfig BuildConfig,
    DeploymentRepoRef Repo);

/// <summary>FR-144: create a resource (<see cref="Create"/>, with name, region and plan) or link <see cref="LinkResourceId"/>.</summary>
public sealed record DeploymentSetupRequest(DeploymentTargetRef Target, bool Create, string? Name, string? Region, string? Plan, string? LinkResourceId);

/// <summary>What setup made or found: the resource, its name, and the production and preview resource and URLs when known.</summary>
public sealed record DeploymentSetupResult(string ResourceId, string Name, string? ProductionUrl, string? PreviewResourceId, string? PreviewUrl);

/// <summary>
/// FR-144 readiness facts only the platform knows: whether its Git integration reaches the repository (<c>null</c>: unknown),
/// a message about it, and whether the preview environment is ready (<c>null</c>: not applicable or unknown).
/// </summary>
public sealed record ProviderTargetCheck(bool? GitAccess, string? GitMessage, bool? PreviewReady);

/// <summary>
/// §4.20 <c>DeployAsync {environment, ref, sha}</c>; <see cref="AideDeploymentId"/> is the idempotency key.
/// <see cref="Rebuild"/>: a redeploy or manual deploy builds anew and never adopts the platform's existing build of the
/// commit (an adopt-only production, Cloudflare Pages, retries that build instead).
/// </summary>
public sealed record DeployRequest(Guid AideDeploymentId, DeploymentTargetRef Target, DeploymentEnvironment Environment, string GitRef, string GitSha, bool Rebuild = false);

/// <summary>§4.20 env sync: writes per (key, environment) and the platform ids to delete.</summary>
public sealed record DeploymentEnvSyncRequest(IReadOnlyList<DeploymentEnvWrite> Writes, IReadOnlyList<string> Deletes);

/// <summary>An entry of the link picker (§4.20 <c>ListResourcesAsync</c>).</summary>
public sealed record DeploymentResource(string Id, string Name, string? Detail);

/// <summary>
/// §4.20: the deployment provider interface (generalising v2's). One instance per platform; in Fake mode
/// (<c>Deployments:Provider=Fake</c>) every platform is the deterministic Fake. Never called from endpoints for builds and
/// polls (those are jobs). Failures throw <see cref="DeploymentProviderException"/>.
/// </summary>
public interface IDeploymentProvider
{
    /// <summary>The provider recorded on deployment rows: the platform, or <c>fake</c> in Fake mode.</summary>
    DeploymentProvider Kind { get; }

    /// <summary>The platform the target is on.</summary>
    DeploymentProvider Platform { get; }

    DeploymentCapabilities Capabilities { get; }

    Task<DeploymentAccount> ValidateConnectionAsync(DeploymentAuth auth, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeploymentResource>> ListResourcesAsync(DeploymentAuth auth, CancellationToken cancellationToken);

    /// <summary>Create or link the resource, with branch control and the preview resource where the platform has one.</summary>
    Task<DeploymentSetupResult> SetupTargetAsync(DeploymentAuth auth, DeploymentSetupRequest request, CancellationToken cancellationToken);

    Task<ProviderTargetCheck> VerifyTargetAsync(DeploymentAuth auth, DeploymentTargetRef target, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeploymentEnvWritten>> SyncEnvAsync(DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvSyncRequest request, CancellationToken cancellationToken);

    Task<ProviderDeployment> DeployAsync(DeploymentAuth auth, DeployRequest request, CancellationToken cancellationToken);

    Task<ProviderDeployment> GetDeploymentAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken);

    /// <summary>The build log (unmasked; the caller masks it before storing the tail), or <c>null</c>.</summary>
    Task<string?> GetBuildLogTailAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken);

    /// <summary>
    /// The deployment of <paramref name="sha"/>: with <paramref name="aideDeploymentId"/>, the one an earlier attempt created
    /// for that AIDE row; without, the one the platform made by itself (production adoption, FR-149). <c>null</c>: none.
    /// <paramref name="createdAfter"/> (the AIDE row's creation time, given with <paramref name="aideDeploymentId"/>): a
    /// platform that can't tag its builds with the row (Cloudflare) adopts only builds created at or after it, so a
    /// redeploy or manual deploy never adopts an earlier build of the same commit.
    /// </summary>
    Task<ProviderDeployment?> FindDeploymentForShaAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvironment environment, string sha, Guid? aideDeploymentId, DateTimeOffset? createdAfter, CancellationToken cancellationToken);

    Task CancelAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken);

    /// <summary>FR-150: make an earlier deployment current (Vercel: promote). Unsupported: <see cref="DeploymentFailure.NotSupported"/>.</summary>
    Task RollbackAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken);
}

/// <summary>§9, §4.20: providers by platform id (the Fake for all in Fake mode) and the Vercel client for connection checks.</summary>
public interface IDeploymentProviderResolver
{
    bool UsesFake { get; }

    IDeploymentProvider ForPlatform(DeploymentProvider platform);

    /// <summary>Vercel's client (the v2 Fake of it in Fake mode), for connection checks, pickers and the wizard.</summary>
    IVercelClient Vercel { get; }
}

/// <summary>
/// §4.20 seam for a platform AIDE can't call for real (none since M30, when Render and Railway joined): every
/// operation is <c>PLATFORM_ERROR</c> "not supported yet". Their Fake mode works.
/// </summary>
public sealed class UnsupportedDeploymentProvider(DeploymentProvider platform) : IDeploymentProvider
{
    public DeploymentProvider Kind => platform;

    public DeploymentProvider Platform => platform;

    public DeploymentCapabilities Capabilities => DeploymentPlatforms.Capabilities(platform);

    public Task<DeploymentAccount> ValidateConnectionAsync(DeploymentAuth auth, CancellationToken cancellationToken) => throw NotSupported();

    public Task<IReadOnlyList<DeploymentResource>> ListResourcesAsync(DeploymentAuth auth, CancellationToken cancellationToken) => throw NotSupported();

    public Task<DeploymentSetupResult> SetupTargetAsync(DeploymentAuth auth, DeploymentSetupRequest request, CancellationToken cancellationToken) => throw NotSupported();

    public Task<ProviderTargetCheck> VerifyTargetAsync(DeploymentAuth auth, DeploymentTargetRef target, CancellationToken cancellationToken) => throw NotSupported();

    public Task<IReadOnlyList<DeploymentEnvWritten>> SyncEnvAsync(DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvSyncRequest request, CancellationToken cancellationToken) =>
        throw NotSupported();

    public Task<ProviderDeployment> DeployAsync(DeploymentAuth auth, DeployRequest request, CancellationToken cancellationToken) => throw NotSupported();

    public Task<ProviderDeployment> GetDeploymentAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken) => throw NotSupported();

    public Task<string?> GetBuildLogTailAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken) => throw NotSupported();

    public Task<ProviderDeployment?> FindDeploymentForShaAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvironment environment, string sha, Guid? aideDeploymentId, DateTimeOffset? createdAfter, CancellationToken cancellationToken) =>
        throw NotSupported();

    public Task CancelAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken) => throw NotSupported();

    public Task RollbackAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken) => throw NotSupported();

    private DeploymentProviderException NotSupported() => new(
        DeploymentFailure.NotSupported,
        null,
        $"{DeploymentPlatforms.DisplayName(platform)} deployments aren't supported yet; only the Fake provider works for it in this release.",
        platform: platform);
}
