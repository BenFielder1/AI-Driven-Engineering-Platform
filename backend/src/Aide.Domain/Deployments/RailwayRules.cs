using Aide.Domain.Entities;

namespace Aide.Domain.Deployments;

/// <summary>A Railway target's resource: project, service and (when known) the production environment.</summary>
public sealed record RailwayResource(string ProjectId, string ServiceId, string? ProductionEnvironmentId);

/// <summary>
/// FR-144, §4.20: the Railway service instance settings AIDE sets (<c>serviceInstanceUpdate</c>): root directory, the
/// Dockerfile or build and start commands, health check, watch patterns and the pre-deploy command.
/// </summary>
public sealed record RailwayInstanceSettings(
    string? RootDirectory,
    string? DockerfilePath,
    string? BuildCommand,
    string? StartCommand,
    string? HealthcheckPath,
    IReadOnlyList<string>? WatchPatterns,
    IReadOnlyList<string>? PreDeployCommand);

/// <summary>
/// FR-144, FR-145, FR-147, FR-150, §4.20 Railway rules: the resource id encoding, the <c>aide-preview</c> environment,
/// deployment states, instance settings from the build configuration, env ids, the Git-not-connected refusal and the
/// polling floor (20 s: the Free plan allows 100 requests an hour). Pure.
/// </summary>
public static class RailwayRules
{
    public const string PreviewEnvironmentName = "aide-preview";
    public const string ProductionEnvironmentName = "production";

    /// <summary>The port AIDE sets (<c>PORT</c>) on a backend service it creates, so the private-network origin resolves.</summary>
    public const string BackendPort = "8080";

    /// <summary>§4.20: Railway is polled at most every 20 seconds.</summary>
    public static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The target's <c>resource_id</c>: <c>&lt;project id&gt;:&lt;service id&gt;:&lt;production environment id&gt;</c>
    /// (Railway ids are UUIDs, so <c>:</c> never occurs in them); the environment is optional.
    /// </summary>
    public static string ResourceId(RailwayResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.ProductionEnvironmentId is { Length: > 0 } env
            ? $"{resource.ProjectId}:{resource.ServiceId}:{env}"
            : $"{resource.ProjectId}:{resource.ServiceId}";
    }

    /// <summary>The project, service and environment of a <c>resource_id</c>; <c>null</c> when it isn't one (a bare project id).</summary>
    public static RailwayResource? ParseResourceId(string? id)
    {
        var parts = id?.Split(':') ?? [];
        return parts.Length is 2 or 3 && parts.All(p => p.Length > 0)
            ? new RailwayResource(parts[0], parts[1], parts.Length == 3 ? parts[2] : null)
            : null;
    }

    /// <summary>
    /// §4.20 state mapping: <c>QUEUED</c>, <c>WAITING</c>, <c>INITIALIZING</c> → queued; <c>BUILDING</c>, <c>DEPLOYING</c> →
    /// building; <c>SUCCESS</c> → ready; <c>FAILED</c>, <c>CRASHED</c> → error; <c>SKIPPED</c>, <c>REMOVED</c> → canceled.
    /// Unknown (e.g. <c>REMOVING</c>, <c>SLEEPING</c>): building, except <c>SLEEPING</c> (a served deployment) ready.
    /// </summary>
    public static DeploymentStatus StateOf(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "QUEUED" or "WAITING" or "INITIALIZING" => DeploymentStatus.Queued,
        "SUCCESS" or "SLEEPING" => DeploymentStatus.Ready,
        "FAILED" or "CRASHED" => DeploymentStatus.Error,
        "SKIPPED" or "REMOVED" => DeploymentStatus.Canceled,
        _ => DeploymentStatus.Building,
    };

    /// <summary>
    /// FR-144: the instance settings for a target: its root directory (as an absolute repository path), the Dockerfile
    /// (relative to the root directory, where Railway builds) or
    /// the build and start commands, the health check, watch patterns limited to the root directory (so a change elsewhere
    /// in a monorepo doesn't redeploy it) and the pre-deploy command (the EF migration bundle, Alembic …).
    /// </summary>
    public static RailwayInstanceSettings InstanceSettings(DeploymentBuildConfig config, string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(config);
        var root = rootDirectory is "" or "." ? null : rootDirectory.Trim('/');
        var docker = config.DockerfilePath is not null;
        return new RailwayInstanceSettings(
            root is null ? null : "/" + root,
            docker ? RenderRules.RelativeToRoot(config.DockerfilePath!, root ?? string.Empty)[2..] : null,
            docker ? null : config.BuildCommand,
            docker ? null : config.StartCommand,
            config.HealthCheckPath,
            root is null ? null : [$"/{root}/**"],
            config.PreDeployCommand is { Length: > 0 } preDeploy ? [preDeploy] : null);
    }

    /// <summary>The env id AIDE records for a Railway variable: the environment and key (Railway has no ids).</summary>
    public static string EnvId(string key, DeploymentEnvironment environment) => RenderRules.EnvId(key, environment);

    public static (string Key, DeploymentEnvironment Environment)? ParseEnvId(string? id) => RenderRules.ParseEnvId(id);

    /// <summary>FR-144: whether Railway refused a service because its GitHub app can't reach the repository.</summary>
    public static bool IsGitNotConnected(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        var lower = message.ToLowerInvariant();
        return (lower.Contains("repo", StringComparison.Ordinal) || lower.Contains("github", StringComparison.Ordinal))
            && (lower.Contains("not found", StringComparison.Ordinal) || lower.Contains("access", StringComparison.Ordinal)
                || lower.Contains("install", StringComparison.Ordinal) || lower.Contains("permission", StringComparison.Ordinal));
    }

    /// <summary>A deployment's <c>meta.commitHash</c> is <paramref name="sha"/> (either may be abbreviated, at least 7 characters).</summary>
    public static bool SameCommit(string? commitHash, string? sha) => NetlifyRules.SameCommit(commitHash, sha);

    /// <summary>The URL of a Railway domain (<c>todo-api-production.up.railway.app</c>).</summary>
    public static string? Url(string? domain) =>
        string.IsNullOrEmpty(domain) ? null
        : domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? domain.TrimEnd('/')
        : $"https://{domain.TrimEnd('/')}";
}
