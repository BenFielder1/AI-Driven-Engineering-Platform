using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Platforms;
using static Aide.Infrastructure.Deployments.PlatformDeploymentCalls;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// FR-144, FR-145, FR-149, FR-150, §4.20 Cloudflare through a user API token (<c>api.cloudflare.com/client/v4</c>). A
/// target is a <b>Worker</b> with Workers Builds (resource id = the script name; <c>preview_resource_id</c> =
/// <c>&lt;preview trigger&gt;|&lt;production trigger&gt;</c>), or an existing <b>Pages</b> project (adopt only; resource
/// id <c>pages:&lt;project&gt;</c>, <see cref="DeploymentRollbackRules.IsCloudflarePages"/>).
/// <list type="bullet">
/// <item>Workers create: the Worker (a placeholder module when it doesn't exist yet, §11 bootstrap), <c>PUT
/// /builds/repos/connections</c> (the Cloudflare Git app must be installed: else <c>PLATFORM_GIT_NOT_CONNECTED</c>),
/// <c>POST /builds/tokens</c> (reused per API token) and the two triggers of <see cref="CloudflareRules"/>. Link: an
/// existing Worker whose Workers Builds production trigger exists; the preview trigger is created when missing.</item>
/// <item>Pages link: the project must build this repository; previews are set to <c>custom</c> excluding
/// <c>aide/*</c>. Creating a Pages project is refused (<c>VALIDATION</c>).</item>
/// </list>
/// </summary>
public sealed partial class CloudflareDeploymentProvider(PlatformHttp http) : IDeploymentProvider
{
    private const DeploymentProvider Cloudflare = DeploymentProvider.Cloudflare;

    public DeploymentProvider Kind => Cloudflare;

    public DeploymentProvider Platform => Cloudflare;

    public DeploymentCapabilities Capabilities => DeploymentPlatforms.Capabilities(Cloudflare);

    public async Task<DeploymentAccount> ValidateConnectionAsync(DeploymentAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        using var verify = await SendAsync(auth, HttpMethod.Get, "user/tokens/verify", null, cancellationToken);
        return new DeploymentAccount(Text(Result(verify), "id") ?? "cloudflare", null, null, null, VercelTokenScope.Account, null);
    }

    public async Task<IReadOnlyList<DeploymentResource>> ListResourcesAsync(DeploymentAuth auth, CancellationToken cancellationToken)
    {
        var account = Account(auth);
        using var workers = await SendAsync(auth, HttpMethod.Get, $"accounts/{account}/workers/scripts", null, cancellationToken);
        using var pages = await SendAsync(auth, HttpMethod.Get, $"accounts/{account}/pages/projects?per_page=100", null, cancellationToken);
        return
        [
            .. Items(Result(workers)).Where(w => Text(w, "id") is not null).Select(w => new DeploymentResource(Text(w, "id")!, Text(w, "id")!, "Worker")),
            .. Items(Result(pages)).Where(p => Text(p, "name") is not null)
                .Select(p => new DeploymentResource(DeploymentRollbackRules.CloudflarePagesResourceId(Text(p, "name")!), Text(p, "name")!, "Pages project")),
        ];
    }

    public async Task<DeploymentSetupResult> SetupTargetAsync(DeploymentAuth auth, DeploymentSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Target;
        if (request.Create)
        {
            var name = string.IsNullOrEmpty(request.Name)
                ? throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, "A new Worker needs a name.", platform: Cloudflare)
                : request.Name;
            if (DeploymentRollbackRules.IsCloudflarePages(name))
            {
                throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, "AIDE can't create Cloudflare Pages projects; link an existing one, or create a Worker.", platform: Cloudflare);
            }

