using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.Infrastructure.Deployments;

/// <summary>Deployment settings (section <c>Deployments</c>, §9).</summary>
public sealed class DeploymentOptions
{
    public const string SectionName = "Deployments";

    /// <summary>
    /// <c>Vercel</c> (default) or <c>Fake</c>: every connection check, project link and deployment uses the deterministic
    /// Fake provider (Development and E2E, §9), and the API accepts provider <c>fake</c> and Local Git projects.
    /// </summary>
    public string Provider { get; set; } = "Vercel";

    /// <summary>
    /// Allows <c>Provider=Fake</c> outside Development and Testing (E2E on Kubernetes); a warning is logged at every boot.
    /// Without it, the Fake provider elsewhere stops startup.
    /// </summary>
    public bool AllowFake { get; set; }

    public bool UseFake => string.Equals(Provider?.Trim(), "Fake", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A user's token and, for a team-scoped Vercel token, its team (sent as <c>teamId</c> on every team resource). For
/// Netlify and Cloudflare, the connection's account (<see cref="AccountId"/>) and its slug (Netlify's
/// <c>account_slug</c>).
/// </summary>
public sealed record DeploymentAuth(string Token, string? TeamId, string? AccountId = null, string? AccountSlug = null)
{
    /// <summary>Redacted: the token never reaches a log.</summary>
    public override string ToString() => $"DeploymentAuth {{ TeamId = {TeamId}, AccountId = {AccountId} }}";
}

/// <summary>FR-87: who a token belongs to: the Vercel user, the team of a team-scoped token, and the token's expiry.</summary>
public sealed record DeploymentAccount(string UserId, string? Username, string? TeamId, string? TeamSlug, VercelTokenScope Scope, DateTimeOffset? ExpiresAt);

/// <summary>FR-88: a Vercel project; <see cref="LinkedRepo"/> is its linked GitHub repository (<c>owner/name</c>), if any.</summary>
public sealed record DeploymentProject(string Id, string Name, string? LinkedRepo, long? LinkedRepoId);

/// <summary>FR-142: a Vercel team the token can see.</summary>
public sealed record DeploymentTeam(string Id, string Slug, string Name);

/// <summary>
/// FR-88, FR-144: create a Vercel project linked to <see cref="Repo"/> (<c>owner/name</c>, GitLab <c>group/project</c>,
/// Bitbucket <c>workspace/slug</c>) on <see cref="RepoType"/> (<c>github</c>, <c>gitlab</c>, <c>bitbucket</c>), with the
/// derived build settings when known.
/// </summary>
public sealed record CreateDeploymentProject(
    string Name, string Repo, string? Framework, string RootDirectory, string RepoType = "github", string? BuildCommand = null, string? OutputDirectory = null);

/// <summary>One env var write for one target (§4.15): create, or update <see cref="ExistingId"/>. The value is never logged.</summary>
public sealed record DeploymentEnvWrite(string Key, string Value, DeploymentEnvironment Target, bool Sensitive, string? ExistingId)
{
    /// <summary>Redacted: the value never reaches a log.</summary>
    public override string ToString() => $"DeploymentEnvWrite {{ Key = {Key}, Target = {Target} }}";
}

/// <summary>Vercel's id of a written (key, target).</summary>
public sealed record DeploymentEnvWritten(string Key, DeploymentEnvironment Target, string Id);

/// <summary>
/// FR-90, FR-91: create a deployment of <see cref="GitSha"/> on <see cref="GitRef"/> for the Vercel project. The repository
/// is named by its GitHub id when known (<see cref="RepoId"/>), else by <see cref="RepoOwner"/>/<see cref="RepoName"/>.
/// <see cref="AideProjectId"/> lets the Fake provider read the commit in the project's working copy.
/// <see cref="AideDeploymentId"/> (the AIDE row) is sent in the deployment's <c>meta</c> as an idempotency key, so a retry
/// finds a deployment an earlier attempt created (<see cref="IVercelClient.FindByAideIdAsync"/>).
/// </summary>
public sealed record CreateDeployment(
    Guid AideDeploymentId,
    Guid AideProjectId,
    string VercelProjectId,
    string VercelProjectName,
    DeploymentEnvironment Target,
    string GitRef,
    string GitSha,
    long? RepoId,
    string? RepoOwner,
    string? RepoName,
    string RepoType = "github",
    string? ExternalRepoId = null);

/// <summary>A deployment as the provider reports it: id, URL (with <c>https://</c>), inspector, raw state and error.</summary>
public sealed record ProviderDeployment(string Id, string? Url, string? InspectorUrl, string? ReadyState, string? ErrorMessage, string? Alias = null, DeploymentStatus? State = null)
{
    /// <summary>The mapped state: <see cref="State"/> when the provider mapped it (§4.20 table), else Vercel's <c>readyState</c>.</summary>
    public DeploymentStatus Status => State ?? DeploymentStates.FromReadyState(ReadyState);
}

/// <summary>A failed provider request: how (<see cref="Failure"/>), the HTTP status and Vercel's message (never a token or value).</summary>
public sealed class DeploymentProviderException : Exception
{
    public DeploymentProviderException()
    {
    }

    public DeploymentProviderException(string message)
        : base(message)
    {
    }

    public DeploymentProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DeploymentProviderException(
        DeploymentFailure failure, int? status, string message, string? installUrl = null, DateTimeOffset? retryAt = null, DeploymentProvider platform = DeploymentProvider.Vercel)
        : base(message)
    {
        Failure = failure;
        Status = status;
        InstallUrl = installUrl;
        RetryAt = retryAt;
        Platform = platform;
    }

    /// <summary>The platform that failed (the <c>platform</c> of the FR-144 problems).</summary>
    public DeploymentProvider Platform { get; } = DeploymentProvider.Vercel;

    public DeploymentFailure Failure { get; }

    public int? Status { get; }

    /// <summary>FR-144: the platform's Git install/connect link for <see cref="DeploymentFailure.GitNotConnected"/>, or the
    /// GitHub App settings link for <see cref="DeploymentFailure.PermissionMissing"/>.</summary>
    public string? InstallUrl { get; }

    /// <summary>For <see cref="DeploymentFailure.RateLimited"/>: when to try again.</summary>
    public DateTimeOffset? RetryAt { get; }
}

/// <summary>
/// §4.15, FR-142: Vercel's REST API (on the fixed host <c>api.vercel.com</c>, §8) or the v2 Fake of it: connection checks,
/// pickers, projects, env vars and deployments. Wrapped by <see cref="VercelDeploymentProvider"/> (the §4.20 interface) and
/// used directly for connection validation and the wizard's Deployment step. Failures throw
/// <see cref="DeploymentProviderException"/>.
/// </summary>
public interface IVercelClient
{
    /// <summary>FR-87: who the token belongs to (<c>GET /v2/user</c>, the token's scope, the team). Rejected: <see cref="DeploymentFailure.CredentialRejected"/>.</summary>
    Task<DeploymentAccount> ValidateTokenAsync(string token, CancellationToken cancellationToken);

    /// <summary>FR-142 picker: the teams the token can see (<c>GET /v2/teams</c>), as <c>(id, slug, name)</c>.</summary>
    Task<IReadOnlyList<DeploymentTeam>> ListTeamsAsync(DeploymentAuth auth, CancellationToken cancellationToken);

    /// <summary>FR-142 picker: the projects of the token's account or team (<c>GET /v9/projects</c>, the first 100).</summary>
    Task<IReadOnlyList<DeploymentProject>> ListProjectsAsync(DeploymentAuth auth, CancellationToken cancellationToken);

    /// <summary>FR-88: <c>POST /v11/projects</c> linked to the GitHub repository.</summary>
    Task<DeploymentProject> CreateProjectAsync(DeploymentAuth auth, CreateDeploymentProject request, CancellationToken cancellationToken);

    /// <summary>FR-88: <c>GET /v9/projects/{idOrName}</c>, or <c>null</c> when it doesn't exist.</summary>
    Task<DeploymentProject?> FindProjectAsync(DeploymentAuth auth, string idOrName, CancellationToken cancellationToken);

    /// <summary>§4.15: writes env vars (creates batched, updates one by one), returning Vercel's id per (key, target).</summary>
    Task<IReadOnlyList<DeploymentEnvWritten>> WriteEnvAsync(DeploymentAuth auth, string vercelProjectId, IReadOnlyList<DeploymentEnvWrite> writes, CancellationToken cancellationToken);

    /// <summary>§4.15: deletes a Vercel env var (already gone counts as deleted).</summary>
    Task DeleteEnvAsync(DeploymentAuth auth, string vercelProjectId, string envId, CancellationToken cancellationToken);

    /// <summary>FR-90, FR-91: <c>POST /v13/deployments</c> with <c>gitSource</c>.</summary>
    Task<ProviderDeployment> CreateDeploymentAsync(DeploymentAuth auth, CreateDeployment request, CancellationToken cancellationToken);

    /// <summary>§4.15: <c>GET /v13/deployments/{id}</c>.</summary>
    Task<ProviderDeployment> GetDeploymentAsync(DeploymentAuth auth, string deploymentId, CancellationToken cancellationToken);

    /// <summary>
    /// FR-90: the build log (<c>GET /v3/deployments/{id}/events</c>; at most <see cref="MaxBuildLogLength"/> characters, the
    /// end), or <c>null</c>. Unmasked: the caller masks it before cutting it to the stored tail.
    /// </summary>
    Task<string?> GetBuildLogAsync(DeploymentAuth auth, string deploymentId, CancellationToken cancellationToken);

    /// <summary>The longest build log read (the end of a longer one).</summary>
    public const int MaxBuildLogLength = 1_000_000;

    /// <summary>
    /// §4.15 idempotency: the deployment an earlier attempt created for the AIDE row <paramref name="aideDeploymentId"/>
    /// (its <c>meta.aideDeploymentId</c>), not canceled, or <c>null</c>.
    /// </summary>
    Task<ProviderDeployment?> FindByAideIdAsync(
        DeploymentAuth auth, string vercelProjectId, string sha, DeploymentEnvironment target, Guid aideDeploymentId, CancellationToken cancellationToken);

    /// <summary>§4.15: cancels a deployment no AIDE row records (<c>PATCH /v12/deployments/{id}/cancel</c>); gone or finished is fine.</summary>
    Task CancelDeploymentAsync(DeploymentAuth auth, string deploymentId, CancellationToken cancellationToken);

    /// <summary>FR-91: the production deployment Vercel made for <paramref name="sha"/>, or <c>null</c>.</summary>
    Task<ProviderDeployment?> FindProductionDeploymentAsync(DeploymentAuth auth, string vercelProjectId, string sha, CancellationToken cancellationToken);

    /// <summary>FR-91: <c>POST /v10/projects/{id}/promote/{deploymentId}</c> (no rebuild).</summary>
    Task PromoteAsync(DeploymentAuth auth, string vercelProjectId, string deploymentId, CancellationToken cancellationToken);
}


