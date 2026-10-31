using System.Text.Json;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Platforms;
using Aide.Infrastructure.Platforms;
using static Aide.Infrastructure.Deployments.PlatformDeploymentCalls;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// FR-144, FR-145, FR-147, FR-149, FR-150, §4.20 Railway through a workspace (or account) token against the public GraphQL
/// API (<c>backboard.railway.com/graphql/v2</c>, Bearer), in the connection's workspace (<see cref="DeploymentAuth.AccountId"/>):
/// <list type="bullet">
/// <item>Create: the project named after the repository (found or <c>projectCreate {workspaceId, prDeploys: false}</c>, so
/// a frontend and backend on Railway share one project and its private network), <c>serviceCreate {projectId, source:
/// {repo}, branch: "main"}</c>, <c>serviceInstanceUpdate</c> (<see cref="RailwayRules.InstanceSettings"/>, with
/// <c>preDeployCommand</c>), <c>PORT</c> on a backend, and <c>serviceDomainCreate</c>. Not reachable by the Railway GitHub
/// app: <c>PLATFORM_GIT_NOT_CONNECTED</c>.</item>
/// <item>Link: a project (its service built from this repository) or <c>project:service</c>.</item>
/// <item>Preview: the <c>aide-preview</c> environment, created once from production with <c>skipInitialDeploys</c>, with
/// its own domain; its id is the target's <c>preview_resource_id</c>.</item>
/// <item><c>resource_id</c> = <c>project:service:production environment</c> (<see cref="RailwayRules.ResourceId"/>).</item>
/// </list>
/// GraphQL <c>errors</c> in a 200 answer are failures (<see cref="PlatformConnectionRules.ClassifyRailwayError"/>).
/// </summary>
public sealed partial class RailwayDeploymentProvider(PlatformHttp http) : IDeploymentProvider
{
    private const DeploymentProvider Railway = DeploymentProvider.Railway;

    private const string EnvironmentsFields = "environments { edges { node { id name } } }";

    public DeploymentProvider Kind => Railway;

    public DeploymentProvider Platform => Railway;

    public DeploymentCapabilities Capabilities => DeploymentPlatforms.Capabilities(Railway);

    public async Task<DeploymentAccount> ValidateConnectionAsync(DeploymentAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        var workspace = Workspace(auth);
        var data = await QueryAsync(auth, "query($id: String!) { workspace(workspaceId: $id) { id name } }", new { id = workspace }, cancellationToken);
        var found = Property(data, "workspace");
        return new DeploymentAccount(Text(found, "id") ?? workspace, Text(found, "name"), workspace, null, VercelTokenScope.Team, null);
    }

    public async Task<IReadOnlyList<DeploymentResource>> ListResourcesAsync(DeploymentAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        return [.. (await ProjectsAsync(auth, cancellationToken)).Select(p => new DeploymentResource(p.Id, p.Name, null))];
    }

    public async Task<DeploymentSetupResult> SetupTargetAsync(DeploymentAuth auth, DeploymentSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Target;
        if (target.Repo.Host != "github")
        {
            throw new DeploymentProviderException(
                DeploymentFailure.NotSupported, null, "Railway deploys from GitHub repositories only; deploy this component elsewhere.", platform: Railway);
        }

        string projectId;
        string? serviceId = null;
        string serviceName;
        if (request.Create)
        {
            serviceName = string.IsNullOrEmpty(request.Name)
                ? throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, "A new Railway service needs a name.", platform: Railway)
                : request.Name;
            projectId = await FindOrCreateProjectAsync(auth, target.Repo.Name ?? serviceName, cancellationToken);
        }
        else
        {
            var link = RailwayRules.ParseResourceId(request.LinkResourceId);
            projectId = link?.ProjectId ?? request.LinkResourceId!;
            serviceName = request.Name ?? string.Empty;
            var services = await ServicesAsync(auth, projectId, cancellationToken);
            var linked = link is not null ? services.FirstOrDefault(s => s.Id == link.ServiceId)
                : services.FirstOrDefault(s => s.Repo is not null && string.Equals(s.Repo, target.Repo.FullName, StringComparison.OrdinalIgnoreCase))
                    ?? services.FirstOrDefault(s => s.Name == request.Name);
            if (linked is null)
            {
                throw new DeploymentProviderException(
                    DeploymentFailure.NotFound, 404, $"The Railway project has no service built from {target.Repo.FullName}; pick another or create one.", platform: Railway);
            }

            if (linked.Repo is not null && !string.Equals(linked.Repo, target.Repo.FullName, StringComparison.OrdinalIgnoreCase))
            {
                throw new DeploymentProviderException(
                    DeploymentFailure.Rejected, 400, $"The Railway service is linked to {linked.Repo}, not {target.Repo.FullName}.", platform: Railway);
            }

            serviceId = linked.Id;
            serviceName = linked.Name;
        }

