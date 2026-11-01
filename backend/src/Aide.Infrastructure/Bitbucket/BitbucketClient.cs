using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aide.Domain.GitHosts;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Bitbucket;

/// <summary>
/// A Bitbucket API failure. <see cref="Status"/> is <c>null</c> when Bitbucket couldn't be reached (network, timeout).
/// <see cref="UpstreamMessage"/> is Bitbucket's error text (never a token). <see cref="ScopeError"/> marks a 403 for missing
/// scopes, with the token's <see cref="GrantedScopes"/> when Bitbucket listed them.
/// </summary>
public class BitbucketApiException : Exception
{
    public BitbucketApiException()
    {
    }

    public BitbucketApiException(string message)
        : base(message)
    {
    }

    public BitbucketApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BitbucketApiException(int? status, string? upstreamMessage, Exception? innerException = null)
        : base(upstreamMessage ?? (status is { } s ? $"Bitbucket answered HTTP {s}." : "Bitbucket couldn't be reached."), innerException)
    {
        Status = status;
        UpstreamMessage = upstreamMessage;
    }

    public int? Status { get; }

    public string? UpstreamMessage { get; }

    /// <summary>The OAuth <c>error</c> code of a token endpoint failure (<c>invalid_grant</c>…), if any.</summary>
    public string? OauthError { get; init; }

    /// <summary>For 429: how long to wait (Bitbucket's <c>Retry-After</c>, or AIDE's budget refill).</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>The 429 came from AIDE's own per-connection budget (§4.19), not from Bitbucket.</summary>
    public bool LocalBudget { get; init; }

    /// <summary>A 403 for missing scopes (Bitbucket's "credentials lack … privilege scopes").</summary>
    public bool ScopeError { get; init; }

    /// <summary>The token's scopes as a scope error listed them, if it did.</summary>
    public IReadOnlyList<string>? GrantedScopes { get; init; }

    public bool IsUnauthorized => Status == 401;

    public bool IsRateLimited => Status == 429;
}

/// <summary>
/// How a request authenticates (§4.19): an OAuth access token (<c>Authorization: Bearer</c>), or an Atlassian API token with
/// its account email (<c>Authorization: Basic email:token</c>). Redacted <see cref="ToString"/>.
/// </summary>
public sealed record BitbucketAuth(string Token, string? Email)
{
    public static BitbucketAuth OAuth(string token) => new(token, null);

    public static BitbucketAuth ApiToken(string email, string token) => new(token, email);

    public AuthenticationHeaderValue Header => Email is null
        ? new AuthenticationHeaderValue("Bearer", Token)
        : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Email}:{Token}")));

    public override string ToString() => Email is null ? "BitbucketAuth { OAuth }" : "BitbucketAuth { ApiToken }";
}

/// <summary><c>GET /2.0/user</c>. <see cref="Uuid"/> (with braces, as Bitbucket sends it) is the stable identity (D-V4-18).</summary>
public sealed record BitbucketUser(string Uuid, string? Username, string? Nickname, string? DisplayName, string? AccountId, string? AvatarUrl)
{
    /// <summary>The name shown for the connection.</summary>
    public string Login => Username ?? Nickname ?? DisplayName ?? AccountId ?? Uuid;
}

/// <summary>A token endpoint response (authorization code or refresh). Redacted <see cref="ToString"/>.</summary>
public sealed record BitbucketOAuthTokens(string AccessToken, string? RefreshToken, long? ExpiresIn, IReadOnlyList<string> Scopes)
{
    public override string ToString() => $"BitbucketOAuthTokens {{ ExpiresIn = {ExpiresIn}, Scopes = {string.Join(' ', Scopes)} }}";
}

/// <summary>A workspace the user can access (<c>GET /2.0/user/workspaces</c>).</summary>
public sealed record BitbucketWorkspace(string Slug, string Name, string? Uuid, bool? IsAdmin);

/// <summary>A project in a workspace (<c>GET /2.0/workspaces/{ws}/projects</c>).</summary>
public sealed record BitbucketProject(string Key, string Name, string? Uuid, bool? IsPrivate);

