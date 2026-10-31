using System.Globalization;
using Aide.Domain.Entities;

namespace Aide.Domain.Deployments;

/// <summary>A Render rewrite rule (static sites): <c>{type: "rewrite", source, destination}</c>.</summary>
public sealed record RenderRoute(string Type, string Source, string Destination);

/// <summary>
/// FR-144, §4.20: the Render service AIDE creates for a target: a <c>static_site</c> (<see cref="BuildCommand"/>,
/// <see cref="PublishPath"/>) or a <c>web_service</c> (<see cref="Runtime"/> <c>docker</c>/<c>node</c>/<c>python</c>, the
/// Dockerfile or build/start commands, pre-deploy, health check, plan and region), in <see cref="RootDir"/>, tracking
/// <see cref="Branch"/> with <see cref="AutoDeployTrigger"/> (<c>commit</c> for production, <c>off</c> for the preview).
/// <see cref="DockerfilePath"/> and <see cref="DockerContext"/> are relative to the root directory, as Render reads them.
/// </summary>
public sealed record RenderServiceSpec(
    string Type,
    string Name,
    string RootDir,
    string Branch,
    string AutoDeployTrigger,
    string? Runtime,
    string? DockerfilePath,
    string? DockerContext,
    string? BuildCommand,
    string? StartCommand,
    string? PublishPath,
    string? PreDeployCommand,
    string? HealthCheckPath,
    string? Plan,
    string? Region);

/// <summary>
/// FR-144, FR-145, FR-147, FR-150, §4.20 Render rules: the service spec from the build configuration, the
/// <c>-preview</c> service, deploy states, the AIDE deployment id (<c>service|deploy</c>), env ids, the proxy route and
/// the log rate limit (30 a minute per user). Pure.
/// </summary>
public static class RenderRules
{
    public const string StaticSite = "static_site";
    public const string WebService = "web_service";
    public const string AutoDeployOnCommit = "commit";
    public const string AutoDeployOff = "off";
    public const string PreviewSuffix = "-preview";

    /// <summary>The deploy status of a failed pre-deploy command (a migration, FR-147: a critical finding).</summary>
    public const string PreDeployFailed = "pre_deploy_failed";

    /// <summary>The prefix of a pre-deploy failure's message, so later steps (M30 slice 3) can tell it from a build failure.</summary>
    public const string PreDeployFailedMessage = "Render: the pre-deploy command failed (pre_deploy_failed).";

    /// <summary>§4.20: Render allows 30 log requests a minute per user.</summary>
    public const int LogRequestsPerMinute = 30;

    /// <summary>The preview service's name: <c>&lt;name&gt;-preview</c>.</summary>
    public static string PreviewName(string name) => name + PreviewSuffix;

    /// <summary>
    /// §4.20 state mapping: <c>created</c>, <c>queued</c> → queued; <c>build_in_progress</c>, <c>update_in_progress</c>,
    /// <c>pre_deploy_in_progress</c> → building; <c>live</c> → ready; <c>build_failed</c>, <c>update_failed</c>,
    /// <c>pre_deploy_failed</c> → error; <c>canceled</c>, <c>deactivated</c> → canceled. Unknown: building.
    /// </summary>
    public static DeploymentStatus StateOf(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "created" or "queued" => DeploymentStatus.Queued,
        "live" => DeploymentStatus.Ready,
        "build_failed" or "update_failed" or PreDeployFailed => DeploymentStatus.Error,
        "canceled" or "deactivated" => DeploymentStatus.Canceled,
        _ => DeploymentStatus.Building,
    };

