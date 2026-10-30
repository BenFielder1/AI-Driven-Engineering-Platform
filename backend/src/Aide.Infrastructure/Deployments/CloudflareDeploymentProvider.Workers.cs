using System.Text;
using System.Text.Json;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using static Aide.Infrastructure.Deployments.PlatformDeploymentCalls;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// Cloudflare Workers Builds deploys, logs, rollback and env sync (§4.20); a Pages target goes to the Pages half
/// (<c>CloudflareDeploymentProvider.Pages.cs</c>).
/// </summary>
public sealed partial class CloudflareDeploymentProvider
{
    /// <summary>The most log pages read for a tail (Workers Builds logs are cursor-paged).</summary>
    private const int MaxLogPages = 50;

    public async Task<ProviderDeployment> DeployAsync(DeploymentAuth auth, DeployRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (DeploymentRollbackRules.IsCloudflarePages(request.Target.ResourceId))
        {
            return await PagesDeployAsync(auth, request, cancellationToken);
        }

        var trigger = TriggerOf(request.Target, request.Environment);

        // §4.20: SHA-pinned (the trigger builds exactly this commit).
        using var started = await SendAsync(
            auth,
            HttpMethod.Post,
            $"accounts/{Account(auth)}/builds/triggers/{Uri.EscapeDataString(trigger)}/builds",
            new { branch = request.GitRef, commit_hash = request.GitSha },
            cancellationToken);
        var build = Text(Result(started), "build_uuid")
            ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Cloudflare started the build but returned no id.", platform: Cloudflare);
        return new ProviderDeployment(build, null, null, null, null, State: DeploymentStatus.Queued);
    }

    public async Task<ProviderDeployment> GetDeploymentAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        if (DeploymentRollbackRules.IsCloudflarePages(target.ResourceId))
        {
            return await PagesGetAsync(auth, target, deploymentId, cancellationToken);
        }

