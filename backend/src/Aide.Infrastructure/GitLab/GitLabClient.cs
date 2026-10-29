using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aide.Domain.GitHosts;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.GitLab;

/// <summary>A GitLab API failure. <see cref="Status"/> is <c>null</c> when GitLab couldn't be reached (network, timeout).
/// <see cref="UpstreamMessage"/> is GitLab's error text (never a token).</summary>
public class GitLabApiException : Exception
{
    public GitLabApiException()
    {
    }

    public GitLabApiException(string message)
        : base(message)
    {
    }

    public GitLabApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GitLabApiException(int? status, string? upstreamMessage, Exception? innerException = null)
        : base(upstreamMessage ?? (status is { } s ? $"GitLab answered HTTP {s}." : "GitLab couldn't be reached."), innerException)
    {
        Status = status;
        UpstreamMessage = upstreamMessage;
    }

    public int? Status { get; }

    public string? UpstreamMessage { get; }

    /// <summary>The OAuth <c>error</c> code of a token endpoint failure (<c>invalid_grant</c>…), if any.</summary>
    public string? OauthError { get; init; }

    /// <summary>For 429: how long GitLab asked to wait (<c>Retry-After</c>, or <c>RateLimit-Reset</c>).</summary>
    public TimeSpan? RetryAfter { get; init; }

    public bool IsUnauthorized => Status == 401;

    public bool IsRateLimited => Status == 429;
}

public sealed record GitLabVersionProbe(bool Reachable, string? Version, int? Status);

public sealed record GitLabUser(long Id, string Username, string? Name, string? Email, string? AvatarUrl, string? State)
{
    public string ExternalId => Id.ToString(CultureInfo.InvariantCulture);
}

/// <summary>OIDC <c>/oauth/userinfo</c>: the email and whether GitLab verified it.</summary>
public sealed record GitLabUserInfo(string? Subject, string? Email, bool EmailVerified);

/// <summary><c>GET /personal_access_tokens/self</c>: the token's scopes and expiry (personal, project and group tokens).</summary>
public sealed record GitLabTokenInfo(IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAt, bool Active, bool Revoked);

/// <summary>A token endpoint response (authorization code or refresh). Redacted <see cref="ToString"/>.</summary>
public sealed record GitLabOAuthTokens(string AccessToken, string? RefreshToken, long? ExpiresIn, IReadOnlyList<string> Scopes)
{
    public override string ToString() => $"GitLabOAuthTokens {{ ExpiresIn = {ExpiresIn}, Scopes = {string.Join(' ', Scopes)} }}";
}

public sealed record GitLabProject(
    long Id,
    string Name,
    string Path,
    string PathWithNamespace,
    string? DefaultBranch,
    string? WebUrl,
    string? NamespaceFullPath,
    string? Visibility,
    DateTimeOffset? LastActivityAt)
{
    public string ExternalId => Id.ToString(CultureInfo.InvariantCulture);
}

public sealed record GitLabProjectPage(IReadOnlyList<GitLabProject> Items, string? NextCursor);

public sealed record GitLabGroup(long Id, string Name, string FullPath, string? FullName, string? ProjectCreationLevel);

public sealed record GitLabNamespace(long Id, string Kind, string Name, string FullPath);

public sealed record GitLabCreateProject(string Name, string? Path, long? NamespaceId, string? Description, bool Private);

/// <summary>A <see cref="GitLabClient"/> bound to one instance's base URL (one client per instance, §8).</summary>
public interface IGitLabClients
{
    GitLabClient ForInstance(Uri instanceBaseUrl);
}

public sealed class GitLabClients(IHttpClientFactory http, IOptionsMonitor<GitLabOptions> options, TimeProvider time) : IGitLabClients
{
    public const string HttpClientName = "gitlab";

