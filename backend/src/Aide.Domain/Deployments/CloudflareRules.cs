using System.Globalization;
using Aide.Domain.Common;
using Aide.Domain.Entities;

namespace Aide.Domain.Deployments;

/// <summary>
/// A Workers Builds trigger AIDE creates (§4.20): its name, branch filters, build and deploy commands and root directory.
/// </summary>
public sealed record CloudflareTriggerSpec(
    string TriggerName,
    IReadOnlyList<string> BranchIncludes,
    IReadOnlyList<string> BranchExcludes,
    string? BuildCommand,
    string DeployCommand,
    string RootDirectory);

/// <summary>
/// §4.20 Cloudflare (Workers + Workers Builds; Pages adopt only): build and Pages states, the two triggers, the trigger
/// ids kept on the target, env ids, the runtime-secret rule and the first-Worker placeholder. Pure.
/// </summary>
public static class CloudflareRules
{
    public const string ProductionDeployCommand = "npx wrangler deploy";

    public const string PreviewDeployCommand = "npx wrangler versions upload";

    /// <summary>AIDE's branches, excluded from the preview trigger (AIDE starts their builds itself, SHA-pinned).</summary>
    public const string AideBranchPattern = "aide/*";

    /// <summary>The Workers and Pages dashboard (FR-144 <c>PLATFORM_GIT_NOT_CONNECTED</c> link to install the Git app).</summary>
    public const string DashboardUrl = "https://dash.cloudflare.com/?to=/:account/workers-and-pages";

    /// <summary>
    /// The placeholder module uploaded when the Worker doesn't exist yet (§11 Q7 first-Worker bootstrap): triggers need a
    /// Worker, and the first build's <c>wrangler deploy</c> replaces it.
    /// </summary>
    public const string PlaceholderModule =
        "export default { async fetch() { return new Response(\"This app is being deployed by AIDE.\", { status: 503 }); } };\n";

    /// <summary>
    /// §4.20 Workers Builds mapping: <c>queued</c>, <c>initializing</c> → queued; <c>running</c> → building;
    /// <c>stopped</c> with <c>build_outcome</c> success → ready, fail/failure/terminated → error, cancelled/canceled/skipped →
    /// canceled (stopped without an outcome yet: building; anything unknown: building).
    /// </summary>
    public static DeploymentStatus BuildStateOf(string? status, string? outcome)
    {
        var s = status?.Trim().ToLowerInvariant();
        if (s is "queued" or "initializing")
        {
            return DeploymentStatus.Queued;
        }

        if (s != "stopped")
        {
            return DeploymentStatus.Building;
        }

        return outcome?.Trim().ToLowerInvariant() switch
        {
            "success" => DeploymentStatus.Ready,
            "fail" or "failure" or "failed" or "terminated" => DeploymentStatus.Error,
            "cancelled" or "canceled" or "skipped" => DeploymentStatus.Canceled,
            null or "" => DeploymentStatus.Building,
            _ => DeploymentStatus.Error,
        };
    }

    /// <summary>
    /// §4.20 Pages mapping on <c>latest_stage</c>: <c>deploy</c> + <c>success</c> → ready; <c>failure</c> → error;
    /// <c>canceled</c>/<c>skipped</c> → canceled; anything else building.
    /// </summary>
    public static DeploymentStatus PagesStateOf(string? stageName, string? stageStatus)
    {
        var status = stageStatus?.Trim().ToLowerInvariant();
        return status switch
        {
            "failure" => DeploymentStatus.Error,
            "canceled" or "cancelled" or "skipped" => DeploymentStatus.Canceled,
            "success" when string.Equals(stageName?.Trim(), "deploy", StringComparison.OrdinalIgnoreCase) => DeploymentStatus.Ready,
            _ => DeploymentStatus.Building,
        };
    }

    /// <summary>§4.20: the production trigger: the production branch only, <c>npx wrangler deploy</c>.</summary>
    public static CloudflareTriggerSpec ProductionTrigger(string productionBranch, string rootDirectory, string? buildCommand) => new(
        "AIDE production", [productionBranch], [], buildCommand, ProductionDeployCommand, Root(rootDirectory));

    /// <summary>
    /// §4.20: the preview trigger: every branch but <c>aide/*</c> (AIDE starts version-branch builds itself with the commit
    /// hash), <c>npx wrangler versions upload</c> (a version with a preview URL; production is untouched).
    /// </summary>
    public static CloudflareTriggerSpec PreviewTrigger(string rootDirectory, string? buildCommand) => new(
        "AIDE preview", ["*"], [AideBranchPattern], buildCommand, PreviewDeployCommand, Root(rootDirectory));

    /// <summary>
    /// The target's <c>preview_resource_id</c> for Workers: the preview trigger, then the production trigger
    /// (<c>&lt;preview&gt;|&lt;production&gt;</c>), since the target has one preview column.
    /// </summary>
    public static string TriggerIds(string previewTrigger, string productionTrigger) => $"{previewTrigger}|{productionTrigger}";

