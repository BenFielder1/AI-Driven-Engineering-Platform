using System.Text.Json;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Platforms;
using static Aide.Infrastructure.Deployments.PlatformDeploymentCalls;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// FR-144, FR-145, FR-149, FR-150, §4.20 Netlify through a personal access token (<c>api.netlify.com/api/v1</c>):
/// <list type="bullet">
/// <item>Create: <c>POST /{account_slug}/sites</c> with <c>repo {provider, id, repo_path, repo_branch, cmd, dir, base,
/// allowed_branches, installation_id}</c>. GitHub needs the Netlify GitHub App installation id, read from another site of
/// the account linked to the same GitHub owner; without one: <c>PLATFORM_GIT_NOT_CONNECTED</c>. GitLab and Bitbucket
/// (D-V4-16): Tevrin adds a push webhook to <c>https://api.netlify.com/hooks/&lt;provider&gt;</c> and a read-only deploy key from
/// <c>POST /deploy_keys</c> to the repository through the project's Git binding (<see cref="IGitHostRepoLinks"/>), then
/// creates the site with <c>deploy_key_id</c>. A retry reuses what is there. The webhook goes first, so a connection that
/// can't add it stops setup before anything is made on Netlify.</item>
/// <item>Link: <c>GET /sites/{id}</c> (linked to this repository, or to none). Both then set branch control:
/// <c>allowed_branches: [production branch]</c> and Deploy Previews off (<c>skip_prs</c>).</item>
/// <item>Preview: <c>POST /sites/{id}/builds?branch=&amp;title=aide:&lt;id&gt;</c>; the deploy must be of the PR head
/// (<c>commit_ref</c>), else it is re-triggered once. Production: adopt <c>GET /sites/{id}/deploys?production=true</c>
/// with <c>commit_ref</c> = the merge SHA, else <c>POST /builds</c> on the production branch.</item>
/// <item>Rollback: <c>POST /sites/{id}/deploys/{deploy_id}/restore</c>. Logs: <c>error_message</c> + <c>admin_url</c>.</item>
/// <item>Env: <c>/accounts/{account_id}/env?site_id=</c>, contexts per <see cref="NetlifyRules.ContextsOf"/>,
/// <c>is_secret</c> for sensitive values.</item>
/// </list>
/// Deploy creation is limited before calling (<see cref="NetlifyDeployRateLimiter"/>).
/// </summary>
public sealed partial class NetlifyDeploymentProvider(PlatformHttp http, NetlifyDeployRateLimiter limiter, IGitHostRepoLinks repoLinks) : IDeploymentProvider
{
    private const DeploymentProvider Netlify = DeploymentProvider.Netlify;

    public DeploymentProvider Kind => Netlify;

    public DeploymentProvider Platform => Netlify;

    public DeploymentCapabilities Capabilities => DeploymentPlatforms.Capabilities(Netlify);

    public async Task<DeploymentAccount> ValidateConnectionAsync(DeploymentAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        using var user = await SendAsync(auth, HttpMethod.Get, "user", null, cancellationToken);
        return new DeploymentAccount(Text(user.RootElement, "id") ?? "netlify", Text(user.RootElement, "full_name"), null, null, VercelTokenScope.Account, null);
    }

    public async Task<IReadOnlyList<DeploymentResource>> ListResourcesAsync(DeploymentAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        using var sites = await SendAsync(auth, HttpMethod.Get, SitesPath(auth) + "?per_page=100", null, cancellationToken);
        return
        [
            .. Items(sites.RootElement)
                .Where(s => Text(s, "id") is not null)
                .Select(s => new DeploymentResource(Text(s, "id")!, Text(s, "name") ?? Text(s, "id")!, Text(Property(s, "build_settings"), "repo_path"))),
        ];
    }

    public async Task<DeploymentSetupResult> SetupTargetAsync(DeploymentAuth auth, DeploymentSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Target;
        var repo = target.Repo;
        string siteId;
        if (!request.Create)
        {
            using var linked = await SendAsync(auth, HttpMethod.Get, $"sites/{Uri.EscapeDataString(request.LinkResourceId!)}", null, cancellationToken);
            var linkedRepo = Text(Property(linked.RootElement, "build_settings"), "repo_path");
            if (linkedRepo is not null && !DeploymentSettingsRules.LinkMatches(linkedRepo, repo.FullName))
            {
                throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, $"The Netlify site is linked to {linkedRepo}, not {repo.FullName}.", platform: Netlify);
            }

            siteId = Text(linked.RootElement, "id") ?? request.LinkResourceId!;
        }
        else
        {
            siteId = await CreateSiteAsync(auth, request, cancellationToken);
        }

