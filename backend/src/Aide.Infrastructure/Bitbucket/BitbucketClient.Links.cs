using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Aide.Infrastructure.Bitbucket;

/// <summary>A repository webhook: its UUID, URL, whether it is active and its events.</summary>
public sealed record BitbucketHook(string? Uuid, string? Url, bool Active, IReadOnlyList<string> Events);

/// <summary>
/// §4.19 "Netlify link support": a repository's deploy keys (always read-only on Bitbucket) and webhooks.
/// <c>description</c> on a webhook is required by Bitbucket's API, though §4.19 doesn't list it.
/// </summary>
public sealed partial class BitbucketClient
{
    private static readonly string[] PushEvents = ["repo:push"];

    /// <summary>The public keys of <c>GET …/deploy-keys</c> (up to <see cref="MaxListPages"/> pages).</summary>
    public Task<IReadOnlyList<string?>> ListDeployKeysAsync(BitbucketAuth auth, string fullName, CancellationToken cancellationToken) =>
        ListLinksAsync(auth, $"{RepoPath(fullName)}/deploy-keys", k => Str(k, "key"), cancellationToken);

    /// <summary><c>POST …/deploy-keys {key, label}</c>.</summary>
    public async Task AddDeployKeyAsync(BitbucketAuth auth, string fullName, string label, string publicKey, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, $"{RepoPath(fullName)}/deploy-keys", auth);
        request.Content = JsonContent.Create(new { key = publicKey, label });
        using var response = await SendAsync(request, cancellationToken);
    }

    /// <summary><c>GET …/hooks</c> (up to <see cref="MaxListPages"/> pages).</summary>
    public Task<IReadOnlyList<BitbucketHook>> ListHooksAsync(BitbucketAuth auth, string fullName, CancellationToken cancellationToken) =>
        ListLinksAsync(
            auth,
            $"{RepoPath(fullName)}/hooks",
            h => new BitbucketHook(
                Str(h, "uuid"),
                Str(h, "url"),
                Bool(h, "active") == true,
                h.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array
                    ? [.. events.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                    : []),
            cancellationToken);

    /// <summary><c>POST …/hooks {description, url, events: ["repo:push"], active: true}</c>.</summary>
    public async Task AddPushHookAsync(BitbucketAuth auth, string fullName, string description, string url, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, $"{RepoPath(fullName)}/hooks", auth);
        request.Content = JsonContent.Create(new { description, url, events = PushEvents, active = true });
        using var response = await SendAsync(request, cancellationToken);
    }

    /// <summary><c>PUT …/hooks/{uuid}</c> with the same body: the existing webhook fires on pushes again.</summary>
    public async Task EnablePushHookAsync(BitbucketAuth auth, string fullName, string uuid, string description, string url, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Put, $"{RepoPath(fullName)}/hooks/{Segment(uuid)}", auth);
        request.Content = JsonContent.Create(new { description, url, events = PushEvents, active = true });
        using var response = await SendAsync(request, cancellationToken);
    }

    private async Task<IReadOnlyList<T>> ListLinksAsync<T>(BitbucketAuth auth, string path, Func<JsonElement, T> read, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        for (var page = 1; page <= MaxListPages; page++)
        {
            using var json = await GetJsonAsync($"{path}?pagelen=100&page={page.ToString(CultureInfo.InvariantCulture)}", auth, cancellationToken);
            items.AddRange(Values(json.RootElement).Select(read));
            if (!HasNext(json.RootElement))
            {
                break;
            }
        }

        return items;
    }
}