        var environments = await EnvironmentsAsync(auth, projectId, cancellationToken);
        var production = environments.FirstOrDefault(e => e.Name == RailwayRules.ProductionEnvironmentName) ?? (environments.Count > 0 ? environments[0] : null)
            ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "The Railway project has no environment.", platform: Railway);
        // FR-147: a second component in a shared project finds aide-preview already made; its instance there needs the
        // same settings (an environment created now copies them from production).
        var existingPreview = environments.FirstOrDefault(e => e.Name == RailwayRules.PreviewEnvironmentName)?.Id;
        string[] configured = existingPreview is null ? [production.Id] : [production.Id, existingPreview];
        if (request.Create)
        {
            serviceId = await FindOrCreateServiceAsync(auth, projectId, serviceName, target.Repo, cancellationToken);
            foreach (var environmentId in configured)
            {
                await ConfigureInstanceAsync(auth, serviceId, environmentId, target, cancellationToken);
            }
        }

        if (target.Component == DeploymentComponent.Backend)
        {
            // FR-146: a same-origin SPA reaches the backend at ${{<service>.RAILWAY_PRIVATE_DOMAIN}}:${{<service>.PORT}}, so
            // the backend needs PORT: set on create, and on link only where the service has none.
            foreach (var environmentId in configured)
            {
                await EnsurePortAsync(auth, projectId, environmentId, serviceId!, onlyIfMissing: !request.Create, cancellationToken);
            }
        }

        var productionUrl = await DomainAsync(auth, projectId, serviceId!, production.Id, cancellationToken);

        // FR-147: the persistent preview environment, copied from production once, never auto-deploying.
        var preview = existingPreview ?? await CreatePreviewEnvironmentAsync(auth, projectId, production.Id, cancellationToken);
        var previewUrl = await DomainAsync(auth, projectId, serviceId!, preview, cancellationToken);
        return new DeploymentSetupResult(
            RailwayRules.ResourceId(new RailwayResource(projectId, serviceId!, production.Id)), serviceName, productionUrl, preview, previewUrl);
    }

    public async Task<ProviderTargetCheck> VerifyTargetAsync(DeploymentAuth auth, DeploymentTargetRef target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (RailwayRules.ParseResourceId(target.ResourceId) is not { } resource)
        {
            return new ProviderTargetCheck(false, "The Railway target has no service; set it up again.", null);
        }

        var service = (await ServicesAsync(auth, resource.ProjectId, cancellationToken)).FirstOrDefault(s => s.Id == resource.ServiceId);
        if (service is null)
        {
            return new ProviderTargetCheck(false, "The Railway service no longer exists; set the target up again.", null);
        }

        var environments = await EnvironmentsAsync(auth, resource.ProjectId, cancellationToken);
        var previewReady = target.PreviewResourceId is { } previewId && environments.Any(e => e.Id == previewId);
        return service.Repo is not null && string.Equals(service.Repo, target.Repo.FullName, StringComparison.OrdinalIgnoreCase)
            ? new ProviderTargetCheck(true, null, previewReady)
            : new ProviderTargetCheck(false, $"The Railway service isn't built from {target.Repo.FullName}. Connect it in Railway, then verify again.", previewReady);
    }

    private async Task<string> FindOrCreateProjectAsync(DeploymentAuth auth, string name, CancellationToken cancellationToken)
    {
        if ((await ProjectsAsync(auth, cancellationToken)).FirstOrDefault(p => p.Name == name) is { } existing)
        {
            return existing.Id;
        }

        var data = await QueryAsync(
            auth,
            "mutation($input: ProjectCreateInput!) { projectCreate(input: $input) { id name } }",
            new { input = new { name, workspaceId = Workspace(auth), prDeploys = false, defaultEnvironmentName = RailwayRules.ProductionEnvironmentName } },
            cancellationToken);
        return Text(Property(data, "projectCreate"), "id")
            ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Railway created the project but returned no id.", platform: Railway);
    }

    private async Task<string> FindOrCreateServiceAsync(DeploymentAuth auth, string projectId, string name, DeploymentRepoRef repo, CancellationToken cancellationToken)
    {
        if ((await ServicesAsync(auth, projectId, cancellationToken)).FirstOrDefault(s => s.Name == name) is { } existing)
        {
            return existing.Repo is null || string.Equals(existing.Repo, repo.FullName, StringComparison.OrdinalIgnoreCase)
                ? existing.Id
                : throw new DeploymentProviderException(
                    DeploymentFailure.Rejected, 409, $"A Railway service named {name} exists already for another repository; pick another name.", platform: Railway);
        }

        var data = await QueryAsync(
            auth,
            "mutation($input: ServiceCreateInput!) { serviceCreate(input: $input) { id name } }",
            new { input = new { projectId, name, source = new { repo = repo.FullName }, branch = repo.DefaultBranch } },
            cancellationToken,
            ex => RailwayRules.IsGitNotConnected(ex.Message) ? DeploymentFailure.GitNotConnected : null);
        return Text(Property(data, "serviceCreate"), "id")
            ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Railway created the service but returned no id.", platform: Railway);
    }

    private async Task ConfigureInstanceAsync(DeploymentAuth auth, string serviceId, string environmentId, DeploymentTargetRef target, CancellationToken cancellationToken)
    {
        var settings = RailwayRules.InstanceSettings(target.BuildConfig, target.RootDirectory);
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rootDirectory"] = settings.RootDirectory,
            ["dockerfilePath"] = settings.DockerfilePath,
            ["buildCommand"] = settings.BuildCommand,
            ["startCommand"] = settings.StartCommand,
            ["healthcheckPath"] = settings.HealthcheckPath,
            ["watchPatterns"] = settings.WatchPatterns,
            ["preDeployCommand"] = settings.PreDeployCommand,
            ["builder"] = settings.DockerfilePath is null ? null : "DOCKERFILE",
        }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        await QueryAsync(
            auth,
            "mutation($serviceId: String!, $environmentId: String, $input: ServiceInstanceUpdateInput!) { serviceInstanceUpdate(serviceId: $serviceId, environmentId: $environmentId, input: $input) }",
            new { serviceId, environmentId, input },
            cancellationToken);
    }

    /// <summary>FR-146: sets <c>PORT</c> on the service in the environment (with <paramref name="onlyIfMissing"/>, only when it has none).</summary>
    private async Task EnsurePortAsync(
        DeploymentAuth auth, string projectId, string environmentId, string serviceId, bool onlyIfMissing, CancellationToken cancellationToken)
    {
        if (onlyIfMissing)
        {
            var data = await QueryAsync(
                auth,
                "query($projectId: String!, $environmentId: String!, $serviceId: String) { variables(projectId: $projectId, environmentId: $environmentId, serviceId: $serviceId) }",
                new { projectId, environmentId, serviceId },
                cancellationToken);
            if (Property(Property(data, "variables"), "PORT").ValueKind != JsonValueKind.Undefined)
            {
                return;
            }
        }

        await UpsertVariablesAsync(
            auth, projectId, environmentId, serviceId, new Dictionary<string, string> { ["PORT"] = RailwayRules.BackendPort }, cancellationToken);
    }

    private async Task<string> CreatePreviewEnvironmentAsync(DeploymentAuth auth, string projectId, string productionId, CancellationToken cancellationToken)
    {
        var data = await QueryAsync(
            auth,
            "mutation($input: EnvironmentCreateInput!) { environmentCreate(input: $input) { id name } }",
            new { input = new { projectId, name = RailwayRules.PreviewEnvironmentName, sourceEnvironmentId = productionId, skipInitialDeploys = true } },
            cancellationToken);
        return Text(Property(data, "environmentCreate"), "id")
            ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Railway created the preview environment but returned no id.", platform: Railway);
    }

    /// <summary>The service's Railway domain in the environment (made with <c>serviceDomainCreate</c> when it has none).</summary>
    private async Task<string?> DomainAsync(DeploymentAuth auth, string projectId, string serviceId, string environmentId, CancellationToken cancellationToken)
    {
        var data = await QueryAsync(
            auth,
            "query($projectId: String!, $serviceId: String!, $environmentId: String!) { domains(projectId: $projectId, serviceId: $serviceId, environmentId: $environmentId) { serviceDomains { domain } } }",
            new { projectId, serviceId, environmentId },
            cancellationToken);
        if (Items(Property(Property(data, "domains"), "serviceDomains")).Select(d => Text(d, "domain")).FirstOrDefault(d => d is not null) is { } existing)
        {
            return RailwayRules.Url(existing);
        }

        var created = await QueryAsync(
            auth,
            "mutation($input: ServiceDomainCreateInput!) { serviceDomainCreate(input: $input) { id domain } }",
            new { input = new { serviceId, environmentId } },
            cancellationToken);
        return RailwayRules.Url(Text(Property(created, "serviceDomainCreate"), "domain"));
    }

    private async Task<IReadOnlyList<RailwayNode>> ProjectsAsync(DeploymentAuth auth, CancellationToken cancellationToken)
    {
        var data = await QueryAsync(
            auth, "query($id: String!) { projects(workspaceId: $id, first: 100) { edges { node { id name } } } }", new { id = Workspace(auth) }, cancellationToken);
        return [.. Nodes(Property(data, "projects")).Where(n => Text(n, "id") is not null).Select(n => new RailwayNode(Text(n, "id")!, Text(n, "name") ?? Text(n, "id")!))];
    }

    private async Task<IReadOnlyList<RailwayNode>> EnvironmentsAsync(DeploymentAuth auth, string projectId, CancellationToken cancellationToken)
    {
        var data = await QueryAsync(auth, $"query($id: String!) {{ project(id: $id) {{ id {EnvironmentsFields} }} }}", new { id = projectId }, cancellationToken);
        return [.. Nodes(Property(Property(data, "project"), "environments")).Where(n => Text(n, "id") is not null).Select(n => new RailwayNode(Text(n, "id")!, Text(n, "name") ?? string.Empty))];
    }

    /// <summary>The project's services with the GitHub repository they build (<c>owner/name</c>, from any instance).</summary>
    private async Task<IReadOnlyList<RailwayNode>> ServicesAsync(DeploymentAuth auth, string projectId, CancellationToken cancellationToken)
    {
        var data = await QueryAsync(
            auth,
            "query($id: String!) { project(id: $id) { id services { edges { node { id name serviceInstances { edges { node { source { repo } } } } } } } } }",
            new { id = projectId },
            cancellationToken);
        return
        [
            .. Nodes(Property(Property(data, "project"), "services"))
                .Where(n => Text(n, "id") is not null)
                .Select(n => new RailwayNode(
                    Text(n, "id")!,
                    Text(n, "name") ?? Text(n, "id")!,
                    Nodes(Property(n, "serviceInstances")).Select(i => Text(Property(i, "source"), "repo")).FirstOrDefault(r => r is not null))),
        ];
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement connection) => Items(connection, "edges").Select(e => Property(e, "node"));

    private static string Workspace(DeploymentAuth auth) => auth.AccountId
        ?? throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, "The Railway connection has no workspace; give its workspace id in Settings → Connections.", platform: DeploymentProvider.Railway);

    /// <summary>
    /// A GraphQL request; returns <c>data</c> (cloned, so it outlives the document). <c>errors</c> in a 200 answer are
    /// failures: <c>Not Authorized</c> rejected, rate limits 429-like (§4.20: honoured without counting an attempt).
    /// </summary>
    private async Task<JsonElement> QueryAsync(
        DeploymentAuth auth, string query, object? variables, CancellationToken cancellationToken, Func<PlatformApiException, DeploymentFailure?>? classify = null) =>
        await PlatformDeploymentCalls.Call(
            Railway,
            async () =>
            {
                using var json = await http.SendAsync(
                    PlatformProvider.Railway, PlatformHosts.RailwayClient, HttpMethod.Post, "graphql/v2", auth.Token, new { query, variables }, cancellationToken);
                var errors = Items(json.RootElement, "errors").ToList();
                if (errors.Count > 0)
                {
                    var message = Text(errors[0], "message") ?? "request failed";
                    var failure = PlatformConnectionRules.ClassifyRailwayError(message);
                    throw new PlatformApiException(failure, failure == PlatformFailure.RateLimited ? 429 : 400, $"Railway: {PlatformHttp.Clean(message)}");
                }

                return Property(json.RootElement, "data").Clone();
            },
            classify);
}

/// <summary>A Railway project, environment or service: id, name and (services) the GitHub repository it builds.</summary>
internal sealed record RailwayNode(string Id, string Name, string? Repo = null);
