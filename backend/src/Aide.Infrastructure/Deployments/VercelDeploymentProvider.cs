using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// §4.20 Vercel on the generic interface (the v2 logic, moved): create with <c>gitRepository</c> for GitHub, GitLab and
/// Bitbucket, link an existing project (it must be unlinked or linked to this repository), env sync per (key, environment),
/// deployments with <c>gitSource {ref, sha}</c>, adoption of Vercel's production deployment of the merge commit, build log
/// tail, cancel and promote (rollback). Branch control is the repository's <c>vercel.json</c>
/// <c>git.deploymentEnabled {"aide/**": false}</c>, which the readiness checklist and review read (<see cref="VercelConfigRules"/>).
/// </summary>
public sealed class VercelDeploymentProvider(VercelApiClient client) : IDeploymentProvider
{
    /// <summary>The <c>vercel</c> HttpClient (on <see cref="VercelApiClient.ApiBaseUrl"/>).</summary>
    public const string HttpClientName = VercelApiClient.HttpClientName;

    public DeploymentProvider Kind => DeploymentProvider.Vercel;

    public DeploymentProvider Platform => DeploymentProvider.Vercel;

    public DeploymentCapabilities Capabilities => DeploymentPlatforms.Capabilities(DeploymentProvider.Vercel);

    public Task<DeploymentAccount> ValidateConnectionAsync(DeploymentAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        return client.ValidateTokenAsync(auth.Token, cancellationToken);
    }

    public async Task<IReadOnlyList<DeploymentResource>> ListResourcesAsync(DeploymentAuth auth, CancellationToken cancellationToken) =>
        [.. (await client.ListProjectsAsync(auth, cancellationToken)).Select(p => new DeploymentResource(p.Id, p.Name, p.LinkedRepo))];

    public async Task<DeploymentSetupResult> SetupTargetAsync(DeploymentAuth auth, DeploymentSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var repo = request.Target.Repo;
        if (!request.Create)
        {
            var linked = await client.FindProjectAsync(auth, request.LinkResourceId!, cancellationToken)
                ?? throw new DeploymentProviderException(DeploymentFailure.NotFound, 404, "The Vercel project was not found (with this connection's account or team).");
            return DeploymentSettingsRules.LinkMatches(linked.LinkedRepo, repo.FullName)
                ? Result(linked)
                : throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, $"The Vercel project is linked to {linked.LinkedRepo}, not {repo.FullName}.");
        }

        // A retry after a failure further on finds the project it created: adopt it when it is linked to this repository.
        // A create always carries a name (the PUT validates it); a nameless one is a bug, never a project called "".
        var name = string.IsNullOrEmpty(request.Name)
            ? throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, "A new Vercel project needs a name.")
            : request.Name;
        if (await client.FindProjectAsync(auth, name, cancellationToken) is { } existing)
        {
            return existing.LinkedRepo is not null && DeploymentSettingsRules.LinkMatches(existing.LinkedRepo, repo.FullName)
                ? Result(existing)
                : throw new DeploymentProviderException(DeploymentFailure.Rejected, 409, "A Vercel project with this name exists already; link it instead or pick another name.");
        }

        var config = request.Target.BuildConfig;
        var created = await client.CreateProjectAsync(
            auth,
            new CreateDeploymentProject(
                name, repo.FullName ?? name, config.Framework, request.Target.RootDirectory, repo.Host ?? "github", config.BuildCommand, config.OutputDirectory),
            cancellationToken);
        return Result(created);
    }

    public async Task<ProviderTargetCheck> VerifyTargetAsync(DeploymentAuth auth, DeploymentTargetRef target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var project = target.ResourceId is { } id ? await client.FindProjectAsync(auth, id, cancellationToken) : null;
        if (project is null)
        {
            return new ProviderTargetCheck(false, "The Vercel project no longer exists; set the target up again.", null);
        }

        // Previews are per branch on Vercel, so the preview environment is ready once the project is linked.
        return project.LinkedRepo is null
            ? new ProviderTargetCheck(false, "The Vercel project isn't connected to a Git repository. Connect it in Vercel, then verify again.", null)
            : DeploymentSettingsRules.LinkMatches(project.LinkedRepo, target.Repo.FullName)
                ? new ProviderTargetCheck(true, null, true)
                : new ProviderTargetCheck(false, $"The Vercel project is linked to {project.LinkedRepo}, not {target.Repo.FullName}.", null);
    }

    public Task<IReadOnlyList<DeploymentEnvWritten>> SyncEnvAsync(DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvSyncRequest request, CancellationToken cancellationToken) =>
        SyncCoreAsync(auth, target, request, cancellationToken);

    public Task<ProviderDeployment> DeployAsync(DeploymentAuth auth, DeployRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Target;
        var repo = target.Repo;
        return client.CreateDeploymentAsync(
            auth,
            new CreateDeployment(
                request.AideDeploymentId,
                target.AideProjectId,
                target.ResourceId!,
                target.Name ?? target.ResourceId!,
                request.Environment,
                request.GitRef,
                request.GitSha,
                repo.GitHubRepoId,
                repo.Owner,
                repo.Name,
                repo.Host ?? "github",
                repo.ExternalId),
            cancellationToken);
    }

    public Task<ProviderDeployment> GetDeploymentAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken) =>
        client.GetDeploymentAsync(auth, deploymentId, cancellationToken);

    public Task<string?> GetBuildLogTailAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken) =>
        client.GetBuildLogAsync(auth, deploymentId, cancellationToken);

    public Task<ProviderDeployment?> FindDeploymentForShaAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvironment environment, string sha, Guid? aideDeploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return aideDeploymentId is { } id
            ? client.FindByAideIdAsync(auth, target.ResourceId!, sha, environment, id, cancellationToken)
            : client.FindProductionDeploymentAsync(auth, target.ResourceId!, sha, cancellationToken);
    }

    public Task CancelAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken) =>
        client.CancelDeploymentAsync(auth, deploymentId, cancellationToken);

    public Task RollbackAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return client.PromoteAsync(auth, target.ResourceId!, deploymentId, cancellationToken);
    }

    private async Task<IReadOnlyList<DeploymentEnvWritten>> SyncCoreAsync(DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvSyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        var written = await client.WriteEnvAsync(auth, target.ResourceId!, request.Writes, cancellationToken);
        foreach (var id in request.Deletes)
        {
            await client.DeleteEnvAsync(auth, target.ResourceId!, id, cancellationToken);
        }

        return written;
    }

    private static DeploymentSetupResult Result(DeploymentProject project) => new(project.Id, project.Name, null, null, null);
}