        using var json = await SendAsync(auth, HttpMethod.Get, $"accounts/{Account(auth)}/builds/builds/{Uri.EscapeDataString(deploymentId)}", null, cancellationToken);
        var build = Result(json);
        return ToBuild(build, deploymentId, await ProductionUrlOfAsync(auth, target, build, cancellationToken));
    }

    public async Task<string?> GetBuildLogTailAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        if (DeploymentRollbackRules.IsCloudflarePages(target.ResourceId))
        {
            return await PagesLogAsync(auth, target, deploymentId, cancellationToken);
        }

        var log = new StringBuilder();
        string? cursor = null;
        for (var page = 0; page < MaxLogPages; page++)
        {
            var path = $"accounts/{Account(auth)}/builds/builds/{Uri.EscapeDataString(deploymentId)}/logs" + (cursor is null ? string.Empty : $"?cursor={Uri.EscapeDataString(cursor)}");
            using var json = await SendAsync(auth, HttpMethod.Get, path, null, cancellationToken);
            var result = Result(json);
            foreach (var line in Items(result, "lines"))
            {
                // Lines are [timestamp, text] pairs (or objects with a message).
                var text = line.ValueKind == JsonValueKind.Array && line.GetArrayLength() > 1 ? line[1].ToString()
                    : line.ValueKind == JsonValueKind.String ? line.GetString()
                    : Text(line, "message") ?? Text(line, "line");
                log.Append(text).Append('\n');
            }

            if (log.Length > IVercelClient.MaxBuildLogLength)
            {
                log.Remove(0, log.Length - IVercelClient.MaxBuildLogLength);
            }

            var next = Text(result, "cursor");
            var truncated = Property(result, "truncated").ValueKind == JsonValueKind.True;
            if (next is null || next == cursor || !truncated)
            {
                break;
            }

            cursor = next;
        }

        return log.Length == 0 ? null : log.ToString();
    }

    public async Task<ProviderDeployment?> FindDeploymentForShaAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvironment environment, string sha, Guid? aideDeploymentId, DateTimeOffset? createdAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (DeploymentRollbackRules.IsCloudflarePages(target.ResourceId))
        {
            return await PagesFindAsync(auth, target, environment, sha, aideDeploymentId is null ? null : createdAfter, cancellationToken);
        }

        var (preview, production) = CloudflareRules.ParseTriggerIds(target.PreviewResourceId);
        var trigger = environment == DeploymentEnvironment.Production ? production : preview;
        var tag = await ScriptTagAsync(auth, target.ResourceId!, cancellationToken);
        if (trigger is null || tag is null)
        {
            return null;
        }

        using var json = await SendAsync(auth, HttpMethod.Get, $"accounts/{Account(auth)}/builds/workers/{Uri.EscapeDataString(tag)}/builds", null, cancellationToken);
        // §4.20: a manual or redeploy row (an AIDE id) adopts only a build made after it, never an earlier build of the commit.
        var after = aideDeploymentId is null ? null : createdAfter;
        var match = Items(Result(json)).FirstOrDefault(b =>
            string.Equals(Text(Property(b, "build_trigger_metadata"), "commit_hash"), sha, StringComparison.OrdinalIgnoreCase)
            && TriggerOfBuild(b) == trigger
            && CloudflareRules.BuildStateOf(Text(b, "status"), Text(b, "build_outcome")) != DeploymentStatus.Canceled
            && CloudflareRules.Adoptable(CreatedOn(b), after));
        return match.ValueKind == JsonValueKind.Object && Text(match, "build_uuid") is { } id
            ? ToBuild(match, id, await ProductionUrlOfAsync(auth, target, match, cancellationToken))
            : null;
    }

    public async Task CancelAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (DeploymentRollbackRules.IsCloudflarePages(target.ResourceId))
        {
            return;
        }

        try
        {
            using var _ = await SendAsync(auth, HttpMethod.Put, $"accounts/{Account(auth)}/builds/builds/{Uri.EscapeDataString(deploymentId)}/cancel", null, cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure is DeploymentFailure.NotFound or DeploymentFailure.Rejected)
        {
            // Gone or finished.
        }
    }

    /// <summary>
    /// FR-150: Workers: deploy the build's Worker version at 100 % (<c>POST /workers/scripts/{name}/deployments</c>);
    /// Pages: <c>POST …/deployments/{id}/rollback</c>.
    /// </summary>
    public async Task RollbackAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        if (DeploymentRollbackRules.CloudflarePagesProject(target.ResourceId) is { } project)
        {
            var id = NetlifyRules.ParseDeploymentId(deploymentId).DeployId;
            using var _ = await SendAsync(
                auth, HttpMethod.Post, $"accounts/{Account(auth)}/pages/projects/{Uri.EscapeDataString(project)}/deployments/{Uri.EscapeDataString(id)}/rollback", null, cancellationToken);
            return;
        }

        var version = await VersionOfBuildAsync(auth, target, deploymentId, cancellationToken);
        using var deployed = await SendAsync(
            auth,
            HttpMethod.Post,
            $"accounts/{Account(auth)}/workers/scripts/{Uri.EscapeDataString(target.ResourceId!)}/deployments",
            CloudflareRules.RollbackDeployment(version),
            cancellationToken);
    }

    /// <summary>
    /// The Worker version a build uploaded: the build's own version field when Cloudflare reports one, else the version
    /// whose annotations name the build's commit. Unknown: refused (<c>INVALID_DEPLOYMENT</c>).
    /// </summary>
    private async Task<string> VersionOfBuildAsync(DeploymentAuth auth, DeploymentTargetRef target, string buildId, CancellationToken cancellationToken)
    {
        var account = Account(auth);
        string? sha;
        using (var build = await SendAsync(auth, HttpMethod.Get, $"accounts/{account}/builds/builds/{Uri.EscapeDataString(buildId)}", null, cancellationToken))
        {
            var result = Result(build);
            if ((Text(result, "version_id") ?? Text(result, "worker_version_id") ?? Text(Property(result, "build_outcome_metadata"), "version_id")) is { } direct)
            {
                return direct;
            }

            sha = Text(Property(result, "build_trigger_metadata"), "commit_hash");
        }

        if (sha is not null)
        {
            using var versions = await SendAsync(auth, HttpMethod.Get, $"accounts/{account}/workers/scripts/{Uri.EscapeDataString(target.ResourceId!)}/versions", null, cancellationToken);
            var items = Items(Result(versions), "items").ToList();
            var match = items.FirstOrDefault(v => CloudflareRules.VersionMatchesCommit(
                Property(v, "annotations").ValueKind == JsonValueKind.Object
                    ? Property(v, "annotations").EnumerateObject().Select(a => a.Value.ToString())
                    : [],
                sha));
            if (match.ValueKind == JsonValueKind.Object && Text(match, "id") is { } id)
            {
                return id;
            }
        }

        throw new DeploymentProviderException(
            DeploymentFailure.Rejected, 409, "Cloudflare doesn't say which Worker version this build uploaded, so AIDE can't roll back to it.", platform: Cloudflare);
    }

    private static string TriggerOf(DeploymentTargetRef target, DeploymentEnvironment environment)
    {
        var (preview, production) = CloudflareRules.ParseTriggerIds(target.PreviewResourceId);
        return (environment == DeploymentEnvironment.Production ? production : preview)
            ?? throw new DeploymentProviderException(
                DeploymentFailure.Rejected, 400, $"The Worker has no Workers Builds {environment.ToString().ToLowerInvariant()} trigger; set the target up again.", platform: Cloudflare);
    }

    /// <summary>A Workers build; <paramref name="productionUrl"/> is the Worker's stable URL for a ready production build (the alias), else <c>null</c>.</summary>
    private static ProviderDeployment ToBuild(JsonElement build, string id, string? productionUrl)
    {
        var state = CloudflareRules.BuildStateOf(Text(build, "status"), Text(build, "build_outcome"));
        return new ProviderDeployment(
            id,
            Https(Text(build, "preview_url")) ?? productionUrl,
            null,
            Text(build, "status"),
            state == DeploymentStatus.Error ? $"The Workers build ended with {Text(build, "build_outcome") ?? "a failure"}." : null,
            productionUrl,
            state);
    }

    /// <summary>
    /// §4.20: the Worker's <c>workers.dev</c> URL when <paramref name="build"/> is a ready build of the production trigger
    /// (a production build has no <c>preview_url</c>, so the stable URL is the alias), else <c>null</c>.
    /// </summary>
    private async Task<string?> ProductionUrlOfAsync(DeploymentAuth auth, DeploymentTargetRef target, JsonElement build, CancellationToken cancellationToken)
    {
        var production = CloudflareRules.ParseTriggerIds(target.PreviewResourceId).Production;
        return production is not null
            && TriggerOfBuild(build) == production
            && CloudflareRules.BuildStateOf(Text(build, "status"), Text(build, "build_outcome")) == DeploymentStatus.Ready
            && !string.IsNullOrEmpty(target.ResourceId)
                ? await WorkersDevUrlAsync(auth, target.ResourceId, cancellationToken)
                : null;
    }

    private static string? TriggerOfBuild(JsonElement build) => Text(Property(build, "trigger"), "trigger_uuid") ?? Text(build, "trigger_uuid");

    /// <summary>When Cloudflare created a build or Pages deployment (<c>created_on</c>), or <c>null</c>.</summary>
    private static DateTimeOffset? CreatedOn(JsonElement item) =>
        DateTimeOffset.TryParse(Text(item, "created_on"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
}