        // §4.20 branch control: only the production branch auto-deploys, and Deploy Previews are off.
        using var site = await SendAsync(
            auth,
            HttpMethod.Patch,
            $"sites/{Uri.EscapeDataString(siteId)}",
            new { build_settings = new { allowed_branches = new[] { repo.DefaultBranch }, skip_prs = true } },
            cancellationToken);
        return new DeploymentSetupResult(
            siteId,
            Text(site.RootElement, "name") ?? request.Name ?? siteId,
            Https(Text(site.RootElement, "ssl_url") ?? Text(site.RootElement, "url")),
            null,
            null);
    }

    public async Task<ProviderTargetCheck> VerifyTargetAsync(DeploymentAuth auth, DeploymentTargetRef target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        JsonDocument site;
        try
        {
            site = await SendAsync(auth, HttpMethod.Get, $"sites/{Uri.EscapeDataString(target.ResourceId!)}", null, cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure == DeploymentFailure.NotFound)
        {
            return new ProviderTargetCheck(false, "The Netlify site no longer exists; set the target up again.", null);
        }

        using (site)
        {
            var settings = Property(site.RootElement, "build_settings");
            var linked = Text(settings, "repo_path");
            if (linked is null)
            {
                return new ProviderTargetCheck(false, "The Netlify site isn't connected to a Git repository. Connect it in Netlify, then verify again.", null);
            }

            return DeploymentSettingsRules.LinkMatches(linked, target.Repo.FullName)
                ? new ProviderTargetCheck(true, null, true)
                : new ProviderTargetCheck(false, $"The Netlify site is linked to {linked}, not {target.Repo.FullName}.", null);
        }
    }

    private async Task<string> CreateSiteAsync(DeploymentAuth auth, DeploymentSetupRequest request, CancellationToken cancellationToken)
    {
        var target = request.Target;
        var repo = target.Repo;
        var linkedByKey = repo.Host is "gitlab" or "bitbucket";
        if (repo.Host != "github" && !linkedByKey)
        {
            throw new DeploymentProviderException(DeploymentFailure.NotSupported, null, "Netlify builds only from a GitHub, GitLab or Bitbucket repository.", platform: Netlify);
        }

        var name = string.IsNullOrEmpty(request.Name)
            ? throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, "A new Netlify site needs a name.", platform: Netlify)
            : request.Name;

        // A retry after a later failure finds the site it created.
        foreach (var existing in await ListResourcesAsync(auth, cancellationToken))
        {
            if (existing.Name == name)
            {
                if (existing.Detail is null || !DeploymentSettingsRules.LinkMatches(existing.Detail, repo.FullName))
                {
                    throw new DeploymentProviderException(DeploymentFailure.Rejected, 409, "A Netlify site with this name exists already; link it instead or pick another name.", platform: Netlify);
                }

                if (linkedByKey)
                {
                    await EnsureWebhookAsync(target, cancellationToken);
                }

                return existing.Id;
            }
        }

        if (linkedByKey)
        {
            return await CreateSiteWithDeployKeyAsync(auth, target, name, cancellationToken);
        }

        var installation = await InstallationIdAsync(auth, repo, cancellationToken)
            ?? throw new DeploymentProviderException(
                DeploymentFailure.GitNotConnected,
                409,
                $"Netlify can't see {repo.Owner} on GitHub: install the Netlify GitHub App for it (or connect one site to it in Netlify), then try again.",
                NetlifyRules.GitHubInstallUrl,
                platform: Netlify);
        var config = target.BuildConfig;
        var body = new
        {
            name,
            repo = new
            {
                provider = "github",
                id = repo.GitHubRepoId,
                repo_path = repo.FullName,
                repo_branch = repo.DefaultBranch,
                cmd = config.BuildCommand,
                dir = config.OutputDirectory,
                @base = target.RootDirectory is "" or "." ? null : target.RootDirectory,
                allowed_branches = new[] { repo.DefaultBranch },
                installation_id = installation,
            },
        };
        return await PostSiteAsync(auth, body, cancellationToken);
    }

    /// <summary>
    /// D-V4-16: the webhook, the read-only deploy key, then the site with <c>deploy_key_id</c>. <c>repo.id</c> is sent only
    /// for a numeric id (GitLab's project id); a Bitbucket repository is named by <c>repo_path</c>.
    /// </summary>
    private async Task<string> CreateSiteWithDeployKeyAsync(DeploymentAuth auth, DeploymentTargetRef target, string name, CancellationToken cancellationToken)
    {
        await EnsureWebhookAsync(target, cancellationToken);
        var deployKeyId = await EnsureDeployKeyAsync(auth, target, cancellationToken);
        var repo = target.Repo;
        var config = target.BuildConfig;
        var repoBody = new Dictionary<string, object?>
        {
            ["provider"] = repo.Host,
            ["repo_path"] = repo.FullName,
            ["repo_branch"] = repo.DefaultBranch,
            ["cmd"] = config.BuildCommand,
            ["dir"] = config.OutputDirectory,
            ["base"] = target.RootDirectory is "" or "." ? null : target.RootDirectory,
            ["allowed_branches"] = new[] { repo.DefaultBranch },
            ["deploy_key_id"] = deployKeyId,
            ["repo_url"] = repo.WebUrl,
        };
        if (long.TryParse(repo.ExternalId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var numericId))
        {
            repoBody["id"] = numericId;
        }

        return await PostSiteAsync(auth, new { name, repo = repoBody }, cancellationToken);
    }

    private Task EnsureWebhookAsync(DeploymentTargetRef target, CancellationToken cancellationToken) =>
        repoLinks.EnsurePushWebhookAsync(target.AideProjectId, target.Repo, NetlifyRules.WebhookUrl(target.Repo.Host), NetlifyRules.RepoLinkTitle, cancellationToken);

    /// <summary>
    /// The id of the Netlify deploy key on the repository: one an earlier attempt added (found by its public key), else a new
    /// one (<c>POST /deploy_keys</c>) added read-only. A retry after that add finds it, so no duplicate is made.
    /// </summary>
    private async Task<string> EnsureDeployKeyAsync(DeploymentAuth auth, DeploymentTargetRef target, CancellationToken cancellationToken)
    {
        var onRepo = await repoLinks.ListDeployKeysAsync(target.AideProjectId, target.Repo, cancellationToken);
        using (var keys = await SendAsync(auth, HttpMethod.Get, "deploy_keys", null, cancellationToken))
        {
            var known = Items(keys.RootElement)
                .Where(k => Text(k, "id") is not null)
                .Select(k => new NetlifyDeployKey(Text(k, "id")!, Text(k, "public_key")));
            if (NetlifyRules.ReusableDeployKey(known, onRepo) is { } reusable)
            {
                return reusable;
            }
        }

        using var created = await SendAsync(auth, HttpMethod.Post, "deploy_keys", null, cancellationToken);
        var id = Text(created.RootElement, "id");
        var publicKey = Text(created.RootElement, "public_key");
        if (id is null || publicKey is null)
        {
            throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Netlify created a deploy key but returned no id or public key.", platform: Netlify);
        }

        await repoLinks.AddReadOnlyDeployKeyAsync(target.AideProjectId, target.Repo, NetlifyRules.RepoLinkTitle, publicKey, cancellationToken);
        return id;
    }

    private async Task<string> PostSiteAsync(DeploymentAuth auth, object body, CancellationToken cancellationToken)
    {
        using var created = await PlatformDeploymentCalls.Call(
            Netlify,
            () => http.SendAsync(PlatformProvider.Netlify, PlatformHosts.NetlifyClient, HttpMethod.Post, SitesPath(auth), auth.Token, body, cancellationToken),
            ex => NetlifyRules.IsPlanLimit(ex.Status, ex.Message) ? DeploymentFailure.PlanLimit : null);
        return Text(created.RootElement, "id")
            ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Netlify created the site but returned no id.", platform: Netlify);
    }

    /// <summary>
    /// The Netlify GitHub App installation for the repository's owner: from a site of the account already linked to a
    /// repository of the same owner (<c>build_settings.installation_id</c>). Netlify's API has no documented installation
    /// lookup, so <c>null</c> when no such site exists.
    /// </summary>
    private async Task<long?> InstallationIdAsync(DeploymentAuth auth, DeploymentRepoRef repo, CancellationToken cancellationToken)
    {
        using var sites = await SendAsync(auth, HttpMethod.Get, SitesPath(auth) + "?per_page=100", null, cancellationToken);
        foreach (var site in Items(sites.RootElement))
        {
            var settings = Property(site, "build_settings");
            var path = Text(settings, "repo_path");
            if (Text(settings, "provider") == "github" && path is not null && repo.Owner is not null
                && path.StartsWith(repo.Owner + "/", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(Text(settings, "installation_id"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                return id;
            }
        }

        return null;
    }

    private static string SitesPath(DeploymentAuth auth) =>
        auth.AccountSlug is { Length: > 0 } slug ? $"{Uri.EscapeDataString(slug)}/sites" : "sites";

    private Task<JsonDocument> SendAsync(DeploymentAuth auth, HttpMethod method, string path, object? body, CancellationToken cancellationToken) =>
        PlatformDeploymentCalls.Call(Netlify, () => http.SendAsync(PlatformProvider.Netlify, PlatformHosts.NetlifyClient, method, path, auth.Token, body, cancellationToken));
}
