using System.Collections.Concurrent;
using System.Globalization;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Git;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// §4.15, §9: the deterministic Fake deployment provider (<c>Deployments:Provider=Fake</c>; Development and E2E). No HTTP:
/// any non-empty token is accepted (tokens starting <c>fake_invalid</c> are rejected; <c>fake_team</c> ones are
/// team-scoped to <c>fake-team</c>), projects are created or linked as named, env writes get stable ids. A deployment is
/// <c>READY</c> after 2 polls at <c>https://fake-&lt;sha8&gt;.vercel.test</c>, or <c>ERROR</c> when the deployed commit contains
/// <c>.aide-fake/fail-build</c> (read from the project's working copy). The outcome is encoded in the deployment id, so it
/// survives a restart; the poll count is kept in memory (a restart polls twice more).
/// </summary>
public sealed class FakeDeploymentProvider(IWorkingCopies workingCopies) : IDeploymentProvider
{
    public const string InvalidTokenPrefix = "fake_invalid";
    public const string TeamTokenPrefix = "fake_team";
    public const string TeamId = "team_fake";
    public const string TeamSlug = "fake-team";

    private const int MaxListedPaths = 200_000;

    private readonly ConcurrentDictionary<string, int> _polls = new(StringComparer.Ordinal);

    public DeploymentProvider Kind => DeploymentProvider.Fake;

    public Task<DeploymentAccount> ValidateTokenAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (token.StartsWith(InvalidTokenPrefix, StringComparison.Ordinal))
        {
            throw new DeploymentProviderException(VercelFailure.CredentialRejected, 403, "The Fake provider rejects tokens starting with fake_invalid.");
        }

        return Task.FromResult(token.StartsWith(TeamTokenPrefix, StringComparison.Ordinal)
            ? new DeploymentAccount("usr_fake", "fake-user", TeamId, TeamSlug, VercelTokenScope.Team, null)
            : new DeploymentAccount("usr_fake", "fake-user", null, null, VercelTokenScope.Account, null));
    }

    public Task<IReadOnlyList<DeploymentTeam>> ListTeamsAsync(DeploymentAuth auth, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeploymentTeam>>([new DeploymentTeam(TeamId, TeamSlug, "Fake team")]);

    public Task<IReadOnlyList<DeploymentProject>> ListProjectsAsync(DeploymentAuth auth, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeploymentProject>>([new DeploymentProject("prj_fake_todo", "todo", null, null)]);

    public Task<DeploymentProject> CreateProjectAsync(DeploymentAuth auth, CreateDeploymentProject request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new DeploymentProject($"prj_fake_{request.Name}", request.Name, request.Repo, null));
    }

    public Task<DeploymentProject?> FindProjectAsync(DeploymentAuth auth, string idOrName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(idOrName);
        var name = idOrName.StartsWith("prj_fake_", StringComparison.Ordinal) ? idOrName["prj_fake_".Length..] : idOrName;
        return Task.FromResult<DeploymentProject?>(new DeploymentProject(idOrName, name, null, null));
    }

    public Task<IReadOnlyList<DeploymentEnvWritten>> WriteEnvAsync(
        DeploymentAuth auth, string vercelProjectId, IReadOnlyList<DeploymentEnvWrite> writes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        IReadOnlyList<DeploymentEnvWritten> written = [.. writes.Select(w => new DeploymentEnvWritten(w.Key, w.Target, w.ExistingId ?? $"env_fake_{w.Key}_{w.Target.ToWire()}"))];
        return Task.FromResult(written);
    }

    public Task DeleteEnvAsync(DeploymentAuth auth, string vercelProjectId, string envId, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<ProviderDeployment> CreateDeploymentAsync(DeploymentAuth auth, CreateDeployment request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<string>? paths = null;
        try
        {
            paths = await workingCopies.ListPathsAtCommitAsync(request.AideProjectId, request.GitSha, MaxListedPaths, cancellationToken);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            // An unreadable commit builds fine.
        }

        var fails = FakeDeploymentRules.FailsBuild(paths);
        var sha8 = request.GitSha.Length >= 8 ? request.GitSha[..8] : request.GitSha;
        var id = string.Create(CultureInfo.InvariantCulture, $"dpl_fake_{(fails ? "err" : "ok")}_{sha8}_{Guid.NewGuid():N}");
        return new ProviderDeployment(id, FakeDeploymentRules.Url(request.GitSha), $"https://vercel.test/fake/{id}", "QUEUED", null);
    }

    public Task<ProviderDeployment> GetDeploymentAsync(DeploymentAuth auth, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        var poll = _polls.AddOrUpdate(deploymentId, 1, (_, n) => n + 1);
        var (fails, sha8) = Parse(deploymentId);
        var state = FakeDeploymentRules.StateAfter(poll, fails);
        return Task.FromResult(new ProviderDeployment(
            deploymentId,
            FakeDeploymentRules.Url(sha8),
            $"https://vercel.test/fake/{deploymentId}",
            state.ToWire().ToUpperInvariant(),
            state == DeploymentStatus.Error ? "The Fake build failed: the commit contains .aide-fake/fail-build." : null));
    }

    public Task<string?> GetBuildLogAsync(DeploymentAuth auth, string deploymentId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(Parse(deploymentId).Fails ? FakeDeploymentRules.FailedBuildLog : "Running \"npm run build\"\n> next build\nBuild completed.");

    public Task<ProviderDeployment?> FindByAideIdAsync(
        DeploymentAuth auth, string vercelProjectId, string sha, DeploymentEnvironment target, Guid aideDeploymentId, CancellationToken cancellationToken) =>
        Task.FromResult<ProviderDeployment?>(null);

    public Task CancelDeploymentAsync(DeploymentAuth auth, string deploymentId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<ProviderDeployment?> FindProductionDeploymentAsync(DeploymentAuth auth, string vercelProjectId, string sha, CancellationToken cancellationToken) =>
        Task.FromResult<ProviderDeployment?>(null);

    public Task PromoteAsync(DeploymentAuth auth, string vercelProjectId, string deploymentId, CancellationToken cancellationToken) => Task.CompletedTask;

    private static (bool Fails, string Sha8) Parse(string deploymentId)
    {
        var parts = deploymentId.Split('_');
        return parts.Length >= 5 && parts[0] == "dpl" && parts[1] == "fake" ? (parts[2] == "err", parts[3]) : (false, "00000000");
    }
}

/// <summary>§9: picks the Fake provider when <c>Deployments:Provider=Fake</c>, else Vercel.</summary>
public sealed class DeploymentProviderResolver(
    Microsoft.Extensions.Options.IOptions<DeploymentOptions> options,
    VercelDeploymentProvider vercel,
    FakeDeploymentProvider fake) : IDeploymentProviderResolver
{
    public bool UsesFake => options.Value.UseFake;

    public IDeploymentProvider Current => UsesFake ? fake : vercel;
}
