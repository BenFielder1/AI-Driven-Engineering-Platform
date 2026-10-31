using System.Text.Json;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Platforms;
using static Aide.Infrastructure.Deployments.PlatformDeploymentCalls;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// FR-144, FR-145, FR-147, FR-149, FR-150, §4.20 Render through an account API key (<c>api.render.com/v1</c>), in the
/// connection's workspace (<see cref="DeploymentAuth.AccountId"/>, the <c>ownerId</c> chosen from <c>GET /v1/owners</c>):
/// <list type="bullet">
/// <item>Create: <c>POST /v1/services</c> per <see cref="RenderRules.ServiceSpec"/> (production: <c>branch: main</c>,
/// <c>autoDeployTrigger: commit</c>), then the <c>&lt;name&gt;-preview</c> service (<c>autoDeployTrigger: off</c>), whose id
/// is the target's <c>preview_resource_id</c>. A repository Render can't reach: <c>PLATFORM_GIT_NOT_CONNECTED</c>.</item>
/// <item>Link: <c>GET /v1/services/{id}</c> (linked to this repository), then find or create its <c>-preview</c>.</item>
/// <item>Env: per service, <c>GET</c> then <c>PUT /v1/services/{id}/env-vars</c> with the full merged set (never deploys);
/// a static site's <c>/api/*</c> rewrite follows <c>API_ORIGIN</c> (<c>PUT /routes</c>).</item>
/// </list>
/// Deploys, adoption, logs (30 a minute per user, <see cref="RenderLogRateLimiter"/>), cancel and rollback are in the
/// <c>.Deploys</c> part.
/// </summary>
public sealed partial class RenderDeploymentProvider(PlatformHttp http, RenderLogRateLimiter logLimiter) : IDeploymentProvider
{
    private const DeploymentProvider Render = DeploymentProvider.Render;

    public DeploymentProvider Kind => Render;

    public DeploymentProvider Platform => Render;

    public DeploymentCapabilities Capabilities => DeploymentPlatforms.Capabilities(Render);

    public async Task<DeploymentAccount> ValidateConnectionAsync(DeploymentAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        using var user = await SendAsync(auth, HttpMethod.Get, "users", null, cancellationToken);
        return new DeploymentAccount(
            Text(user.RootElement, "email") ?? "render", Text(user.RootElement, "name"), auth.AccountId, null, VercelTokenScope.Account, null);
    }

    public async Task<IReadOnlyList<DeploymentResource>> ListResourcesAsync(DeploymentAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        return [.. (await ListServicesAsync(auth, null, cancellationToken)).Select(s => new DeploymentResource(s.Id, s.Name, s.Repo))];
    }

    public async Task<DeploymentSetupResult> SetupTargetAsync(DeploymentAuth auth, DeploymentSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Target;
        RenderService production;
        if (!request.Create)
        {
            production = await GetServiceAsync(auth, request.LinkResourceId!, cancellationToken);
            if (production.Repo is not null && !RepoMatches(production.Repo, target.Repo))
            {
                throw new DeploymentProviderException(
                    DeploymentFailure.Rejected, 400, $"The Render service is linked to {production.Repo}, not {target.Repo.FullName}.", platform: Render);
            }
        }
        else
        {
            var name = string.IsNullOrEmpty(request.Name)
                ? throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, "A new Render service needs a name.", platform: Render)
                : request.Name;
            production = await FindOrCreateAsync(
                auth, target, RenderRules.ServiceSpec(target.Component, target.BuildConfig, target.RootDirectory, name, false, request.Plan, request.Region, target.Repo.DefaultBranch), cancellationToken);
        }