/// <summary>A repository (<c>GET /2.0/repositories/{ws}</c>, <c>POST /2.0/repositories/{ws}/{slug}</c>).</summary>
public sealed record BitbucketRepository(
    string Uuid,
    string FullName,
    string Name,
    string Slug,
    string? WorkspaceSlug,
    string? ProjectKey,
    string? MainBranch,
    bool IsPrivate,
    string? WebUrl,
    DateTimeOffset? UpdatedOn);

public sealed record BitbucketPage<T>(IReadOnlyList<T> Items, bool HasNext);

public sealed record BitbucketCreateRepository(string Workspace, string Slug, string Name, string ProjectKey, string? Description, bool Private);

/// <summary>The answer to a harmless scope probe (FR-134): its status and whether a 403 was about scopes.</summary>
public sealed record BitbucketProbe(int Status, bool ScopeError, IReadOnlyList<string>? GrantedScopes);

/// <summary>A <see cref="BitbucketClient"/>, counted against a budget (§4.19): a connection's, or an AIDE user's.</summary>
public interface IBitbucketClients
{
    /// <param name="connectionId">The connection whose token the client sends (its requests take from the connection's
    /// bucket); <c>null</c> for calls nobody's budget covers (the OAuth code exchange and refresh).</param>
    BitbucketClient Create(Guid? connectionId = null);

    /// <summary>A client whose requests take from <paramref name="userId"/>'s bucket (checking a pasted API token, FR-134).</summary>
    BitbucketClient CreateForUser(Guid userId);
}

public sealed class BitbucketClients(IHttpClientFactory http, IOptionsMonitor<BitbucketOptions> options, BitbucketRateLimiter limiter, TimeProvider time) : IBitbucketClients
{
    public const string HttpClientName = "bitbucket";

    public BitbucketClient Create(Guid? connectionId = null) =>
        New(connectionId is { } id ? BitbucketRateLimiter.ConnectionKey(id) : null);

    public BitbucketClient CreateForUser(Guid userId) => New(BitbucketRateLimiter.UserKey(userId));

    private BitbucketClient New(string? limiterKey)
    {
        var client = http.CreateClient(HttpClientName);
        client.Timeout = options.CurrentValue.RequestTimeout;
        return new BitbucketClient(client, limiterKey, limiter, time);
    }
}

/// <summary>
/// FR-133–FR-135, §4.19: the Bitbucket Cloud REST API (<c>https://api.bitbucket.org/2.0</c>) and OAuth endpoints
/// (<c>https://bitbucket.org/site/oauth2/…</c>, client Basic auth). The hosts are fixed: tokens go only to the API host and the
/// consumer secret only to the OAuth host. Redirects are never followed (the handler has <c>AllowAutoRedirect = false</c>; a
/// 3xx is a failure). With a budget key, every request first takes a token from its bucket (900 per hour, §4.19), and a
/// Bitbucket 429 empties the bucket until <c>Retry-After</c> (else five minutes): either way the call fails with a 429
/// <see cref="BitbucketApiException"/>. Non-success answers throw <see cref="BitbucketApiException"/>.
/// </summary>
public sealed partial class BitbucketClient
{
    public const int ReposPerPage = BitbucketRules.ReposPerPage;

    private const int MaxWorkspacePages = 5;

    private static readonly Uri ApiBase = new(BitbucketRules.ApiBaseUrl + "/", UriKind.Absolute);
    private static readonly Uri WebBase = new(BitbucketRules.WebBaseUrl + "/", UriKind.Absolute);

    private readonly HttpClient _http;
    private readonly string? _limiterKey;
    private readonly BitbucketRateLimiter _limiter;
    private readonly TimeProvider _time;

    internal BitbucketClient(HttpClient http, string? limiterKey, BitbucketRateLimiter limiter, TimeProvider time)
    {
        _http = http;
        _limiterKey = limiterKey;
        _limiter = limiter;
        _time = time;
    }

