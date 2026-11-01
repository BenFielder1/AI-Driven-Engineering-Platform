using System.Text;
using System.Text.Json;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using static Aide.Infrastructure.Deployments.PlatformDeploymentCalls;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// Cloudflare Pages (adopt only, §4.20) and env sync for both modes. Pages previews are <c>POST …/deployments</c> with the
/// form field <c>branch</c> (it builds the branch HEAD, so the commit is checked and re-triggered once, as on Netlify);
/// production is adopted from Pages' own build of the merge commit (until it appears, a poll without news, up to the
/// 30-minute ceiling).
/// </summary>
public sealed partial class CloudflareDeploymentProvider
{
    public async Task<IReadOnlyList<DeploymentEnvWritten>> SyncEnvAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvSyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        var account = Account(auth);
        var deletes = request.Deletes.Select(CloudflareRules.ParseEnvId).OfType<(string Key, DeploymentEnvironment Environment)>().ToList();
        if (DeploymentRollbackRules.CloudflarePagesProject(target.ResourceId) is { } project)
        {
            // Pages: deployment_configs.{production,preview}.env_vars; null deletes.
            var configs = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            foreach (var w in request.Writes)
            {
                Config(configs, w.Target)[w.Key] = new { type = w.Sensitive ? "secret_text" : "plain_text", value = w.Value };
            }

            foreach (var (key, environment) in deletes)
            {
                Config(configs, environment)[key] = null;
            }

            if (configs.Count > 0)
            {
                var body = new { deployment_configs = configs.ToDictionary(c => c.Key, c => (object)new { env_vars = c.Value }) };
                using var _ = await SendAsync(auth, HttpMethod.Patch, $"accounts/{account}/pages/projects/{Uri.EscapeDataString(project)}", body, cancellationToken);
            }

            return [.. request.Writes.Select(w => new DeploymentEnvWritten(w.Key, w.Target, CloudflareRules.EnvId(w.Key, w.Target)))];
        }

        // Workers: build variables per trigger (is_secret), and sensitive values as runtime secrets (shared by both environments).
        foreach (var group in request.Writes.GroupBy(w => w.Target))
        {
            var trigger = TriggerOf(target, group.Key);
            var vars = group.ToDictionary(w => w.Key, w => (object)new { value = w.Value, is_secret = w.Sensitive }, StringComparer.Ordinal);
            using var _ = await SendAsync(auth, HttpMethod.Patch, $"accounts/{account}/builds/triggers/{Uri.EscapeDataString(trigger)}/environment_variables", vars, cancellationToken);
        }

        var script = Uri.EscapeDataString(target.ResourceId!);
        foreach (var secret in request.Writes.Where(w => w.Sensitive && w.Target == DeploymentEnvironment.Production))
        {
            using var _ = await SendAsync(
                auth, HttpMethod.Put, $"accounts/{account}/workers/scripts/{script}/secrets", new { name = secret.Key, text = secret.Value, type = "secret_text" }, cancellationToken);
        }

        foreach (var (key, environment) in deletes)
        {
            await DeleteIgnoringMissingAsync(auth, $"accounts/{account}/builds/triggers/{Uri.EscapeDataString(TriggerOf(target, environment))}/environment_variables/{Uri.EscapeDataString(key)}", cancellationToken);
            if (environment == DeploymentEnvironment.Production)
            {
                await DeleteIgnoringMissingAsync(auth, $"accounts/{account}/workers/scripts/{script}/secrets/{Uri.EscapeDataString(key)}", cancellationToken);
            }
        }