        // FR-147: the persistent preview service, deployed only by AIDE (auto-deploy off).
        var preview = await FindOrCreateAsync(
            auth,
            target,
            RenderRules.ServiceSpec(target.Component, target.BuildConfig, target.RootDirectory, production.Name, true, request.Plan ?? production.Plan, request.Region ?? production.Region, target.Repo.DefaultBranch),
            cancellationToken);
        return new DeploymentSetupResult(production.Id, production.Name, production.Url, preview.Id, preview.Url);
    }

    public async Task<ProviderTargetCheck> VerifyTargetAsync(DeploymentAuth auth, DeploymentTargetRef target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        RenderService service;
        try
        {
            service = await GetServiceAsync(auth, target.ResourceId!, cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure == DeploymentFailure.NotFound)
        {
            return new ProviderTargetCheck(false, "The Render service no longer exists; set the target up again.", null);
        }

        if (service.Repo is null || !RepoMatches(service.Repo, target.Repo))
        {
            return new ProviderTargetCheck(false, $"The Render service isn't connected to {target.Repo.FullName}. Connect it in Render, then verify again.", null);
        }

        bool previewReady;
        try
        {
            previewReady = target.PreviewResourceId is { } previewId
                && (await GetServiceAsync(auth, previewId, cancellationToken)).AutoDeployTrigger == RenderRules.AutoDeployOff;
        }
        catch (DeploymentProviderException ex) when (ex.Failure == DeploymentFailure.NotFound)
        {
            previewReady = false;
        }

        // §4.20 branch control: production tracks main only, so aide/** never auto-deploys.
        return service.Branch is { } branch && branch != target.Repo.DefaultBranch
            ? new ProviderTargetCheck(true, $"The Render service tracks {branch}, not {target.Repo.DefaultBranch}.", previewReady)
            : new ProviderTargetCheck(true, null, previewReady);
    }

    public async Task<IReadOnlyList<DeploymentEnvWritten>> SyncEnvAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvSyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        var written = new List<DeploymentEnvWritten>();
        var deletes = request.Deletes.Select(RenderRules.ParseEnvId).OfType<(string Key, DeploymentEnvironment Environment)>().ToList();
        foreach (var environment in new[] { DeploymentEnvironment.Production, DeploymentEnvironment.Preview })
        {
            var serviceId = environment == DeploymentEnvironment.Production ? target.ResourceId : target.PreviewResourceId;
            var writes = request.Writes.Where(w => w.Target == environment).ToList();
            var gone = deletes.Where(d => d.Environment == environment).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
            if (serviceId is null || (writes.Count == 0 && gone.Count == 0))
            {
                continue;
            }

            // §4.20: PUT replaces the whole set, so merge with what the service has (never deploys).
            var current = await ReadEnvAsync(auth, serviceId, cancellationToken);
            var merged = RenderRules.MergeEnv(current, writes.ToDictionary(w => w.Key, w => w.Value, StringComparer.Ordinal), gone);
            using (var _ = await SendAsync(
                auth, HttpMethod.Put, $"services/{Uri.EscapeDataString(serviceId)}/env-vars", merged.Select(kv => new { key = kv.Key, value = kv.Value }).ToList(), cancellationToken))
            {
            }

            written.AddRange(writes.Select(w => new DeploymentEnvWritten(w.Key, w.Target, RenderRules.EnvId(w.Key, w.Target))));
            if (writes.FirstOrDefault(w => w.Key == DerivedEnvRules.ApiOrigin) is { } origin && target.Component == DeploymentComponent.Frontend)
            {
                await SetApiRouteAsync(auth, serviceId, origin.Value, cancellationToken);
            }
        }

        return written;
    }

    /// <summary>FR-146: a static site's <c>/api/*</c> rewrite to <c>API_ORIGIN</c> (web services proxy in their own code).</summary>
    private async Task SetApiRouteAsync(DeploymentAuth auth, string serviceId, string apiOrigin, CancellationToken cancellationToken)
    {
        var service = await GetServiceAsync(auth, serviceId, cancellationToken);
        if (service.Type != RenderRules.StaticSite)
        {
            return;
        }

        // Render's PUT replaces every rule: keep the existing ones (the SPA fallback) behind the proxy.
        var existing = (await ReadAllAsync(auth, $"services/{Uri.EscapeDataString(serviceId)}/routes", "route", cancellationToken))
            .Where(r => Text(r, "source") is not null && Text(r, "destination") is not null)
            .OrderBy(r => int.TryParse(Text(r, "priority"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : int.MaxValue)
            .Select(r => new RenderRoute(Text(r, "type") ?? "rewrite", Text(r, "source")!, Text(r, "destination")!))
            .ToList();
        var routes = RenderRules.MergeRoutes(existing, RenderRules.ApiProxyRoute(apiOrigin));
        using var _ = await SendAsync(
            auth,
            HttpMethod.Put,
            $"services/{Uri.EscapeDataString(serviceId)}/routes",
            routes.Select((r, i) => new { type = r.Type, source = r.Source, destination = r.Destination, priority = i }).ToList(),
            cancellationToken);
    }

    private async Task<Dictionary<string, string>> ReadEnvAsync(DeploymentAuth auth, string serviceId, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var envVar in await ReadAllAsync(auth, $"services/{Uri.EscapeDataString(serviceId)}/env-vars", "envVar", cancellationToken))
        {
            if (Text(envVar, "key") is { } key)
            {
                values[key] = Text(envVar, "value") ?? string.Empty;
            }
        }

        return values;
    }

    /// <summary>
    /// Every item of a Render list (<c>[{&lt;wrapper&gt;: {…}, cursor}]</c>), following the cursor 100 at a time until a page
    /// is short or has no cursor (a full PUT must not drop what a later page holds). Items are cloned.
    /// </summary>
    private async Task<IReadOnlyList<JsonElement>> ReadAllAsync(DeploymentAuth auth, string path, string wrapper, CancellationToken cancellationToken)
    {
        const int PageSize = 100;
        const int MaxPages = 50;
        var all = new List<JsonElement>();
        string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var query = $"{path}?limit={PageSize}" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var list = await SendAsync(auth, HttpMethod.Get, query, null, cancellationToken);
            var items = Items(list.RootElement).ToList();
            all.AddRange(items.Select(i => (Property(i, wrapper).ValueKind == JsonValueKind.Object ? Property(i, wrapper) : i).Clone()));
            cursor = items.Count > 0 ? Text(items[^1], "cursor") : null;
            if (items.Count < PageSize || cursor is null)
            {
                break;
            }
        }

        return all;
    }

    /// <summary>The service named <c>spec.Name</c> in the workspace (a retry finds what it created), else a new one.</summary>
    private async Task<RenderService> FindOrCreateAsync(DeploymentAuth auth, DeploymentTargetRef target, RenderServiceSpec spec, CancellationToken cancellationToken)
    {
        var existing = (await ListServicesAsync(auth, spec.Name, cancellationToken)).FirstOrDefault(s => s.Name == spec.Name);
        if (existing is not null)
        {
            return existing.Repo is null || RepoMatches(existing.Repo, target.Repo)
                ? existing
                : throw new DeploymentProviderException(
                    DeploymentFailure.Rejected, 409, $"A Render service named {spec.Name} exists already for another repository; pick another name.", platform: Render);
        }

        var owner = auth.AccountId
            ?? throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, "The Render connection has no workspace; choose one in Settings → Connections.", platform: Render);
        var repoUrl = RenderRules.RepoUrl(target.Repo.Host, target.Repo.FullName)
            ?? throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, "Render deploys from a GitHub, GitLab or Bitbucket repository.", platform: Render);
        using var created = await PlatformDeploymentCalls.Call(
            Render,
            () => http.SendAsync(PlatformProvider.Render, PlatformHosts.RenderClient, HttpMethod.Post, "services", auth.Token, RenderPayloads.CreateService(spec, owner, repoUrl), cancellationToken),
            ex => RenderRules.IsGitNotConnected(ex.Status, ex.Message) ? DeploymentFailure.GitNotConnected
                : RenderRules.IsPlanLimit(ex.Status, ex.Message) ? DeploymentFailure.PlanLimit
                : null);
        var root = Property(created.RootElement, "service").ValueKind == JsonValueKind.Object ? Property(created.RootElement, "service") : created.RootElement;
        return RenderService.From(root) is { } service
            ? service
            : throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Render created the service but returned no id.", platform: Render);
    }

    private async Task<RenderService> GetServiceAsync(DeploymentAuth auth, string serviceId, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(auth, HttpMethod.Get, $"services/{Uri.EscapeDataString(serviceId)}", null, cancellationToken);
        return RenderService.From(json.RootElement)
            ?? throw new DeploymentProviderException(DeploymentFailure.NotFound, 404, "Render returned no service.", platform: Render);
    }

    private async Task<IReadOnlyList<RenderService>> ListServicesAsync(DeploymentAuth auth, string? name, CancellationToken cancellationToken)
    {
        var query = "services?limit=100"
            + (auth.AccountId is { } owner ? $"&ownerId={Uri.EscapeDataString(owner)}" : string.Empty)
            + (name is null ? string.Empty : $"&name={Uri.EscapeDataString(name)}");
        using var json = await SendAsync(auth, HttpMethod.Get, query, null, cancellationToken);
        return
        [
            .. Items(json.RootElement)
                .Select(s => Property(s, "service").ValueKind == JsonValueKind.Object ? Property(s, "service") : s)
                .Select(RenderService.From)
                .OfType<RenderService>(),
        ];
    }

    private static bool RepoMatches(string repoUrl, DeploymentRepoRef repo) =>
        repo.FullName is not null
        && repoUrl.TrimEnd('/').EndsWith("/" + repo.FullName, StringComparison.OrdinalIgnoreCase);

    private Task<JsonDocument> SendAsync(DeploymentAuth auth, HttpMethod method, string path, object? body, CancellationToken cancellationToken) =>
        PlatformDeploymentCalls.Call(Render, () => http.SendAsync(PlatformProvider.Render, PlatformHosts.RenderClient, method, path, auth.Token, body, cancellationToken));
}

/// <summary>A Render service as AIDE reads it: id, name, type, repository URL, branch, auto-deploy trigger, URL, plan, region.</summary>
internal sealed record RenderService(
    string Id, string Name, string? Type, string? Repo, string? Branch, string? AutoDeployTrigger, string? Url, string? Plan, string? Region)
{
    public static RenderService? From(JsonElement service)
    {
        if (Text(service, "id") is not { } id)
        {
            return null;
        }

        var details = Property(service, "serviceDetails");
        var trigger = Text(service, "autoDeployTrigger")
            ?? (Text(service, "autoDeploy") is { } legacy ? legacy == "yes" ? RenderRules.AutoDeployOnCommit : RenderRules.AutoDeployOff : null);
        return new RenderService(
            id,
            Text(service, "name") ?? id,
            Text(service, "type"),
            Text(service, "repo"),
            Text(service, "branch"),
            trigger,
            Https(Text(details, "url")),
            Text(details, "plan"),
            Text(details, "region"));
    }
}