    public GitLabClient ForInstance(Uri instanceBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(instanceBaseUrl);
        var client = http.CreateClient(HttpClientName);
        client.Timeout = options.CurrentValue.RequestTimeout;
        return new GitLabClient(client, instanceBaseUrl, time);
    }
}

/// <summary>
/// FR-130–FR-132, §4.19: the GitLab REST API (<c>{base}/api/v4</c>, <c>Authorization: Bearer</c>) and OAuth endpoints of one
/// instance. Every request URL is built from the instance's base URL and checked to be on its origin before a token is
/// attached (§8: tokens are bound to the instance that issued them). Redirects are never followed (the handler has
/// <c>AllowAutoRedirect = false</c>; a 3xx is a failure). Non-success answers throw <see cref="GitLabApiException"/>; the SSRF
/// guard throws <see cref="GitHostBlockedException"/>.
/// </summary>
public sealed partial class GitLabClient
{
    public const int ProjectsPerPage = 30;

    private const int MaxGroupPages = 10;

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    internal GitLabClient(HttpClient http, Uri instanceBaseUrl, TimeProvider time)
    {
        _http = http;
        _time = time;
        BaseUrl = new Uri(instanceBaseUrl.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    /// <summary>The instance base URL with a trailing slash.</summary>
    public Uri BaseUrl { get; }

    /// <summary>FR-131: the authorize URL (code + PKCE S256).</summary>
    public Uri AuthorizeUrl(string clientId, string redirectUri, string state, string codeChallenge) =>
        Url("oauth/authorize?" + Query(
            ("client_id", clientId),
            ("redirect_uri", redirectUri),
            ("response_type", "code"),
            ("state", state),
            ("scope", GitLabRules.OauthScopeParameter),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256")));

    /// <summary>FR-130: <c>GET /api/v4/version</c> unauthenticated. 200 with a <c>version</c>, or 401 with GitLab's JSON
    /// error shape, is reachable; anything else isn't.</summary>
    public async Task<GitLabVersionProbe> ProbeVersionAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("api/v4/version"));
        using var response = await SendRawAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        using var json = await TryJsonAsync(response, cancellationToken);
        if (json?.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new GitLabVersionProbe(false, null, status);
        }

        var root = json.RootElement;
        return status switch
        {
            200 when Str(root, "version") is { } version => new GitLabVersionProbe(true, version, status),
            401 when root.TryGetProperty("message", out _) => new GitLabVersionProbe(true, null, status),
            _ => new GitLabVersionProbe(false, null, status),
        };
    }

    /// <summary><c>GET /user</c>: the token's user.</summary>
    public async Task<GitLabUser> GetUserAsync(string token, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync("api/v4/user", token, cancellationToken);
        var root = json.RootElement;
        return new GitLabUser(
            Long(root, "id") ?? throw new GitLabApiException(200, "GitLab's user has no id."),
            Str(root, "username") ?? throw new GitLabApiException(200, "GitLab's user has no username."),
            Str(root, "name"),
            Str(root, "email"),
            Str(root, "avatar_url"),
            Str(root, "state"));
    }

    /// <summary>OIDC <c>GET /oauth/userinfo</c>; <c>null</c> when GitLab refuses it (the email is then unverified).</summary>
    public async Task<GitLabUserInfo?> GetUserInfoAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonAsync("oauth/userinfo", token, cancellationToken);
            var root = json.RootElement;
            var verified = root.TryGetProperty("email_verified", out var v) && v.ValueKind == JsonValueKind.True;
            return new GitLabUserInfo(Str(root, "sub"), Str(root, "email"), verified);
        }
        catch (GitLabApiException ex) when (ex.Status is 401 or 403 or 404)
        {
            return null;
        }
    }