        return [.. request.Writes.Select(w => new DeploymentEnvWritten(w.Key, w.Target, CloudflareRules.EnvId(w.Key, w.Target)))];
    }

    private static Dictionary<string, object?> Config(Dictionary<string, Dictionary<string, object?>> configs, DeploymentEnvironment environment)
    {
        var name = environment == DeploymentEnvironment.Production ? "production" : "preview";
        if (!configs.TryGetValue(name, out var vars))
        {
            vars = new Dictionary<string, object?>(StringComparer.Ordinal);
            configs[name] = vars;
        }

        return vars;
    }

    private async Task DeleteIgnoringMissingAsync(DeploymentAuth auth, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var _ = await SendAsync(auth, HttpMethod.Delete, path, null, cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure == DeploymentFailure.NotFound)
        {
            // Already gone.
        }
    }

    private async Task<ProviderDeployment> PagesDeployAsync(DeploymentAuth auth, DeployRequest request, CancellationToken cancellationToken)
    {
        if (request.Environment == DeploymentEnvironment.Production)
        {
            var built = await PagesFindAsync(auth, request.Target, DeploymentEnvironment.Production, request.GitSha, null, cancellationToken);
            if (request.Rebuild && built is not null)
            {
                // §4.20: a redeploy builds anew: retry Pages' earlier build of the commit (a new deployment), never adopt it.
                var earlier = NetlifyRules.ParseDeploymentId(built.Id).DeployId;
                using var retried = await SendAsync(
                    auth, HttpMethod.Post, $"{PagesPath(auth, request.Target)}/deployments/{Uri.EscapeDataString(earlier)}/retry", null, cancellationToken);
                var result = Result(retried);
                return Text(result, "id") is { } retryId
                    ? ToPages(result, retryId)
                    : throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Cloudflare Pages retried the build but returned no id.", platform: Cloudflare);
            }

            // Adopt only: Pages builds the production branch itself; until its build of the merge commit appears, wait.
            return built
                ?? throw new DeploymentProviderException(
                    DeploymentFailure.Upstream, null, "Waiting for Cloudflare Pages to start its build of the merge commit.", platform: Cloudflare);
        }

        var id = await PagesStartAsync(auth, request.Target, request.GitRef, cancellationToken);
        return new ProviderDeployment(NetlifyRules.DeploymentId(id, request.GitSha, 1, request.GitRef), null, null, null, null, State: DeploymentStatus.Queued);
    }

    private async Task<string> PagesStartAsync(DeploymentAuth auth, DeploymentTargetRef target, string branch, CancellationToken cancellationToken)
    {
        var form = new MultipartFormDataContent { { new StringContent(branch, Encoding.UTF8), "branch" } };
        using var created = await SendContentAsync(auth, HttpMethod.Post, $"{PagesPath(auth, target)}/deployments", form, cancellationToken);
        return Text(Result(created), "id")
            ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Cloudflare Pages started the deployment but returned no id.", platform: Cloudflare);
    }

    private async Task<ProviderDeployment> PagesGetAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        var (id, sha, attempt, branch) = NetlifyRules.ParseDeploymentId(deploymentId);
        using var json = await SendAsync(auth, HttpMethod.Get, $"{PagesPath(auth, target)}/deployments/{Uri.EscapeDataString(id)}", null, cancellationToken);
        var result = Result(json);
        var commit = Text(Property(Property(result, "deployment_trigger"), "metadata"), "commit_hash");
        if (sha is not null && branch is not null && NetlifyRules.BuiltOtherCommit(commit, sha))
        {
            if (attempt < 2)
            {
                var again = await PagesStartAsync(auth, target, branch, cancellationToken);
                return new ProviderDeployment(NetlifyRules.DeploymentId(again, sha, attempt + 1, branch), null, null, null, null, State: DeploymentStatus.Queued);
            }

            return new ProviderDeployment(deploymentId, null, null, null, $"Cloudflare Pages built commit {commit}, not the pull request head {sha}.", State: DeploymentStatus.Error);
        }

        return ToPages(result, deploymentId);
    }

    /// <summary>The Pages deployment of <paramref name="sha"/>; with <paramref name="createdAfter"/> (a manual or redeploy row) only one created after the row (§4.20).</summary>
    private async Task<ProviderDeployment?> PagesFindAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvironment environment, string sha, DateTimeOffset? createdAfter, CancellationToken cancellationToken)
    {
        var env = environment == DeploymentEnvironment.Production ? "production" : "preview";
        using var json = await SendAsync(auth, HttpMethod.Get, $"{PagesPath(auth, target)}/deployments?env={env}", null, cancellationToken);
        var match = Items(Result(json)).FirstOrDefault(d =>
            NetlifyRules.SameCommit(Text(Property(Property(d, "deployment_trigger"), "metadata"), "commit_hash"), sha)
            && CloudflareRules.Adoptable(CreatedOn(d), createdAfter));
        return match.ValueKind == JsonValueKind.Object && Text(match, "id") is { } id ? ToPages(match, id) : null;
    }

    private async Task<string?> PagesLogAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        var id = NetlifyRules.ParseDeploymentId(deploymentId).DeployId;
        using var json = await SendAsync(auth, HttpMethod.Get, $"{PagesPath(auth, target)}/deployments/{Uri.EscapeDataString(id)}/history/logs", null, cancellationToken);
        var lines = Items(Result(json), "data").Select(l => Text(l, "line")).OfType<string>().ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static string PagesPath(DeploymentAuth auth, DeploymentTargetRef target) =>
        $"accounts/{Account(auth)}/pages/projects/{Uri.EscapeDataString(DeploymentRollbackRules.CloudflarePagesProject(target.ResourceId)!)}";

    private static ProviderDeployment ToPages(JsonElement deployment, string id)
    {
        var stage = Property(deployment, "latest_stage");
        var state = CloudflareRules.PagesStateOf(Text(stage, "name"), Text(stage, "status"));
        var url = Https(Text(deployment, "url"));

        // §4.20: production's stable URL is the project's pages.dev host, not this deployment's hash URL.
        var production = Text(deployment, "environment") == "production" ? CloudflareRules.PagesProductionUrl(url) : null;
        return new ProviderDeployment(
            id,
            url,
            null,
            Text(stage, "status"),
            state == DeploymentStatus.Error ? $"The Cloudflare Pages {Text(stage, "name") ?? "build"} stage failed." : null,
            production,
            state);
    }
}
