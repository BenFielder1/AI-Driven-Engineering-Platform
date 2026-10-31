using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.Infrastructure.Deployments;

/// <summary>FR-144, §4.20: Render request bodies (absent fields are left out, not sent as null).</summary>
public static class RenderPayloads
{
    /// <summary>
    /// <c>POST /v1/services</c>: <c>{type, name, ownerId, repo, branch, autoDeployTrigger, rootDir, serviceDetails}</c>.
    /// A static site's details are <c>{buildCommand, publishPath, pullRequestPreviewsEnabled: "no"}</c>; a web service's
    /// <c>{runtime, envSpecificDetails, preDeployCommand, healthCheckPath, plan, region, pullRequestPreviewsEnabled: "no"}</c>
    /// with <c>envSpecificDetails</c> <c>{dockerfilePath, dockerContext}</c> (Docker) or <c>{buildCommand, startCommand}</c>.
    /// </summary>
    public static Dictionary<string, object?> CreateService(RenderServiceSpec spec, string ownerId, string repoUrl)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Dictionary<string, object?> details;
        if (spec.Type == RenderRules.StaticSite)
        {
            details = Compact(
                ("buildCommand", spec.BuildCommand),
                ("publishPath", spec.PublishPath),
                ("pullRequestPreviewsEnabled", "no"));
        }
        else
        {
            var envSpecific = spec.Runtime == "docker"
                ? Compact(("dockerfilePath", spec.DockerfilePath), ("dockerContext", spec.DockerContext))
                : Compact(("buildCommand", spec.BuildCommand), ("startCommand", spec.StartCommand));
            details = Compact(
                ("runtime", spec.Runtime),
                ("envSpecificDetails", envSpecific),
                ("preDeployCommand", spec.PreDeployCommand),
                ("healthCheckPath", spec.HealthCheckPath),
                ("plan", spec.Plan),
                ("region", spec.Region),
                ("pullRequestPreviewsEnabled", "no"));
        }

        return Compact(
            ("type", spec.Type),
            ("name", spec.Name),
            ("ownerId", ownerId),
            ("repo", repoUrl),
            ("branch", spec.Branch),
            ("autoDeployTrigger", spec.AutoDeployTrigger),
            ("rootDir", spec.RootDir.Length == 0 ? null : spec.RootDir),
            ("serviceDetails", details));
    }

    private static Dictionary<string, object?> Compact(params (string Key, object? Value)[] fields) =>
        fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
}

/// <summary>
/// §4.20: Render's log limit (30 requests a minute per user), enforced in memory per process before calling. Users are
/// keyed by a hash of their API key (never the key itself). A refused request throws
/// <see cref="DeploymentFailure.RateLimited"/> with the wait (jobs defer without counting an attempt).
/// </summary>
public sealed class RenderLogRateLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _users = new(StringComparer.Ordinal);

    public void Take(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var now = time.GetUtcNow();
        var list = _users.GetOrAdd(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), _ => []);
        lock (list)
        {
            list.RemoveAll(t => t <= now - TimeSpan.FromMinutes(1));
            var decision = RenderRules.DecideLogRequest(list, now);
            if (!decision.Allowed)
            {
                throw new DeploymentProviderException(
                    DeploymentFailure.RateLimited,
                    429,
                    $"Render allows {RenderRules.LogRequestsPerMinute} log requests a minute per user; AIDE waits.",
                    retryAt: decision.RetryAt,
                    platform: DeploymentProvider.Render);
            }

            list.Add(now);
        }
    }
}
