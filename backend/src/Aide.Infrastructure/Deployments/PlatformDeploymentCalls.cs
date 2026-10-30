using System.Collections.Concurrent;
using System.Text.Json;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Platforms;
using Aide.Infrastructure.Platforms;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// §4.20: a platform API failure (<see cref="PlatformApiException"/>) as a deployment failure: a rejected token is
/// <see cref="DeploymentFailure.CredentialRejected"/>, a 403 for permissions <see cref="DeploymentFailure.Forbidden"/>,
/// 429 <see cref="DeploymentFailure.RateLimited"/> (with its reset), 404 <see cref="DeploymentFailure.NotFound"/>, another
/// 4xx <see cref="DeploymentFailure.Rejected"/> and anything else <see cref="DeploymentFailure.Upstream"/>.
/// </summary>
internal static class PlatformDeploymentCalls
{
    public static async Task<T> Call<T>(DeploymentProvider platform, Func<Task<T>> call, Func<PlatformApiException, DeploymentFailure?>? classify = null)
    {
        try
        {
            return await call();
        }
        catch (PlatformApiException ex)
        {
            throw Map(platform, ex, classify?.Invoke(ex));
        }
    }

    public static DeploymentProviderException Map(DeploymentProvider platform, PlatformApiException ex, DeploymentFailure? special = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var failure = special ?? ex.Failure switch
        {
            PlatformFailure.Rejected => DeploymentFailure.CredentialRejected,
            PlatformFailure.MissingPermissions => DeploymentFailure.Forbidden,
            PlatformFailure.RateLimited => DeploymentFailure.RateLimited,
            PlatformFailure.NotFound => DeploymentFailure.NotFound,
            _ when ex.Status is >= 400 and < 500 => DeploymentFailure.Rejected,
            _ => DeploymentFailure.Upstream,
        };
        return new DeploymentProviderException(
            failure,
            ex.Status,
            ex.Message,
            failure == DeploymentFailure.GitNotConnected ? DeploymentPlatforms.GitConnectUrl(platform, "github") : null,
            ex.RetryAt,
            platform);
    }

    /// <summary>A string or number property, or <c>null</c>.</summary>
    public static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    public static JsonElement Property(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value : default;

    /// <summary>The array at <paramref name="element"/> itself, or under <paramref name="property"/>.</summary>
    public static IEnumerable<JsonElement> Items(JsonElement element, string? property = null)
    {
        var list = element.ValueKind == JsonValueKind.Array ? element : property is null ? default : Property(element, property);
        return list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];
    }

    /// <summary>The URL with <c>https://</c> (Cloudflare and Netlify sometimes return a bare host), without a trailing slash.</summary>
    public static string? Https(string? url) =>
        string.IsNullOrEmpty(url) ? null
        : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url.TrimEnd('/')
        : $"https://{url.TrimEnd('/')}";
}

/// <summary>
/// §4.20: Netlify's deploy-creation limit (3 a minute, 100 a day per account), enforced in memory before calling (like
/// the M28 Bitbucket bucket; per process). A refused creation throws <see cref="DeploymentFailure.RateLimited"/> with the
/// wait, which jobs defer to without counting an attempt. A Netlify 429 blocks the account until its reset (however far
/// off), separately from the counted creations, so it doesn't use up the daily budget.
/// </summary>
public sealed class NetlifyDeployRateLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _accounts = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _blockedUntil = new(StringComparer.Ordinal);

    /// <summary>Takes one deploy creation for <paramref name="account"/>, or throws the rate limit with its wait.</summary>
    public void Take(string account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var now = time.GetUtcNow();
        if (_blockedUntil.TryGetValue(account, out var until))
        {
            if (until > now)
            {
                throw Limited(until);
            }

            _blockedUntil.TryRemove(new KeyValuePair<string, DateTimeOffset>(account, until));
        }

        var list = _accounts.GetOrAdd(account, _ => []);
        lock (list)
        {
            list.RemoveAll(t => t <= now - TimeSpan.FromDays(1));
            var decision = NetlifyRules.Decide(list, now);
            if (!decision.Allowed)
            {
                throw Limited(decision.RetryAt);
            }

            list.Add(now);
        }
    }

    /// <summary>Netlify answered 429: no more creations for the account until <paramref name="retryAt"/> (default a minute).</summary>
    public void Block(string account, DateTimeOffset? retryAt)
    {
        ArgumentNullException.ThrowIfNull(account);
        var until = retryAt ?? time.GetUtcNow() + TimeSpan.FromMinutes(1);
        _blockedUntil.AddOrUpdate(account, until, (_, existing) => existing > until ? existing : until);
    }

    private static DeploymentProviderException Limited(DateTimeOffset? retryAt) => new(
        DeploymentFailure.RateLimited,
        429,
        $"Netlify allows {NetlifyRules.DeploysPerMinute} deploys a minute and {NetlifyRules.DeploysPerDay} a day per account; AIDE waits.",
        retryAt: retryAt,
        platform: DeploymentProvider.Netlify);
}