    /// <summary>Whether the deploy failed in its pre-deploy command (the migration), not its build.</summary>
    public static bool IsPreDeployFailure(string? status) => string.Equals(status?.Trim(), PreDeployFailed, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// FR-144: the service for <paramref name="component"/>: Next.js (a start command) a Node web service, another
    /// frontend a static site publishing its output, and a backend a Docker web service with the health check and the
    /// pre-deploy migrate command, tracking <paramref name="branch"/> (the repository's default branch; the preview's is
    /// re-pointed at the version branch per deploy). <paramref name="preview"/>: the <c>-preview</c> service, auto-deploy off.
    /// </summary>
    public static RenderServiceSpec ServiceSpec(
        DeploymentComponent component, DeploymentBuildConfig config, string rootDirectory, string name, bool preview, string? plan, string? region, string branch)
    {
        ArgumentException.ThrowIfNullOrEmpty(branch);
        ArgumentNullException.ThrowIfNull(config);
        var root = rootDirectory is "" or "." ? string.Empty : rootDirectory.Trim('/');
        var serviceName = preview ? PreviewName(name) : name;
        var trigger = preview ? AutoDeployOff : AutoDeployOnCommit;
        var isStatic = component == DeploymentComponent.Frontend && config.DockerfilePath is null && config.StartCommand is null;
        if (isStatic)
        {
            return new RenderServiceSpec(
                StaticSite, serviceName, root, branch, trigger, null, null, null, config.BuildCommand, null, config.OutputDirectory ?? "dist", null, null, null, null);
        }

        var docker = config.DockerfilePath is not null;
        return new RenderServiceSpec(
            WebService,
            serviceName,
            root,
            branch,
            trigger,
            docker ? "docker" : RuntimeOf(config),
            docker ? RelativeToRoot(config.DockerfilePath!, root) : null,
            docker ? "." : null,
            docker ? null : config.BuildCommand,
            docker ? null : config.StartCommand,
            null,
            config.PreDeployCommand,
            config.HealthCheckPath,
            plan,
            region);
    }

    /// <summary>
    /// A repository path (the build configuration's <c>backend/Dockerfile</c>) relative to the service's root directory
    /// (<c>./Dockerfile</c>), as Render resolves Docker paths from the root directory.
    /// </summary>
    public static string RelativeToRoot(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.TrimStart('.', '/');
        if (root.Length > 0 && trimmed.StartsWith(root + "/", StringComparison.Ordinal))
        {
            trimmed = trimmed[(root.Length + 1)..];
        }

        return "./" + trimmed;
    }

    /// <summary>FR-146: the static site's proxy rule, <c>/api/*</c> rewritten to <paramref name="apiOrigin"/><c>/api/*</c>.</summary>
    public static RenderRoute ApiProxyRoute(string apiOrigin)
    {
        ArgumentNullException.ThrowIfNull(apiOrigin);
        return new RenderRoute("rewrite", "/api/*", apiOrigin.TrimEnd('/') + "/api/*");
    }

    /// <summary>
    /// FR-146: the static site's routes after setting the proxy. Render's <c>PUT /routes</c> replaces them all, so the
    /// <paramref name="existing"/> rules (in their order, e.g. the SPA fallback <c>/* → /index.html</c>) are kept, any
    /// earlier <c>/api/*</c> rule is replaced, and the proxy goes first, ahead of every catch-all (index = priority, lowest first).
    /// </summary>
    public static IReadOnlyList<RenderRoute> MergeRoutes(IReadOnlyList<RenderRoute> existing, RenderRoute apiRoute)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(apiRoute);
        return [apiRoute, .. existing.Where(r => !string.Equals(r.Source, apiRoute.Source, StringComparison.Ordinal))];
    }

    /// <summary>
    /// The AIDE deployment id of a Render deploy: <c>&lt;service id&gt;|&lt;deploy id&gt;</c>, so polls, logs and rollback
    /// reach the right service (production or <c>-preview</c>).
    /// </summary>
    public static string DeploymentId(string serviceId, string deployId) => $"{serviceId}|{deployId}";

    /// <summary>The service and deploy of an AIDE deployment id; a bare deploy id belongs to <paramref name="defaultService"/>.</summary>
    public static (string ServiceId, string DeployId) ParseDeploymentId(string id, string? defaultService)
    {
        ArgumentNullException.ThrowIfNull(id);
        var bar = id.IndexOf('|', StringComparison.Ordinal);
        return bar > 0 ? (id[..bar], id[(bar + 1)..]) : (defaultService ?? string.Empty, id);
    }

    /// <summary>The env id AIDE records for a Render variable: the environment and key (Render has no ids).</summary>
    public static string EnvId(string key, DeploymentEnvironment environment) =>
        $"{(environment == DeploymentEnvironment.Preview ? "preview" : "production")}:{key}";