    /// <summary>The scopes Bitbucket listed on the last answer (<c>X-OAuth-Scopes</c>), if it did.</summary>
    public IReadOnlyList<string>? LastGrantedScopes { get; private set; }

    /// <summary>FR-134: the authorize URL (authorization code; the scopes are fixed on the consumer).</summary>
    public static Uri AuthorizeUrl(string clientId, string state) =>
        new(WebBase, "site/oauth2/authorize?" + Query(("client_id", clientId), ("response_type", "code"), ("state", state)));

    /// <summary>FR-134: exchanges an authorization code (client Basic auth).</summary>
    public Task<BitbucketOAuthTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code, CancellationToken cancellationToken) =>
        TokenAsync(clientId, clientSecret, [new("grant_type", "authorization_code"), new("code", code)], cancellationToken);

    /// <summary>FR-134: refreshes an access token; AIDE stores whatever refresh token comes back.</summary>
    public Task<BitbucketOAuthTokens> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken) =>
        TokenAsync(clientId, clientSecret, [new("grant_type", "refresh_token"), new("refresh_token", refreshToken)], cancellationToken);

    /// <summary><c>GET /2.0/user</c>: the token's user.</summary>
    public async Task<BitbucketUser> GetUserAsync(BitbucketAuth auth, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync("user", auth, cancellationToken);
        var root = json.RootElement;
        var avatar = root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object
            && links.TryGetProperty("avatar", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "href") : null;
        return new BitbucketUser(
            Str(root, "uuid") ?? throw new BitbucketApiException(200, "Bitbucket's user has no uuid."),
            Str(root, "username"),
            Str(root, "nickname"),
            Str(root, "display_name"),
            Str(root, "account_id"),
            avatar);
    }

    /// <summary><c>GET /2.0/user/emails</c>: the user's addresses with whether they are primary and confirmed.</summary>
    public async Task<IReadOnlyList<BitbucketEmail>> GetEmailsAsync(BitbucketAuth auth, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync("user/emails?pagelen=100", auth, cancellationToken);
        return [.. Values(json.RootElement)
            .Where(e => Str(e, "email") is not null)
            .Select(e => new BitbucketEmail(Str(e, "email")!, Bool(e, "is_primary") == true, Bool(e, "is_confirmed") == true))];
    }

    /// <summary>FR-135: the workspaces the user can access (<c>GET /2.0/user/workspaces</c>, up to 500).</summary>
    public async Task<IReadOnlyList<BitbucketWorkspace>> ListWorkspacesAsync(BitbucketAuth auth, CancellationToken cancellationToken)
    {
        var workspaces = new List<BitbucketWorkspace>();
        for (var page = 1; page <= MaxWorkspacePages; page++)
        {
            var result = await ListWorkspacesPageAsync(auth, 100, page, cancellationToken);
            workspaces.AddRange(result.Items);
            if (!result.HasNext)
            {
                break;
            }
        }

        return workspaces;
    }

    /// <summary>FR-135: one page of the user's workspaces (<c>GET /2.0/user/workspaces</c>, <paramref name="pageLength"/> per page).</summary>
    public async Task<BitbucketPage<BitbucketWorkspace>> ListWorkspacesPageAsync(BitbucketAuth auth, int pageLength, int page, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(
            $"user/workspaces?pagelen={pageLength.ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}", auth, cancellationToken);
        var workspaces = new List<BitbucketWorkspace>();
        foreach (var access in Values(json.RootElement))
        {
            // user/workspaces answers workspace_access objects ({administrator, workspace: {…}}).
            var ws = access.TryGetProperty("workspace", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : access;
            if (Str(ws, "slug") is { } slug)
            {
                workspaces.Add(new BitbucketWorkspace(slug, Str(ws, "name") ?? slug, Str(ws, "uuid"), Bool(access, "administrator")));
            }
        }

        return new BitbucketPage<BitbucketWorkspace>(workspaces, HasNext(json.RootElement));
    }

    /// <summary>
    /// FR-135: one page of the workspace's repositories the user can write to
    /// (<c>GET /2.0/repositories/{ws}?role=contributor&amp;q=…&amp;sort=-updated_on</c>), 30 per page.
    /// </summary>
    public Task<BitbucketPage<BitbucketRepository>> ListRepositoriesAsync(BitbucketAuth auth, string workspace, string? query, int page, CancellationToken cancellationToken) =>
        ListRepositoriesAsync(auth, workspace, query, page, "contributor", cancellationToken);

    /// <summary>One page of the workspace's repositories where the user has <paramref name="role"/> (<c>member</c>, <c>contributor</c>, <c>admin</c>).</summary>
    public async Task<BitbucketPage<BitbucketRepository>> ListRepositoriesAsync(
        BitbucketAuth auth, string workspace, string? query, int page, string role, CancellationToken cancellationToken)
    {
        var parameters = new List<(string, string)>
        {
            ("role", role),
            ("sort", "-updated_on"),
            ("pagelen", ReposPerPage.ToString(CultureInfo.InvariantCulture)),
            ("page", page.ToString(CultureInfo.InvariantCulture)),
        };
        if (query is not null)
        {
            parameters.Add(("q", query));
        }

        using var json = await GetJsonAsync($"repositories/{Segment(workspace)}?{Query([.. parameters])}", auth, cancellationToken);
        return new BitbucketPage<BitbucketRepository>([.. Values(json.RootElement).Select(ToRepository).OfType<BitbucketRepository>()], HasNext(json.RootElement));
    }

    /// <summary>FR-135: the first page of the workspace's projects (<c>GET /2.0/workspaces/{ws}/projects</c>, 100).</summary>
    public async Task<BitbucketPage<BitbucketProject>> ListProjectsPageAsync(BitbucketAuth auth, string workspace, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync($"workspaces/{Segment(workspace)}/projects?pagelen=100", auth, cancellationToken);
        var projects = new List<BitbucketProject>();
        foreach (var p in Values(json.RootElement))
        {
            if (Str(p, "key") is { } key)
            {
                projects.Add(new BitbucketProject(key, Str(p, "name") ?? key, Str(p, "uuid"), Bool(p, "is_private")));
            }
        }

        return new BitbucketPage<BitbucketProject>(projects, HasNext(json.RootElement));
    }

    /// <summary>
    /// FR-135: <c>POST /2.0/repositories/{ws}/{slug} {scm: "git", is_private, project: {key}, name, description}</c>. The
    /// repository is empty (Bitbucket has no auto-init); AIDE pushes the README itself.
    /// </summary>
    public async Task<BitbucketRepository> CreateRepositoryAsync(BitbucketAuth auth, BitbucketCreateRepository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var body = new Dictionary<string, object?>
        {
            ["scm"] = "git",
            ["name"] = repository.Name,
            ["is_private"] = repository.Private,
            ["project"] = new Dictionary<string, string> { ["key"] = repository.ProjectKey },
        };
        if (!string.IsNullOrEmpty(repository.Description))
        {
            body["description"] = repository.Description;
        }

        using var request = Authorized(HttpMethod.Post, $"repositories/{Segment(repository.Workspace)}/{Segment(repository.Slug)}", auth);
        request.Content = JsonContent.Create(body);
        using var response = await SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        return ToRepository(json.RootElement) ?? throw new BitbucketApiException(200, "Bitbucket's repository has no uuid.");
    }

    /// <summary>
    /// FR-134: a harmless call to check one scope: <paramref name="method"/> on <paramref name="relative"/> (with an empty
    /// JSON object for writes, which Bitbucket refuses with a validation error when the scope is there). 2xx–4xx answers are
    /// returned; 401, 429, 5xx and network failures throw <see cref="BitbucketApiException"/>.
    /// </summary>
    public Task<BitbucketProbe> ProbeAsync(BitbucketAuth auth, HttpMethod method, string relative, CancellationToken cancellationToken) =>
        ProbeAsync(auth, method, relative, "{}", cancellationToken);

    /// <summary>As <see cref="ProbeAsync(BitbucketAuth, HttpMethod, string, CancellationToken)"/>, with <paramref name="jsonBody"/> for a write.</summary>
    public async Task<BitbucketProbe> ProbeAsync(BitbucketAuth auth, HttpMethod method, string relative, string jsonBody, CancellationToken cancellationToken)
    {
        using var request = Authorized(method, relative, auth);
        if (method != HttpMethod.Get)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = await SendRawAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        if (status is 401 or 429 or >= 500 or (>= 300 and < 400))
        {
            throw await FailureAsync(response, cancellationToken);
        }

        if (status == 403)
        {
            using var json = await TryJsonAsync(response, cancellationToken);
            var (scopeError, granted) = ScopeErrorOf(json?.RootElement);
            return new BitbucketProbe(status, scopeError, granted);
        }

        return new BitbucketProbe(status, false, null);
    }

    private async Task<BitbucketOAuthTokens> TokenAsync(string clientId, string clientSecret, IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(WebBase, "site/oauth2/access_token")) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await SendRawAsync(request, cancellationToken);
        using var json = await TryJsonAsync(response, cancellationToken);
        var root = json?.RootElement;
        if (!response.IsSuccessStatusCode || root is not { ValueKind: JsonValueKind.Object } body || Str(body, "access_token") is not { } access)
        {
            var error = root is { ValueKind: JsonValueKind.Object } r ? Str(r, "error") : null;
            var description = root is { ValueKind: JsonValueKind.Object } d ? Str(d, "error_description") : null;
            throw new BitbucketApiException((int)response.StatusCode, description ?? error ?? "The Bitbucket token endpoint refused the request.")
            {
                OauthError = error,
                RetryAfter = RetryAfterOf(response),
            };
        }

        return new BitbucketOAuthTokens(access, Str(body, "refresh_token"), Long(body, "expires_in"), BitbucketRules.ParseScopes(Str(body, "scopes") ?? Str(body, "scope")));
    }

    private async Task<JsonDocument> GetJsonAsync(string relative, BitbucketAuth auth, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, relative, auth);
        using var response = await SendAsync(request, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    /// <summary>§8: the token goes only to the API host.</summary>
    private static HttpRequestMessage Authorized(HttpMethod method, string relative, BitbucketAuth auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentException.ThrowIfNullOrEmpty(auth.Token);
        var url = new Uri(ApiBase, relative);
        if (!string.Equals(url.GetLeftPart(UriPartial.Authority), ApiBase.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            || !url.AbsolutePath.StartsWith(ApiBase.AbsolutePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A Bitbucket token may only be sent to the Bitbucket API.");
        }

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = auth.Header;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <summary>Sends and throws <see cref="BitbucketApiException"/> for any non-2xx answer (including redirects).</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await SendRawAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await FailureAsync(response, cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_limiterKey is { } key && _limiter.TryAcquire(key) is { } wait)
        {
            // §4.19: the budget is spent; nothing is sent.
            throw new BitbucketApiException(429, "AIDE's Bitbucket request budget is used up for now.")
            {
                RetryAfter = wait,
                LocalBudget = true,
            };
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new BitbucketApiException(null, null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BitbucketApiException(null, "Bitbucket didn't answer in time.", ex);
        }

        LastGrantedScopes = response.Headers.TryGetValues("X-OAuth-Scopes", out var scopes) ? BitbucketRules.ParseScopes(string.Join(',', scopes)) : null;
        if ((int)response.StatusCode == 429 && _limiterKey is { } limited)
        {
            _limiter.OnRateLimited(limited, Header(response, "Retry-After"));
        }

        return response;
    }

    private async Task<BitbucketApiException> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var json = await TryJsonAsync(response, cancellationToken);
        var status = (int)response.StatusCode;
        var message = status is >= 300 and < 400
            ? "Bitbucket answered with a redirect, which AIDE doesn't follow."
            : json is null ? null : ErrorText(json.RootElement);
        var (scopeError, granted) = status == 403 ? ScopeErrorOf(json?.RootElement) : (false, null);
        return new BitbucketApiException(status, message)
        {
            RetryAfter = status == 429 ? RetryAfterOf(response) ?? Domain.GitHosts.GitHostPollBudget.BitbucketRateLimitWait : RetryAfterOf(response),
            ScopeError = scopeError,
            GrantedScopes = granted,
        };
    }

    /// <summary>Bitbucket's <c>error.message</c> (plus <c>error.detail</c> when it is text, and field errors), or OAuth's.</summary>
    public static string? ErrorText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var parts = new List<string>();
            if (Str(error, "message") is { } message)
            {
                parts.Add(message);
            }

            if (Str(error, "detail") is { } detail)
            {
                parts.Add(detail);
            }

            if (error.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in fields.EnumerateObject())
                {
                    var texts = field.Value.ValueKind == JsonValueKind.Array
                        ? field.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString())
                        : field.Value.ValueKind == JsonValueKind.String ? [field.Value.GetString()] : [];
                    parts.AddRange(texts.Select(t => $"{field.Name}: {t}"));
                }
            }

            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        return Str(root, "error_description") ?? Str(root, "error");
    }

    /// <summary>
    /// A 403 about scopes: Bitbucket says the credentials "lack … privilege scopes" and may list <c>detail.required</c> and
    /// <c>detail.granted</c>.
    /// </summary>
    private static (bool ScopeError, IReadOnlyList<string>? Granted) ScopeErrorOf(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } body || !body.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return (false, null);
        }

        IReadOnlyList<string>? granted = null;
        var hasRequired = false;
        if (error.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
        {
            hasRequired = detail.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array;
            if (detail.TryGetProperty("granted", out var g) && g.ValueKind == JsonValueKind.Array)
            {
                granted = [.. g.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)];
            }
        }

        var scopeError = hasRequired || (Str(error, "message")?.Contains("scope", StringComparison.OrdinalIgnoreCase) ?? false);
        return (scopeError, scopeError ? granted : null);
    }

    private TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retry)
        {
            return null;
        }

        if (retry.Delta is { } delta)
        {
            return delta;
        }

        if (retry.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await TryJsonAsync(response, cancellationToken) ?? throw new BitbucketApiException((int)response.StatusCode, "Bitbucket's answer isn't JSON.");

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

    private static List<JsonElement> Values(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array
            ? [.. values.EnumerateArray()]
            : [];

    /// <summary>Bitbucket pages carry a <c>next</c> link while more follow (AIDE asks for the next page number itself).</summary>
    private static bool HasNext(JsonElement root) => root.ValueKind == JsonValueKind.Object && Str(root, "next") is not null;

    private static BitbucketRepository? ToRepository(JsonElement r)
    {
        if (Str(r, "uuid") is not { } uuid)
        {
            return null;
        }

        var fullName = Str(r, "full_name") ?? string.Empty;
        var slug = Str(r, "slug") ?? fullName.Split('/').LastOrDefault() ?? string.Empty;
        var workspace = r.TryGetProperty("workspace", out var ws) && ws.ValueKind == JsonValueKind.Object ? Str(ws, "slug") : fullName.Split('/').FirstOrDefault();
        var project = r.TryGetProperty("project", out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, "key") : null;
        var main = r.TryGetProperty("mainbranch", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "name") : null;
        var web = r.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object
            && links.TryGetProperty("html", out var html) && html.ValueKind == JsonValueKind.Object ? Str(html, "href") : null;
        return new BitbucketRepository(
            uuid,
            fullName,
            Str(r, "name") ?? slug,
            slug,
            workspace,
            project,
            main,
            Bool(r, "is_private") ?? true,
            web,
            Str(r, "updated_on") is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToUniversalTime()
                : null);
    }

    /// <summary>One escaped path segment (workspace, slug).</summary>
    private static string Segment(string value) => Uri.EscapeDataString(value.Trim());

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
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s ? s : null;

    private static long? Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;

    private static bool? Bool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;
}
