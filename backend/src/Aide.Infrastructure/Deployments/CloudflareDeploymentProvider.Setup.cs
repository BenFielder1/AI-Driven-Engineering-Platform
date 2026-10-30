using System.Text.Json;
using Aide.Domain.Deployments;
using static Aide.Infrastructure.Deployments.PlatformDeploymentCalls;

namespace Aide.Infrastructure.Deployments;

/// <summary>Cloudflare setup: the Workers Builds triggers and the Pages link (§4.20).</summary>
public sealed partial class CloudflareDeploymentProvider
{
    /// <summary>
    /// The Worker's triggers: reuses AIDE's (or the user's) production and preview triggers and creates the missing ones
    /// with the repository connection and build token (from the arguments, else from the production trigger).
    /// </summary>
    private async Task<DeploymentSetupResult> WorkerResultAsync(
        DeploymentAuth auth, DeploymentTargetRef target, string name, string? connection, string? token, CancellationToken cancellationToken)
    {
        var account = Account(auth);
        var tag = await ScriptTagAsync(auth, name, cancellationToken)
            ?? throw new DeploymentProviderException(DeploymentFailure.NotFound, 404, $"The Worker {name} was not found.", platform: Cloudflare);
        var branch = target.Repo.DefaultBranch;
        List<JsonElement> triggers;
        using (var json = await SendAsync(auth, HttpMethod.Get, $"accounts/{account}/builds/workers/{Uri.EscapeDataString(tag)}/triggers", null, cancellationToken))
        {
            triggers = [.. Items(Result(json)).Select(t => t.Clone())];
        }

        var production = triggers.FirstOrDefault(t => !IsPreviewTrigger(t) && Items(t, "branch_includes").Any(b => b.ValueKind == JsonValueKind.String && b.GetString() == branch));
        var preview = triggers.FirstOrDefault(IsPreviewTrigger);
        connection ??= production.ValueKind == JsonValueKind.Object
            ? Text(Property(production, "repo_connection"), "repo_connection_uuid") ?? Text(production, "repo_connection_uuid")
            : null;
        if (connection is null)
        {
            throw new DeploymentProviderException(
                DeploymentFailure.GitNotConnected,
                409,
                $"The Worker {name} isn't connected to {target.Repo.FullName} with Workers Builds. Connect it in Cloudflare (install the Cloudflare Git app), then try again.",
                CloudflareRules.DashboardUrl,
                platform: Cloudflare);
        }

        token ??= production.ValueKind == JsonValueKind.Object ? Text(production, "build_token_uuid") : null;
        token ??= await BuildTokenAsync(auth, name, cancellationToken);
        var config = target.BuildConfig;
        var productionId = production.ValueKind == JsonValueKind.Object && Text(production, "trigger_uuid") is { } p
            ? p
            : await CreateTriggerAsync(auth, tag, connection, token, CloudflareRules.ProductionTrigger(branch, target.RootDirectory, config.BuildCommand), cancellationToken);
        var previewId = preview.ValueKind == JsonValueKind.Object && Text(preview, "trigger_uuid") is { } v
            ? v
            : await CreateTriggerAsync(auth, tag, connection, token, CloudflareRules.PreviewTrigger(target.RootDirectory, config.BuildCommand), cancellationToken);
        return new DeploymentSetupResult(name, name, await WorkersDevUrlAsync(auth, name, cancellationToken), CloudflareRules.TriggerIds(previewId, productionId), null);
    }

    private async Task<string> CreateTriggerAsync(
        DeploymentAuth auth, string tag, string connection, string token, CloudflareTriggerSpec spec, CancellationToken cancellationToken)
    {
        var body = new
        {
            external_script_id = tag,
            repo_connection_uuid = connection,
            build_token_uuid = token,
            trigger_name = spec.TriggerName,
            build_command = spec.BuildCommand ?? string.Empty,
            deploy_command = spec.DeployCommand,
            root_directory = spec.RootDirectory,
            branch_includes = spec.BranchIncludes,
            branch_excludes = spec.BranchExcludes,
            path_includes = new[] { "*" },
            path_excludes = Array.Empty<string>(),
        };
        using var created = await SendAsync(auth, HttpMethod.Post, $"accounts/{Account(auth)}/builds/triggers", body, cancellationToken);
        return Text(Result(created), "trigger_uuid")
            ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Cloudflare created the trigger but returned no id.", platform: Cloudflare);
    }

    private static bool IsPreviewTrigger(JsonElement trigger) =>
        Text(trigger, "deploy_command")?.Contains("versions upload", StringComparison.Ordinal) ?? false;

    /// <summary>The Worker's <c>workers.dev</c> URL (best effort: <c>null</c> when the account has no subdomain).</summary>
    private async Task<string?> WorkersDevUrlAsync(DeploymentAuth auth, string name, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await SendAsync(auth, HttpMethod.Get, $"accounts/{Account(auth)}/workers/subdomain", null, cancellationToken);
            return Text(Result(json), "subdomain") is { Length: > 0 } sub ? CloudflareRules.WorkersDevUrl(name, sub) : null;
        }
        catch (DeploymentProviderException ex) when (ex.Failure is DeploymentFailure.NotFound or DeploymentFailure.Forbidden)
        {
            return null;
        }
    }

    /// <summary>
    /// Pages (adopt only): the project must build this repository; previews become <c>custom</c> excluding <c>aide/*</c>
    /// (AIDE deploys its branches through the API).
    /// </summary>
    private async Task<DeploymentSetupResult> LinkPagesAsync(DeploymentAuth auth, DeploymentTargetRef target, string project, CancellationToken cancellationToken)
    {
        var path = $"accounts/{Account(auth)}/pages/projects/{Uri.EscapeDataString(project)}";
        using (var json = await SendAsync(auth, HttpMethod.Get, path, null, cancellationToken))
        {
            var linked = PagesRepo(Result(json));
            if (linked is null)
            {
                throw new DeploymentProviderException(
                    DeploymentFailure.GitNotConnected, 409, "The Pages project isn't connected to a Git repository. Connect it in Cloudflare, then link it again.", CloudflareRules.DashboardUrl, platform: Cloudflare);
            }

            if (!DeploymentSettingsRules.LinkMatches(linked, target.Repo.FullName))
            {
                throw new DeploymentProviderException(DeploymentFailure.Rejected, 400, $"The Pages project builds {linked}, not {target.Repo.FullName}.", platform: Cloudflare);
            }
        }

        var body = new
        {
            source = new
            {
                config = new { preview_deployment_setting = "custom", preview_branch_excludes = new[] { CloudflareRules.AideBranchPattern } },
            },
        };
        using var updated = await SendAsync(auth, HttpMethod.Patch, path, body, cancellationToken);
        var result = Result(updated);
        return new DeploymentSetupResult(
            DeploymentRollbackRules.CloudflarePagesResourceId(project),
            Text(result, "name") ?? project,
            Https(Text(result, "subdomain")),
            null,
            null);
    }

    /// <summary>The <c>owner/repo</c> a Pages project builds (<c>source.config</c>), or <c>null</c>.</summary>
    private static string? PagesRepo(JsonElement project)
    {
        var config = Property(Property(project, "source"), "config");
        return Text(config, "owner") is { } owner && Text(config, "repo_name") is { } repo ? $"{owner}/{repo}" : null;
    }
}