    /// <summary>The (preview, production) triggers of <see cref="TriggerIds"/>; parts are <c>null</c> when unknown.</summary>
    public static (string? Preview, string? Production) ParseTriggerIds(string? previewResourceId)
    {
        if (string.IsNullOrEmpty(previewResourceId))
        {
            return (null, null);
        }

        var parts = previewResourceId.Split('|', 2);
        return (parts[0].Length > 0 ? parts[0] : null, parts.Length == 2 && parts[1].Length > 0 ? parts[1] : null);
    }

    /// <summary>AIDE's id of a synced (key, environment) on Cloudflare: <c>cloudflare:&lt;environment&gt;:&lt;key&gt;</c>.</summary>
    public static string EnvId(string key, DeploymentEnvironment environment) =>
        string.Create(CultureInfo.InvariantCulture, $"cloudflare:{environment.ToWire()}:{key}");

    /// <summary>The (key, environment) of an <see cref="EnvId"/>, or <c>null</c>.</summary>
    public static (string Key, DeploymentEnvironment Environment)? ParseEnvId(string? id)
    {
        var parts = id?.Split(':', 3);
        return parts is { Length: 3 } && parts[0] == "cloudflare" && EnumStrings.TryParse<DeploymentEnvironment>(parts[1], out var env) && parts[2].Length > 0
            ? (parts[2], env)
            : null;
    }

    /// <summary>
    /// FR-145, §4.20: Worker runtime secrets are shared by preview and production, so a sensitive variable for Cloudflare
    /// Workers must apply to production too (or to both); a preview-only one is refused (<c>VALIDATION</c>).
    /// </summary>
    public static Result ValidateRuntimeSecret(bool sensitive, IReadOnlyCollection<DeploymentEnvironment> environments)
    {
        ArgumentNullException.ThrowIfNull(environments);
        return sensitive && environments.Count > 0 && !environments.Contains(DeploymentEnvironment.Production)
            ? Result.Failure(new DomainError(
                ErrorCodes.Validation,
                "Cloudflare Workers secrets are shared by preview and production, so a secret can't be preview-only.")
            { Field = "targets" })
            : Result.Success();
    }

    /// <summary>
    /// FR-150: the body of <c>POST /workers/scripts/{name}/deployments</c> that makes <paramref name="versionId"/> current:
    /// <c>{strategy: "percentage", versions: [{version_id, percentage: 100}]}</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, object> RollbackDeployment(string versionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(versionId);
        return new Dictionary<string, object>
        {
            ["strategy"] = "percentage",
            ["versions"] = new[] { new Dictionary<string, object> { ["version_id"] = versionId, ["percentage"] = 100 } },
        };
    }

    /// <summary>The <c>workers.dev</c> URL of a Worker on the account's subdomain.</summary>
    public static string WorkersDevUrl(string scriptName, string subdomain) => $"https://{scriptName}.{subdomain}.workers.dev";

    /// <summary>
    /// §4.20: the stable production URL of a Cloudflare Pages project from one of its deployment URLs
    /// (<c>https://&lt;hash&gt;.&lt;project&gt;.pages.dev</c> → <c>https://&lt;project&gt;.pages.dev</c>); <c>null</c> when
    /// the URL isn't a per-deployment <c>pages.dev</c> URL.
    /// </summary>
    public static string? PagesProductionUrl(string? deploymentUrl)
    {
        if (!Uri.TryCreate(deploymentUrl, UriKind.Absolute, out var uri) || !uri.Host.EndsWith(".pages.dev", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var labels = uri.Host.Split('.');
        return labels.Length == 4 ? $"https://{string.Join('.', labels[1..])}" : null;
    }

    /// <summary>
    /// §4.20 idempotency: whether a build created at <paramref name="createdOn"/> may be adopted for an AIDE row created at
    /// <paramref name="createdAfter"/> (<c>null</c>: by commit alone, the production auto-deploy path). Cloudflare can't tag
    /// a build with the row, so a manual or redeploy row adopts only builds made after it (never an earlier build of the
    /// commit, nor one of unknown age).
    /// </summary>
    public static bool Adoptable(DateTimeOffset? createdOn, DateTimeOffset? createdAfter) =>
        createdAfter is not { } after || (createdOn is { } created && created >= after);

    /// <summary>Whether a Worker version's annotations name <paramref name="sha"/> (at least its first 7 characters).</summary>
    public static bool VersionMatchesCommit(IEnumerable<string?> annotationValues, string sha)
    {
        ArgumentNullException.ThrowIfNull(annotationValues);
        if (string.IsNullOrEmpty(sha) || sha.Length < 7)
        {
            return false;
        }

        var shortSha = sha[..7];
        return annotationValues.Any(v => v is not null && v.Contains(shortSha, StringComparison.OrdinalIgnoreCase));
    }

    private static string Root(string rootDirectory) => rootDirectory is "" or "." ? "/" : rootDirectory;
}
