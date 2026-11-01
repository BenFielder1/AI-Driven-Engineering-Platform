using System.Text.Json;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using static Aide.Infrastructure.Deployments.PlatformDeploymentCalls;

namespace Aide.Infrastructure.Deployments;

/// <summary>Netlify env sync, deploys, adoption, logs, cancel and restore (§4.20).</summary>
public sealed partial class NetlifyDeploymentProvider
{
    private static readonly string[] SecretScopes = ["builds", "functions", "runtime"];

    private static readonly string[] PlainScopes = ["builds", "functions", "runtime", "post-processing"];

    public async Task<IReadOnlyList<DeploymentEnvWritten>> SyncEnvAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvSyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        var account = auth.AccountId ?? auth.AccountSlug
            ?? throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, "The Netlify connection has no account; choose one in Settings → Connections.", platform: Netlify);
        var basePath = $"accounts/{Uri.EscapeDataString(account)}/env";
        var site = $"site_id={Uri.EscapeDataString(target.ResourceId!)}";
        var written = new List<DeploymentEnvWritten>();
        foreach (var key in request.Writes.GroupBy(w => w.Key, StringComparer.Ordinal))
        {
            var values = key.SelectMany(w => NetlifyRules.ContextsOf(w.Target).Select(c => new { context = c, value = w.Value })).ToList();
            try
            {
                foreach (var value in values)
                {
                    using var _ = await SendAsync(auth, HttpMethod.Patch, $"{basePath}/{Uri.EscapeDataString(key.Key)}?{site}", value, cancellationToken);
                }
            }
            catch (DeploymentProviderException ex) when (ex.Failure == DeploymentFailure.NotFound)
            {
                var secret = key.Any(w => w.Sensitive);
                var body = new[] { new { key = key.Key, scopes = secret ? SecretScopes : PlainScopes, is_secret = secret, values } };
                using var _ = await SendAsync(auth, HttpMethod.Post, $"{basePath}?{site}", body, cancellationToken);
            }

            written.AddRange(key.Select(w => new DeploymentEnvWritten(w.Key, w.Target, NetlifyRules.EnvId(w.Key, w.Target))));
        }

        foreach (var key in request.Deletes.Select(NetlifyRules.ParseEnvId).OfType<(string Key, DeploymentEnvironment Environment)>().GroupBy(d => d.Key, StringComparer.Ordinal))
        {
            await DeleteEnvAsync(auth, $"{basePath}/{Uri.EscapeDataString(key.Key)}", site, [.. key.SelectMany(d => NetlifyRules.ContextsOf(d.Environment))], cancellationToken);
        }

        return written;
    }

    /// <summary>Deletes the key's values in <paramref name="contexts"/>, and the key when nothing else is left. Gone is fine.</summary>
    private async Task DeleteEnvAsync(DeploymentAuth auth, string keyPath, string site, IReadOnlyList<string> contexts, CancellationToken cancellationToken)
    {
        try
        {
            using var current = await SendAsync(auth, HttpMethod.Get, $"{keyPath}?{site}", null, cancellationToken);
            var values = Items(current.RootElement, "values").Select(v => (Id: Text(v, "id"), Context: Text(v, "context"))).ToList();
            var doomed = values.Where(v => v.Id is not null && contexts.Contains(v.Context ?? string.Empty, StringComparer.Ordinal)).ToList();
            if (doomed.Count == values.Count)
            {
                using var _ = await SendAsync(auth, HttpMethod.Delete, $"{keyPath}?{site}", null, cancellationToken);
                return;
            }

            foreach (var value in doomed)
            {
                using var _ = await SendAsync(auth, HttpMethod.Delete, $"{keyPath}/value/{Uri.EscapeDataString(value.Id!)}?{site}", null, cancellationToken);
            }
        }
        catch (DeploymentProviderException ex) when (ex.Failure == DeploymentFailure.NotFound)
        {
            // Already gone.
        }
    }

    public async Task<ProviderDeployment> DeployAsync(DeploymentAuth auth, DeployRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preview = request.Environment == DeploymentEnvironment.Preview;
        var deployId = await StartBuildAsync(auth, request.Target, request.GitRef, request.AideDeploymentId, cancellationToken);
        return new ProviderDeployment(
            preview ? NetlifyRules.DeploymentId(deployId, request.GitSha, 1, request.GitRef) : deployId, null, null, null, null, State: DeploymentStatus.Queued);
    }

    public async Task<ProviderDeployment> GetDeploymentAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        var (deployId, sha, attempt, branch) = NetlifyRules.ParseDeploymentId(deploymentId);
        var id = deploymentId;
        if (deployId == NetlifyRules.HookPending && sha is not null && branch is not null)
        {
            // §4.20 fallback: the build hook's build appears under its trigger title; until then it is queued.
            if (await FindHookBuildAsync(auth, target, sha, cancellationToken) is not { } hooked)
            {
                return new ProviderDeployment(deploymentId, null, null, null, null, State: DeploymentStatus.Queued);
            }

            deployId = hooked;
            id = NetlifyRules.DeploymentId(hooked, sha, attempt, branch);
        }

        using var deploy = await SendAsync(auth, HttpMethod.Get, $"deploys/{Uri.EscapeDataString(deployId)}", null, cancellationToken);
        var root = deploy.RootElement;
        var commit = Text(root, "commit_ref");
        if (sha is not null && branch is not null && attempt < NetlifyRules.HookAttempt && NetlifyRules.IsSkipped(Text(root, "state"), Text(root, "error_message")))
        {
            // §4.20: an ignore rule skipped (or cancelled) the API branch build: build through the site's build hook instead,
            // which ignore rules don't cancel (once).
            await TriggerHookAsync(auth, target, branch, sha, cancellationToken);
            return new ProviderDeployment(
                NetlifyRules.DeploymentId(NetlifyRules.HookPending, sha, NetlifyRules.HookAttempt, branch), null, null, null, null, State: DeploymentStatus.Queued);
        }

        if (sha is not null && branch is not null && NetlifyRules.BuiltOtherCommit(commit, sha))
        {
            // §4.20: Netlify built the branch HEAD, not the PR head: trigger it once more, then give up.
            if (attempt < 2)
            {
                var again = await StartBuildAsync(auth, target, branch, null, cancellationToken);
                return new ProviderDeployment(NetlifyRules.DeploymentId(again, sha, attempt + 1, branch), null, null, null, null, State: DeploymentStatus.Queued);
            }

            return new ProviderDeployment(
                id, null, Inspector(root, deployId), null, $"Netlify built commit {commit}, not the pull request head {sha}.", State: DeploymentStatus.Error);
        }

        return ToDeployment(root, id, deployId);
    }

    public async Task<string?> GetBuildLogTailAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        var deployId = NetlifyRules.ParseDeploymentId(deploymentId).DeployId;
        using var deploy = await SendAsync(auth, HttpMethod.Get, $"deploys/{Uri.EscapeDataString(deployId)}", null, cancellationToken);

        // §4.20: Netlify has no REST log endpoint; best effort is the error and the deploy's admin page.
        var error = Text(deploy.RootElement, "error_message");
        var link = Inspector(deploy.RootElement, deployId);
        return error is null && link is null ? null : $"{error ?? "The Netlify build failed."}{(link is null ? string.Empty : $"\nThe full log: {link}")}";
    }

    public async Task<ProviderDeployment?> FindDeploymentForShaAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvironment environment, string sha, Guid? aideDeploymentId, DateTimeOffset? createdAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var site = Uri.EscapeDataString(target.ResourceId!);
        if (aideDeploymentId is { } id)
        {
            using var recent = await SendAsync(auth, HttpMethod.Get, $"sites/{site}/deploys?per_page=30", null, cancellationToken);
            var mine = Items(recent.RootElement).FirstOrDefault(d => Text(d, "title") == NetlifyRules.TitlePrefix + id.ToString("D"));
            if (mine.ValueKind != JsonValueKind.Object || Text(mine, "id") is not { } deployId)
            {
                return null;
            }

            var encoded = environment == DeploymentEnvironment.Preview && Text(mine, "branch") is { } branch
                ? NetlifyRules.DeploymentId(deployId, sha, 1, branch)
                : deployId;
            return ToDeployment(mine, encoded, deployId);
        }

        if (environment != DeploymentEnvironment.Production)
        {
            return null;
        }

        using var production = await SendAsync(auth, HttpMethod.Get, $"sites/{site}/deploys?production=true&per_page=30", null, cancellationToken);
        var match = Items(production.RootElement).FirstOrDefault(d => NetlifyRules.SameCommit(Text(d, "commit_ref"), sha));
        return match.ValueKind == JsonValueKind.Object && Text(match, "id") is { } adopted ? ToDeployment(match, adopted, adopted) : null;
    }

    public async Task CancelAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        try
        {
            using var _ = await SendAsync(auth, HttpMethod.Post, $"deploys/{Uri.EscapeDataString(NetlifyRules.ParseDeploymentId(deploymentId).DeployId)}/cancel", null, cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure is DeploymentFailure.NotFound or DeploymentFailure.Rejected)
        {
            // Gone or finished.
        }
    }

    /// <summary>
    /// FR-150: <c>POST /sites/{id}/deploys/{deploy_id}/restore</c> (the restored deploy is published, no rebuild), then
    /// reads the deploy's <c>locked</c> state: a restore can lock auto publishing, so the next production deploy would reach
    /// <c>ready</c> without going live. A locked deploy is unlocked (<c>POST /deploys/{deploy_id}/unlock</c>): the restored
    /// deploy stays published, and later production deploys publish again (§4.20, PROGRESS.md decision).
    /// </summary>
    public async Task RollbackAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        var deployId = NetlifyRules.ParseDeploymentId(deploymentId).DeployId;
        bool? locked;
        using (var restored = await SendAsync(
            auth, HttpMethod.Post, $"sites/{Uri.EscapeDataString(target.ResourceId!)}/deploys/{Uri.EscapeDataString(deployId)}/restore", null, cancellationToken))
        {
            locked = Locked(restored.RootElement);
        }

        if (locked is null)
        {
            try
            {
                using var deploy = await SendAsync(auth, HttpMethod.Get, $"deploys/{Uri.EscapeDataString(deployId)}", null, cancellationToken);
                locked = Locked(deploy.RootElement);
            }
            catch (DeploymentProviderException ex) when (ex.Failure == DeploymentFailure.NotFound)
            {
                locked = false;
            }
        }

        if (locked == true)
        {
            using var _ = await SendAsync(auth, HttpMethod.Post, $"deploys/{Uri.EscapeDataString(deployId)}/unlock", null, cancellationToken);
        }
    }

    private static bool? Locked(JsonElement deploy) => Property(deploy, "locked").ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>The id of the deploy the build hook made for <paramref name="sha"/> (by its trigger title), or <c>null</c>.</summary>
    private async Task<string?> FindHookBuildAsync(DeploymentAuth auth, DeploymentTargetRef target, string sha, CancellationToken cancellationToken)
    {
        using var recent = await SendAsync(auth, HttpMethod.Get, $"sites/{Uri.EscapeDataString(target.ResourceId!)}/deploys?per_page=30", null, cancellationToken);
        var title = NetlifyRules.HookDeployTitle(sha);
        var match = Items(recent.RootElement).FirstOrDefault(d => Text(d, "title") == title);
        return match.ValueKind == JsonValueKind.Object ? Text(match, "id") : null;
    }

    /// <summary>
    /// §4.20 fallback: builds <paramref name="branch"/> through AIDE's build hook of the branch (made through the API the
    /// first time), titled for <paramref name="sha"/>. Counts as a deploy creation for the account's limit.
    /// </summary>
    private async Task TriggerHookAsync(DeploymentAuth auth, DeploymentTargetRef target, string branch, string sha, CancellationToken cancellationToken)
    {
        var site = Uri.EscapeDataString(target.ResourceId!);
        var hookTitle = NetlifyRules.HookTitlePrefix + branch;
        string? hookId;
        using (var hooks = await SendAsync(auth, HttpMethod.Get, $"sites/{site}/build_hooks", null, cancellationToken))
        {
            var existing = Items(hooks.RootElement).FirstOrDefault(h => Text(h, "branch") == branch && Text(h, "title") == hookTitle);
            hookId = existing.ValueKind == JsonValueKind.Object ? Text(existing, "id") : null;
        }

        if (hookId is null)
        {
            using var created = await SendAsync(auth, HttpMethod.Post, $"sites/{site}/build_hooks", new { title = hookTitle, branch }, cancellationToken);
            hookId = Text(created.RootElement, "id")
                ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Netlify created the build hook but returned no id.", platform: Netlify);
        }

        var account = auth.AccountId ?? auth.AccountSlug ?? "netlify";
        limiter.Take(account);
        try
        {
            // Build hooks live at api.netlify.com/build_hooks/{id}, outside /api/v1.
            using var _ = await SendAsync(
                auth, HttpMethod.Post, $"../../build_hooks/{Uri.EscapeDataString(hookId)}?trigger_title={Uri.EscapeDataString(NetlifyRules.HookDeployTitle(sha))}", null, cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure == DeploymentFailure.RateLimited)
        {
            limiter.Block(account, ex.RetryAt);
            throw;
        }
    }

    /// <summary>Starts a build of <paramref name="branch"/> after the account's deploy limit allows it; returns the deploy id.</summary>
    private async Task<string> StartBuildAsync(DeploymentAuth auth, DeploymentTargetRef target, string branch, Guid? aideDeploymentId, CancellationToken cancellationToken)
    {
        var account = auth.AccountId ?? auth.AccountSlug ?? "netlify";
        limiter.Take(account);
        var query = $"branch={Uri.EscapeDataString(branch)}" + (aideDeploymentId is { } id ? $"&title={Uri.EscapeDataString(NetlifyRules.TitlePrefix + id.ToString("D"))}" : string.Empty);
        JsonDocument build;
        try
        {
            build = await SendAsync(auth, HttpMethod.Post, $"sites/{Uri.EscapeDataString(target.ResourceId!)}/builds?{query}", null, cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure == DeploymentFailure.RateLimited)
        {
            limiter.Block(account, ex.RetryAt);
            throw;
        }

        using (build)
        {
            return Text(build.RootElement, "deploy_id") ?? Text(build.RootElement, "id")
                ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Netlify started the build but returned no deploy id.", platform: Netlify);
        }
    }

    private static ProviderDeployment ToDeployment(JsonElement deploy, string id, string deployId) => new(
        id,
        Https(Text(deploy, "deploy_ssl_url") ?? Text(deploy, "deploy_url")),
        Inspector(deploy, deployId),
        Text(deploy, "state"),
        NetlifyRules.IsSkipped(Text(deploy, "state"), Text(deploy, "error_message"))
            ? $"Netlify skipped or cancelled this build (e.g. an ignore rule), so it was never published.{(Text(deploy, "error_message") is { } m ? $" Netlify: {m}" : string.Empty)}"
            : Text(deploy, "error_message"),
        Https(Text(deploy, "ssl_url") ?? Text(deploy, "url")),
        NetlifyRules.StateOf(Text(deploy, "state")));

    private static string? Inspector(JsonElement deploy, string deployId) =>
        Text(deploy, "admin_url") is { } admin ? $"{admin.TrimEnd('/')}/deploys/{deployId}" : null;
}