    public static (string Key, DeploymentEnvironment Environment)? ParseEnvId(string? id)
    {
        var colon = id?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        if (colon <= 0)
        {
            return null;
        }

        var env = id![..colon] switch
        {
            "preview" => DeploymentEnvironment.Preview,
            "production" => DeploymentEnvironment.Production,
            _ => (DeploymentEnvironment?)null,
        };
        return env is { } e && colon < id.Length - 1 ? (id[(colon + 1)..], e) : null;
    }

    /// <summary>
    /// §4.20 env sync: Render's <c>PUT /env-vars</c> replaces the whole set, so the new set is the service's current
    /// variables without <paramref name="deletes"/>, with <paramref name="writes"/> applied (ordered by key).
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> MergeEnv(
        IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> writes, IReadOnlyCollection<string> deletes)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(deletes);
        var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in current)
        {
            if (!deletes.Contains(key))
            {
                merged[key] = value;
            }
        }

        foreach (var (key, value) in writes)
        {
            merged[key] = value;
        }

        return [.. merged];
    }

    /// <summary>
    /// FR-144: whether Render refused a create because it can't reach the repository (the Git host isn't connected in
    /// Render, or its app lacks access to the repository).
    /// </summary>
    public static bool IsGitNotConnected(int? status, string? message)
    {
        if (status is not (400 or 403 or 404 or 422) || string.IsNullOrEmpty(message))
        {
            return false;
        }

        var lower = message.ToLowerInvariant();
        return (lower.Contains("repo", StringComparison.Ordinal) || lower.Contains("git", StringComparison.Ordinal))
            && (lower.Contains("access", StringComparison.Ordinal) || lower.Contains("not found", StringComparison.Ordinal)
                || lower.Contains("connect", StringComparison.Ordinal) || lower.Contains("credential", StringComparison.Ordinal)
                || lower.Contains("invalid", StringComparison.Ordinal) || lower.Contains("install", StringComparison.Ordinal));
    }

    /// <summary>FR-144: a plan refusal on create (e.g. the free tier's service limit, or a paid plan the workspace can't use).</summary>
    public static bool IsPlanLimit(int? status, string? message) =>
        status is 400 or 402 or 403 or 422 && message is not null
        && (message.Contains("plan", StringComparison.OrdinalIgnoreCase) || message.Contains("payment", StringComparison.OrdinalIgnoreCase)
            || message.Contains("limit", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// §4.20: the log limit (30 a minute per user): allowed when fewer than 30 requests fell in the last minute, else when
    /// the oldest of them leaves the window.
    /// </summary>
    public static NetlifyDeployLimitDecision DecideLogRequest(IReadOnlyCollection<DateTimeOffset> recent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(recent);
        var window = recent.Where(t => t > now - TimeSpan.FromMinutes(1)).OrderBy(t => t).ToList();
        return window.Count < LogRequestsPerMinute
            ? new NetlifyDeployLimitDecision(true, null)
            : new NetlifyDeployLimitDecision(false, window[window.Count - LogRequestsPerMinute] + TimeSpan.FromMinutes(1));
    }

    /// <summary>The repository URL Render's <c>repo</c> takes (GitHub, GitLab, Bitbucket).</summary>
    public static string? RepoUrl(string? host, string? fullName) => fullName is null ? null : host switch
    {
        "github" => $"https://github.com/{fullName}",
        "gitlab" => $"https://gitlab.com/{fullName}",
        "bitbucket" => $"https://bitbucket.org/{fullName}",
        _ => null,
    };

    /// <summary>A time as Render's logs API takes it (RFC 3339, UTC).</summary>
    public static string LogTime(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string RuntimeOf(DeploymentBuildConfig config)
    {
        var start = config.StartCommand ?? string.Empty;
        return start.StartsWith("uvicorn", StringComparison.Ordinal) || start.StartsWith("gunicorn", StringComparison.Ordinal)
            || start.StartsWith("python", StringComparison.Ordinal)
            ? "python"
            : "node";
    }
}