            await EnsureWorkerAsync(auth, name, cancellationToken);
            var connection = await RepoConnectionAsync(auth, target.Repo, cancellationToken);
            var token = await BuildTokenAsync(auth, name, cancellationToken);
            return await WorkerResultAsync(auth, target, name, connection, token, cancellationToken);
        }

        var id = request.LinkResourceId!;
        if (!DeploymentRollbackRules.IsCloudflarePages(id) && await ScriptTagAsync(auth, id, cancellationToken) is not null)
        {
            return await WorkerResultAsync(auth, target, id, null, null, cancellationToken);
        }

        var project = DeploymentRollbackRules.CloudflarePagesProject(id) ?? id;
        return await LinkPagesAsync(auth, target, project, cancellationToken);
    }

    public async Task<ProviderTargetCheck> VerifyTargetAsync(DeploymentAuth auth, DeploymentTargetRef target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (DeploymentRollbackRules.CloudflarePagesProject(target.ResourceId) is { } project)
        {
            try
            {
                using var json = await SendAsync(auth, HttpMethod.Get, $"accounts/{Account(auth)}/pages/projects/{Uri.EscapeDataString(project)}", null, cancellationToken);
                var linked = PagesRepo(Result(json));
                return linked is null ? new ProviderTargetCheck(false, "The Pages project isn't connected to a Git repository.", null)
                    : DeploymentSettingsRules.LinkMatches(linked, target.Repo.FullName) ? new ProviderTargetCheck(true, null, true)
                    : new ProviderTargetCheck(false, $"The Pages project builds {linked}, not {target.Repo.FullName}.", null);
            }
            catch (DeploymentProviderException ex) when (ex.Failure == DeploymentFailure.NotFound)
            {
                return new ProviderTargetCheck(false, "The Pages project no longer exists; set the target up again.", null);
            }
        }

        if (await ScriptTagAsync(auth, target.ResourceId!, cancellationToken) is null)
        {
            return new ProviderTargetCheck(false, "The Worker no longer exists; set the target up again.", null);
        }

        var (preview, production) = CloudflareRules.ParseTriggerIds(target.PreviewResourceId);
        return production is null
            ? new ProviderTargetCheck(false, "The Worker has no Workers Builds production trigger. Connect it to the repository in Cloudflare, then set the target up again.", preview is not null)
            : new ProviderTargetCheck(true, null, preview is not null);
    }

    /// <summary>Uploads the placeholder module when the Worker doesn't exist yet (§11 first-Worker bootstrap).</summary>
    private async Task EnsureWorkerAsync(DeploymentAuth auth, string name, CancellationToken cancellationToken)
    {
        if (await ScriptTagAsync(auth, name, cancellationToken) is not null)
        {
            return;
        }

        var form = new MultipartFormDataContent();
        form.Add(new StringContent("""{"main_module":"index.js","compatibility_date":"2026-01-01"}""", Encoding.UTF8, "application/json"), "metadata");
        var module = new StringContent(CloudflareRules.PlaceholderModule, Encoding.UTF8);
        module.Headers.ContentType = new MediaTypeHeaderValue("application/javascript+module");
        form.Add(module, "index.js", "index.js");
        using var _ = await SendContentAsync(auth, HttpMethod.Put, $"accounts/{Account(auth)}/workers/scripts/{Uri.EscapeDataString(name)}", form, cancellationToken);
    }

    /// <summary>The Worker's tag (Workers Builds' <c>external_script_id</c>), or <c>null</c> when there's no such Worker.</summary>
    private async Task<string?> ScriptTagAsync(DeploymentAuth auth, string name, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(auth, HttpMethod.Get, $"accounts/{Account(auth)}/workers/scripts", null, cancellationToken);
        var script = Items(Result(json)).FirstOrDefault(s => Text(s, "id") == name);
        return script.ValueKind == JsonValueKind.Object ? Text(script, "tag") ?? name : null;
    }

    /// <summary><c>PUT /builds/repos/connections</c>: fails with <c>PLATFORM_GIT_NOT_CONNECTED</c> when the Git app can't see the repository.</summary>
    private async Task<string> RepoConnectionAsync(DeploymentAuth auth, DeploymentRepoRef repo, CancellationToken cancellationToken)
    {
        if (repo.Host is not ("github" or "gitlab") || repo.Owner is null || repo.Name is null)
        {
            throw new DeploymentProviderException(
                DeploymentFailure.Rejected, 400, "Workers Builds connects GitHub and GitLab repositories only.", platform: Cloudflare);
        }

        var body = new
        {
            provider_type = repo.Host,
            provider_account_id = repo.Owner,
            provider_account_name = repo.Owner,
            repo_id = repo.GitHubRepoId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? repo.ExternalId ?? repo.FullName,
            repo_name = repo.Name,
        };
        using var json = await PlatformDeploymentCalls.Call(
            Cloudflare,
            () => http.SendAsync(PlatformProvider.Cloudflare, PlatformHosts.CloudflareClient, HttpMethod.Put, $"accounts/{Account(auth)}/builds/repos/connections", auth.Token, body, cancellationToken),
            ex => ex.Status is >= 400 and < 500 and not 401 and not 429 ? DeploymentFailure.GitNotConnected : null);
        return Text(Result(json), "repo_connection_uuid")
            ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Cloudflare connected the repository but returned no id.", platform: Cloudflare);
    }

    /// <summary>The build token for this API token (<c>POST /builds/tokens</c>, reused when one exists).</summary>
    private async Task<string> BuildTokenAsync(DeploymentAuth auth, string name, CancellationToken cancellationToken)
    {
        using var verify = await SendAsync(auth, HttpMethod.Get, "user/tokens/verify", null, cancellationToken);
        var tokenId = Text(Result(verify), "id");
        var account = Account(auth);
        using (var existing = await SendAsync(auth, HttpMethod.Get, $"accounts/{account}/builds/tokens", null, cancellationToken))
        {
            var match = Items(Result(existing)).FirstOrDefault(t => tokenId is not null && Text(t, "cloudflare_token_id") == tokenId);
            if (match.ValueKind == JsonValueKind.Object && Text(match, "build_token_uuid") is { } reused)
            {
                return reused;
            }
        }

        using var created = await SendAsync(
            auth,
            HttpMethod.Post,
            $"accounts/{account}/builds/tokens",
            new { build_token_name = $"aide-{name}", build_token_secret = auth.Token, cloudflare_token_id = tokenId },
            cancellationToken);
        return Text(Result(created), "build_token_uuid")
            ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Cloudflare created the build token but returned no id.", platform: Cloudflare);
    }

    private static string Account(DeploymentAuth auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        return Uri.EscapeDataString(auth.AccountId
            ?? throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, "The Cloudflare connection has no account; choose one in Settings → Connections.", platform: Cloudflare));
    }

    /// <summary>Cloudflare's envelope: <c>{"success", "result", "result_info"}</c>.</summary>
    private static JsonElement Result(JsonDocument json) => Property(json.RootElement, "result");

    private Task<JsonDocument> SendAsync(DeploymentAuth auth, HttpMethod method, string path, object? body, CancellationToken cancellationToken) =>
        PlatformDeploymentCalls.Call(Cloudflare, () => http.SendAsync(PlatformProvider.Cloudflare, PlatformHosts.CloudflareClient, method, path, auth.Token, body, cancellationToken));

    private Task<JsonDocument> SendContentAsync(DeploymentAuth auth, HttpMethod method, string path, HttpContent content, CancellationToken cancellationToken) =>
        PlatformDeploymentCalls.Call(Cloudflare, () => http.SendContentAsync(PlatformProvider.Cloudflare, PlatformHosts.CloudflareClient, method, path, auth.Token, content, cancellationToken));
}
