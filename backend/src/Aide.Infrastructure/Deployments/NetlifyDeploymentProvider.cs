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
/// (deploy key + webhook) aren't automated yet: <c>PLATFORM_ERROR</c>, link an existing site instead.</item>
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
public sealed partial class NetlifyDeploymentProvider(PlatformHttp http, NetlifyDeployRateLimiter limiter) : IDeploymentProvider
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
        if (repo.Host != "github")
        {
            // §4.20: GitLab and Bitbucket need a Netlify deploy key on the repository and a push webhook; not automated yet.
            throw new DeploymentProviderException(
                DeploymentFailure.NotSupported,
                null,
                "Creating a Netlify site for a GitLab or Bitbucket repository isn't automated yet: create the site in Netlify, then link it.",
                platform: Netlify);
        }

        var name = string.IsNullOrEmpty(request.Name)
            ? throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, "A new Netlify site needs a name.", platform: Netlify)
            : request.Name;

        // A retry after a later failure finds the site it created.
        foreach (var existing in await ListResourcesAsync(auth, cancellationToken))
        {
            if (existing.Name == name)
            {
                return existing.Detail is not null && DeploymentSettingsRules.LinkMatches(existing.Detail, repo.FullName)
                    ? existing.Id
                    : throw new DeploymentProviderException(DeploymentFailure.Rejected, 409, "A Netlify site with this name exists already; link it instead or pick another name.", platform: Netlify);
            }
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
