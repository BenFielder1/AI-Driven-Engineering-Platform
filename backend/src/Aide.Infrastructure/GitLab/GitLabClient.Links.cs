using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Aide.Infrastructure.GitLab;

/// <summary>A project deploy key: its public key and whether it can push.</summary>
public sealed record GitLabDeployKey(string? Key, bool CanPush);

/// <summary>A project webhook: its id, URL and whether it fires on pushes.</summary>
public sealed record GitLabHook(long Id, string? Url, bool PushEvents);

/// <summary>§4.19 "Netlify link support": a project's deploy keys and webhooks.</summary>
public sealed partial class GitLabClient
{
    /// <summary><c>GET /projects/:id/deploy_keys</c> (up to <see cref="MaxRefPages"/> pages of 100).</summary>
    public Task<IReadOnlyList<GitLabDeployKey>> ListDeployKeysAsync(string token, string projectId, CancellationToken cancellationToken) =>
        ListLinksAsync(
            token,
            $"api/v4/projects/{Id(projectId)}/deploy_keys",
            k => new GitLabDeployKey(Str(k, "key"), k.TryGetProperty("can_push", out var push) && push.ValueKind == JsonValueKind.True),
            cancellationToken);

    /// <summary>§8: <c>POST /projects/:id/deploy_keys {title, key, can_push: false}</c>: always read-only.</summary>
    public async Task AddReadOnlyDeployKeyAsync(string token, string projectId, string title, string publicKey, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, Url($"api/v4/projects/{Id(projectId)}/deploy_keys"), token);
        request.Content = JsonContent.Create(new { title, key = publicKey, can_push = false });
        using var response = await SendAsync(request, cancellationToken);
    }

    /// <summary><c>GET /projects/:id/hooks</c> (up to <see cref="MaxRefPages"/> pages of 100).</summary>
    public Task<IReadOnlyList<GitLabHook>> ListHooksAsync(string token, string projectId, CancellationToken cancellationToken) =>
        ListLinksAsync(
            token,
            $"api/v4/projects/{Id(projectId)}/hooks",
            h => new GitLabHook(Long(h, "id") ?? 0, Str(h, "url"), h.TryGetProperty("push_events", out var push) && push.ValueKind == JsonValueKind.True),
            cancellationToken);

    /// <summary><c>POST /projects/:id/hooks {url, push_events: true}</c>.</summary>
    public async Task AddPushHookAsync(string token, string projectId, string url, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, Url($"api/v4/projects/{Id(projectId)}/hooks"), token);
        request.Content = JsonContent.Create(new { url, push_events = true });
        using var response = await SendAsync(request, cancellationToken);
    }

    /// <summary><c>PUT /projects/:id/hooks/:hook_id {url, push_events: true}</c>.</summary>
    public async Task EnablePushHookAsync(string token, string projectId, long hookId, string url, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Put, Url($"api/v4/projects/{Id(projectId)}/hooks/{hookId.ToString(CultureInfo.InvariantCulture)}"), token);
        request.Content = JsonContent.Create(new { url, push_events = true });
        using var response = await SendAsync(request, cancellationToken);
    }

    private async Task<IReadOnlyList<T>> ListLinksAsync<T>(string token, string path, Func<JsonElement, T> read, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        for (var page = 1; page <= MaxRefPages; page++)
        {
            using var request = Authorized(HttpMethod.Get, Url($"{path}?per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}"), token);
            using var response = await SendAsync(request, cancellationToken);
            using var json = await ReadJsonAsync(response, cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            items.AddRange(json.RootElement.EnumerateArray().Select(read));
            if (!response.Headers.TryGetValues("X-Next-Page", out var next) || string.IsNullOrWhiteSpace(next.FirstOrDefault()))
            {
                break;
            }
        }

        return items;
    }
}
