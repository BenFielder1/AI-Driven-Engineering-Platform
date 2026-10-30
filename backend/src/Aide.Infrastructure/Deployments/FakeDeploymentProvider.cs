using System.Collections.Concurrent;
using System.Globalization;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Git;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// §4.20, §9: the deterministic Fake mode of every platform (<c>Deployments:Provider=Fake</c>; Development and E2E). No HTTP.
/// Setup creates <c>fake_&lt;platform&gt;_&lt;name&gt;</c> (or links the id given) with production URL
/// <c>https://&lt;name&gt;-&lt;component&gt;.&lt;platform&gt;.test</c> and, where the platform has previews, a stable preview
/// URL. A deployment is <c>ready</c> after 2 polls at <c>https://fake-&lt;sha8&gt;-&lt;component&gt;.&lt;platform&gt;.test</c>;
/// it fails when the deployed commit contains <c>.aide-fake/fail-build</c>, and a backend's pre-deploy fails on
/// <c>.aide-fake/fail-migration</c>. The outcome is encoded in the deployment id (survives a restart); polls are counted in
/// memory. Deployments are recorded with provider <c>fake</c>.
/// </summary>
public sealed class FakeDeploymentProvider(DeploymentProvider platform, IWorkingCopies workingCopies, ConcurrentDictionary<string, int> polls) : IDeploymentProvider
{
    private const int MaxListedPaths = 200_000;

    public DeploymentProvider Kind => DeploymentProvider.Fake;

    public DeploymentProvider Platform => platform;

    public DeploymentCapabilities Capabilities => DeploymentPlatforms.Capabilities(platform);

    private string Label => DeploymentPlatforms.HostLabel(platform);

    public Task<DeploymentAccount> ValidateConnectionAsync(DeploymentAuth auth, CancellationToken cancellationToken) =>
        Task.FromResult(new DeploymentAccount("usr_fake", "fake-user", null, null, VercelTokenScope.Account, null));

    public Task<IReadOnlyList<DeploymentResource>> ListResourcesAsync(DeploymentAuth auth, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeploymentResource>>([new DeploymentResource($"fake_{Label}_todo", "todo", null)]);

    public Task<DeploymentSetupResult> SetupTargetAsync(DeploymentAuth auth, DeploymentSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Create
            ? request.Name ?? request.Target.Repo.Name ?? "app"
            : request.LinkResourceId is { } link && link.StartsWith($"fake_{Label}_", StringComparison.Ordinal) ? link[$"fake_{Label}_".Length..] : request.LinkResourceId ?? "app";
        var resourceId = request.Create ? $"fake_{Label}_{name}" : request.LinkResourceId ?? $"fake_{Label}_{name}";
        var component = request.Target.Component.ToWire();
        var previews = Capabilities.Previews;
        return Task.FromResult(new DeploymentSetupResult(
            resourceId,
            name,
            string.Create(CultureInfo.InvariantCulture, $"https://{name}-{component}.{Label}.test"),
            previews && platform is DeploymentProvider.Render or DeploymentProvider.Railway ? $"{resourceId}-preview" : null,
            previews ? string.Create(CultureInfo.InvariantCulture, $"https://{name}-{component}-preview.{Label}.test") : null));
    }

    public Task<ProviderTargetCheck> VerifyTargetAsync(DeploymentAuth auth, DeploymentTargetRef target, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderTargetCheck(true, null, Capabilities.Previews ? true : null));

    public Task<IReadOnlyList<DeploymentEnvWritten>> SyncEnvAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvSyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);
        IReadOnlyList<DeploymentEnvWritten> written =
            [.. request.Writes.Select(w => new DeploymentEnvWritten(w.Key, w.Target, w.ExistingId ?? $"env_fake_{target.Component.ToWire()}_{w.Key}_{w.Target.ToWire()}"))];
        return Task.FromResult(written);
    }

    public async Task<ProviderDeployment> DeployAsync(DeploymentAuth auth, DeployRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<string>? paths = null;
        try
        {
            paths = await workingCopies.ListPathsAtCommitAsync(request.Target.AideProjectId, request.GitSha, MaxListedPaths, cancellationToken);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            // An unreadable commit builds fine.
        }

        var outcome = FakeDeploymentRules.FailsBuild(paths) ? "err"
            : request.Target.Component == DeploymentComponent.Backend && FakeDeploymentRules.FailsMigration(paths) ? "mig"
            : "ok";
        var sha8 = (request.GitSha.Length >= 8 ? request.GitSha[..8] : request.GitSha).ToLowerInvariant();
        var id = string.Create(CultureInfo.InvariantCulture, $"dpl_fake_{outcome}_{sha8}_{request.Target.Component.ToWire()}_{Guid.NewGuid():N}");
        return new ProviderDeployment(id, FakeDeploymentRules.Url(request.GitSha, request.Target.Component, platform), Inspector(id), "QUEUED", null);
    }

    public Task<ProviderDeployment> GetDeploymentAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        ArgumentNullException.ThrowIfNull(target);
        var poll = polls.AddOrUpdate(deploymentId, 1, (_, n) => n + 1);
        var (outcome, sha8) = Parse(deploymentId);
        var state = FakeDeploymentRules.StateAfter(poll, outcome != "ok");
        var message = state != DeploymentStatus.Error ? null
            : outcome == "mig" ? "The Fake pre-deploy migration failed: the commit contains .aide-fake/fail-migration."
            : "The Fake build failed: the commit contains .aide-fake/fail-build.";
        return Task.FromResult(new ProviderDeployment(
            deploymentId, FakeDeploymentRules.Url(sha8, target.Component, platform), Inspector(deploymentId), state.ToWire().ToUpperInvariant(), message));
    }

    public Task<string?> GetBuildLogTailAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(Parse(deploymentId).Outcome switch
        {
            "err" => FakeDeploymentRules.FailedBuildLog,
            "mig" => FakeDeploymentRules.FailedMigrationLog,
            _ => "Running \"npm run build\"\nBuild completed.",
        });

    public Task<ProviderDeployment?> FindDeploymentForShaAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvironment environment, string sha, Guid? aideDeploymentId, CancellationToken cancellationToken) =>
        Task.FromResult<ProviderDeployment?>(null);

    public Task CancelAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RollbackAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken) =>
        Capabilities.Rollback
            ? Task.CompletedTask
            : throw new DeploymentProviderException(DeploymentFailure.NotSupported, null, $"{DeploymentPlatforms.DisplayName(platform)} has no rollback.", platform: platform);

    private string Inspector(string id) => $"https://{Label}.test/fake/{id}";

    private static (string Outcome, string Sha8) Parse(string deploymentId)
    {
        var parts = deploymentId.Split('_');
        return parts.Length >= 5 && parts[0] == "dpl" && parts[1] == "fake" ? (parts[2], parts[3]) : ("ok", "00000000");
    }
}