    /// <summary>FR-131: <c>GET /personal_access_tokens/self</c> (scopes, <c>expires_at</c>).</summary>
    public async Task<GitLabTokenInfo> GetTokenSelfAsync(string token, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync("api/v4/personal_access_tokens/self", token, cancellationToken);
        var root = json.RootElement;
        IReadOnlyList<string> scopes = root.TryGetProperty("scopes", out var s) && s.ValueKind == JsonValueKind.Array
            ? [.. s.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];
        DateTimeOffset? expires = Str(root, "expires_at") is { } date
            && DateOnly.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : null;
        var active = !root.TryGetProperty("active", out var a) || a.ValueKind != JsonValueKind.False;
        var revoked = root.TryGetProperty("revoked", out var r) && r.ValueKind == JsonValueKind.True;
        return new GitLabTokenInfo(scopes, expires, active, revoked);
    }

    /// <summary>FR-131: exchanges an authorization code (with the PKCE verifier).</summary>
    public Task<GitLabOAuthTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken) =>
        TokenAsync(
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("redirect_uri", redirectUri),
                new("client_id", clientId),
                new("client_secret", clientSecret),
                new("code_verifier", codeVerifier),
            ],
            cancellationToken);

    /// <summary>FR-131: refreshes an access token. GitLab rotates the refresh token: the old pair stops working.</summary>
    public Task<GitLabOAuthTokens> RefreshAsync(string clientId, string clientSecret, string refreshToken, string redirectUri, CancellationToken cancellationToken) =>
        TokenAsync(
            [
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
                new("redirect_uri", redirectUri),
                new("client_id", clientId),
                new("client_secret", clientSecret),
            ],
            cancellationToken);

    /// <summary>
    /// FR-132: projects the user can develop in (<c>membership=true&amp;min_access_level=30&amp;simple=true</c>), newest id
    /// first with keyset pagination; <paramref name="cursor"/> is the previous page's <see cref="GitLabProjectPage.NextCursor"/>.
    /// </summary>
    public async Task<GitLabProjectPage> ListProjectsAsync(string token, string? search, string? cursor, CancellationToken cancellationToken)
    {
        var query = new List<(string, string)>
        {
            ("membership", "true"),
            ("min_access_level", GitLabRules.DeveloperAccess.ToString(CultureInfo.InvariantCulture)),
            ("simple", "true"),
            ("pagination", "keyset"),
            ("order_by", "id"),
            ("sort", "desc"),
            ("per_page", ProjectsPerPage.ToString(CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add(("search", search.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query.Add(("id_before", cursor.Trim()));
        }

        using var request = Authorized(HttpMethod.Get, Url("api/v4/projects?" + Query([.. query])), token);
        using var response = await SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var items = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().Select(ToProject).ToList() : [];
        return new GitLabProjectPage(items, NextKeysetCursor(response.Headers));
    }

    /// <summary>FR-132: the groups where the user has at least <paramref name="minAccessLevel"/> (all pages, up to 1,000 groups).</summary>
    public async Task<IReadOnlyList<GitLabGroup>> ListGroupsAsync(string token, int minAccessLevel, CancellationToken cancellationToken)
    {
        var groups = new List<GitLabGroup>();
        for (var page = 1; page <= MaxGroupPages; page++)
        {
            var url = Url("api/v4/groups?" + Query(
                ("min_access_level", minAccessLevel.ToString(CultureInfo.InvariantCulture)),
                ("per_page", "100"),
                ("page", page.ToString(CultureInfo.InvariantCulture)),
                ("order_by", "path"),
                ("sort", "asc")));
            using var request = Authorized(HttpMethod.Get, url, token);
            using var response = await SendAsync(request, cancellationToken);
            using var json = await ReadJsonAsync(response, cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var group in json.RootElement.EnumerateArray())
            {
                if (Long(group, "id") is { } id && Str(group, "full_path") is { } fullPath)
                {
                    groups.Add(new GitLabGroup(id, Str(group, "name") ?? fullPath, fullPath, Str(group, "full_name"), Str(group, "project_creation_level")));
                }
            }

            if (!response.Headers.TryGetValues("X-Next-Page", out var next) || string.IsNullOrWhiteSpace(next.FirstOrDefault()))
            {
                break;
            }
        }

        return groups;
    }

    /// <summary><c>GET /namespaces/:id-or-path</c>; <c>null</c> when it doesn't exist or isn't visible.</summary>
    public async Task<GitLabNamespace?> GetNamespaceAsync(string token, string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonAsync("api/v4/namespaces/" + Uri.EscapeDataString(fullPath), token, cancellationToken);
            var root = json.RootElement;
            return Long(root, "id") is { } id
                ? new GitLabNamespace(id, Str(root, "kind") ?? "group", Str(root, "name") ?? fullPath, Str(root, "full_path") ?? fullPath)
                : null;
        }
        catch (GitLabApiException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// FR-132: <c>POST /projects</c> with <c>initialize_with_readme: true</c> and <c>default_branch: "main"</c>, so <c>main</c>
    /// exists with a README.
    /// </summary>
    public async Task<GitLabProject> CreateProjectAsync(string token, GitLabCreateProject project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var body = new Dictionary<string, object?>
        {
            ["name"] = project.Name,
            ["visibility"] = project.Private ? "private" : "public",
            ["initialize_with_readme"] = true,
            ["default_branch"] = "main",
        };
        if (project.Path is not null)
        {
            body["path"] = project.Path;
        }

        if (project.NamespaceId is { } namespaceId)
        {
            body["namespace_id"] = namespaceId;
        }

        if (!string.IsNullOrEmpty(project.Description))
        {
            body["description"] = project.Description;
        }

        using var request = Authorized(HttpMethod.Post, Url("api/v4/projects"), token);
        request.Content = JsonContent.Create(body);
        using var response = await SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        return ToProject(json.RootElement);
    }

    private async Task<GitLabOAuthTokens> TokenAsync(IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("oauth/token")) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await SendRawAsync(request, cancellationToken);
        using var json = await TryJsonAsync(response, cancellationToken);
        var root = json?.RootElement;
        if (!response.IsSuccessStatusCode || root is not { ValueKind: JsonValueKind.Object } body || Str(body, "access_token") is not { } access)
        {
            var error = root is { ValueKind: JsonValueKind.Object } r ? Str(r, "error") : null;
            var description = root is { ValueKind: JsonValueKind.Object } d ? Str(d, "error_description") : null;
            throw new GitLabApiException((int)response.StatusCode, description ?? error ?? "The GitLab token endpoint refused the request.")
            {
                OauthError = error,
                RetryAfter = RetryAfterOf(response),
            };
        }

        return new GitLabOAuthTokens(access, Str(body, "refresh_token"), Long(body, "expires_in"), GitLabRules.ParseScopes(Str(body, "scope")));
    }

    private async Task<JsonDocument> GetJsonAsync(string relative, string token, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, Url(relative), token);
        using var response = await SendAsync(request, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    /// <summary>§8: the token goes only to this instance's origin.</summary>
    private HttpRequestMessage Authorized(HttpMethod method, Uri url, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (!GitHostAddressRules.SameOrigin(BaseUrl, url))
        {
            throw new InvalidOperationException("A GitLab token may only be sent to the instance that issued it.");
        }

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <summary>Sends and throws <see cref="GitLabApiException"/> for any non-2xx answer (including redirects).</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await SendRawAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            using var json = await TryJsonAsync(response, cancellationToken);
            var status = (int)response.StatusCode;
            var message = status is >= 300 and < 400
                ? "GitLab answered with a redirect, which AIDE doesn't follow. Check the instance's base URL."
                : json is null ? null : ErrorText(json.RootElement);
            throw new GitLabApiException(status, message) { RetryAfter = RetryAfterOf(response) };
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            LastRateLimit = new RateLimitHeaders(
                Header(response, "RateLimit-Remaining"), Header(response, "RateLimit-Limit"), Header(response, "RateLimit-Reset"), Header(response, "Retry-After"));
            return response;
        }
        catch (HttpRequestException ex) when (GitHostBlockedException.Find(ex) is { } blocked)
        {
            throw blocked;
        }
        catch (HttpRequestException ex)
        {
            throw new GitLabApiException(null, null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitLabApiException(null, "GitLab didn't answer in time.", ex);
        }
    }

    /// <summary>§4.19: the rate-limit headers of this client's last answer (for the polling budget).</summary>
    public RateLimitHeaders LastRateLimit { get; private set; } = RateLimitHeaders.None;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await TryJsonAsync(response, cancellationToken) ?? throw new GitLabApiException((int)response.StatusCode, "GitLab's answer isn't JSON.");

    private static async Task<JsonDocument?> TryJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>GitLab's <c>message</c> (a string, or an object of field → messages) or <c>error</c>, flattened.</summary>
    public static string? ErrorText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("message", out var message))
        {
            switch (message.ValueKind)
            {
                case JsonValueKind.String:
                    return message.GetString();
                case JsonValueKind.Object:
                    var parts = new List<string>();
                    foreach (var field in message.EnumerateObject())
                    {
                        var texts = field.Value.ValueKind == JsonValueKind.Array
                            ? field.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString())
                            : field.Value.ValueKind == JsonValueKind.String ? [field.Value.GetString()] : [];
                        parts.AddRange(texts.Select(t => $"{field.Name} {t}"));
                    }

                    return parts.Count == 0 ? null : string.Join("; ", parts);
                case JsonValueKind.Array:
                    return string.Join("; ", message.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));
            }
        }

        return Str(root, "error_description") ?? Str(root, "error");
    }

    /// <summary>The <c>id_before</c> of the <c>Link: &lt;…&gt;; rel="next"</c> header (keyset pagination), if any.</summary>
    public static string? NextKeysetCursor(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var links))
        {
            return null;
        }

        foreach (var part in links.SelectMany(l => l.Split(',')))
        {
            var segments = part.Split(';', StringSplitOptions.TrimEntries);
            if (segments.Length < 2 || !segments.Skip(1).Any(s => s.Replace(" ", string.Empty, StringComparison.Ordinal) == "rel=\"next\"")
                || !Uri.TryCreate(segments[0].Trim('<', '>'), UriKind.Absolute, out var next))
            {
                continue;
            }

            foreach (var pair in next.Query.TrimStart('?').Split('&'))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] is "id_before" or "id_after" && long.TryParse(Uri.UnescapeDataString(kv[1]), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    return id.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        return null;
    }

    private TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var now = _time.GetUtcNow();
        if (response.Headers.RetryAfter is { } retry)
        {
            if (retry.Delta is { } delta)
            {
                return delta;
            }

            if (retry.Date is { } date)
            {
                var wait = date - now;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }

        return response.Headers.TryGetValues("RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch) - now is var w && w > TimeSpan.Zero ? w : TimeSpan.Zero
                : null;
    }

    private static GitLabProject ToProject(JsonElement p) => new(
        Long(p, "id") ?? throw new GitLabApiException(200, "GitLab's project has no id."),
        Str(p, "name") ?? string.Empty,
        Str(p, "path") ?? string.Empty,
        Str(p, "path_with_namespace") ?? string.Empty,
        Str(p, "default_branch"),
        Str(p, "web_url"),
        p.TryGetProperty("namespace", out var ns) && ns.ValueKind == JsonValueKind.Object ? Str(ns, "full_path") : null,
        Str(p, "visibility"),
        Str(p, "last_activity_at") is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null);

    private Uri Url(string relative)
    {
        var url = new Uri(BaseUrl, relative);
        return GitHostAddressRules.SameOrigin(BaseUrl, url)
            ? url
            : throw new InvalidOperationException("GitLab requests stay on the instance's origin.");
    }

    private static string Query(params (string Key, string Value)[] pairs)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    private static string? Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s ? s : null;

    private static long? Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;
}
