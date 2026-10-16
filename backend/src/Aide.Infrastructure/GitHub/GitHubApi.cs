using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Infrastructure.Git;

namespace Aide.Infrastructure.GitHub;

/// <summary>
/// §4.14: GitHub's rate limit for the token in use is exhausted until <see cref="ResetAt"/>. Request paths answer
/// <c>GITHUB_RATE_LIMITED</c> (429 with <c>Retry-After</c>); the job worker re-queues the job for the reset without counting
/// the attempt as a failure (NFR-REL).
/// </summary>
public sealed class GitHubRateLimitedException : Exception
{
    public GitHubRateLimitedException()
        : this(DateTimeOffset.UtcNow.AddMinutes(1))
    {
    }

    public GitHubRateLimitedException(string message)
        : base(message)
    {
        ResetAt = DateTimeOffset.UtcNow.AddMinutes(1);
    }

    public GitHubRateLimitedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ResetAt = DateTimeOffset.UtcNow.AddMinutes(1);
    }

    public GitHubRateLimitedException(DateTimeOffset resetAt)
        : base($"GitHub's API rate limit is exhausted until {resetAt:u}.")
    {
        ResetAt = resetAt;
    }

    public DateTimeOffset ResetAt { get; }
}

/// <summary>How a GitHub request failed (other than a rate limit).</summary>
public enum GitHubFailureKind
{
    /// <summary>GitHub couldn't be reached, timed out, answered 5xx or something unreadable.</summary>
    Upstream = 0,
    Unauthorized = 1,
    Forbidden = 2,
    NotFound = 3,
    Conflict = 4,
    Unprocessable = 5,
    Other = 6,
}

/// <summary>A failed GitHub request: its kind, HTTP status and GitHub's message (never a token).</summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException()
    {
    }

    public GitHubApiException(string message)
        : base(message)
    {
    }

    public GitHubApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GitHubApiException(GitHubFailureKind kind, int? status, string message)
        : base(message)
    {
        Kind = kind;
        Status = status;
    }

    public GitHubFailureKind Kind { get; }

    public int? Status { get; }
}

public sealed record GitHubUser(long Id, string Login, GitHubOwnerType Type);

/// <summary>A repository; <see cref="CanPush"/> is the token's <c>permissions</c> (push, maintain or admin), <c>false</c>
/// when GitHub didn't say (e.g. an installation token's view).</summary>
public sealed record GitHubRepository(
    long Id, string FullName, string Owner, GitHubOwnerType OwnerType, string Name, bool Private, string DefaultBranch, long Size, string HtmlUrl, bool CanPush = false);

public sealed record GitHubInstallation(
    long Id, string AccountLogin, GitHubOwnerType AccountType, GitHubRepositorySelection RepositorySelection, IReadOnlyDictionary<string, string> Permissions);

/// <summary>The App as GitHub describes it (<c>GET /app</c>, or a manifest conversion without its secrets).</summary>
public sealed record GitHubAppInfo(long Id, string Slug, string? OwnerLogin, GitHubOwnerType OwnerType, string? HtmlUrl, IReadOnlyDictionary<string, string> Permissions);

/// <summary>FR-80: the App created by the manifest flow, with its secrets (stored encrypted, never returned).</summary>
public sealed record GitHubManifestConversion(GitHubAppInfo App, string ClientId, string ClientSecret, string PrivateKeyPem)
{
    /// <summary>Redacted: the client secret and private key never reach a log.</summary>
    public override string ToString() => $"GitHubManifestConversion {{ App = {App.Id} }}";
}

/// <summary>An installation token (FR-82) and when GitHub expires it.</summary>
public sealed record GitHubInstallationToken(string Token, DateTimeOffset? ExpiresAt, IReadOnlyDictionary<string, string> Permissions)
{
    /// <summary>Redacted: the token never reaches a log.</summary>
    public override string ToString() => $"GitHubInstallationToken {{ ExpiresAt = {ExpiresAt:u} }}";
}

/// <summary>A user-to-server token pair (FR-81); expiries are <c>null</c> when the App's tokens don't expire.</summary>
public sealed record GitHubUserTokens(string AccessToken, DateTimeOffset? AccessExpiresAt, string? RefreshToken, DateTimeOffset? RefreshExpiresAt)
{
    /// <summary>Redacted: tokens never reach a log.</summary>
    public override string ToString() => $"GitHubUserTokens {{ AccessExpiresAt = {AccessExpiresAt:u} }}";

    /// <summary>Reads GitHub's token response (<c>access_token</c>, <c>expires_in</c>, <c>refresh_token</c>,
    /// <c>refresh_token_expires_in</c>); <c>null</c> when it has no access token (e.g. an <c>error</c> body).</summary>
    public static GitHubUserTokens? FromTokenResponse(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object || GitHubApi.Text(root, "access_token") is not { Length: > 0 } access)
        {
            return null;
        }

        DateTimeOffset? After(string property) =>
            root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds) && seconds > 0
                ? now + TimeSpan.FromSeconds(seconds)
                : null;

        return new GitHubUserTokens(access, After("expires_in"), GitHubApi.Text(root, "refresh_token") is { Length: > 0 } refresh ? refresh : null, After("refresh_token_expires_in"));
    }
}

/// <summary>
/// The GitHub REST API (FR-80–FR-83, §4.14) through the <c>github</c> HttpClient (<c>GitHub:ApiBaseUrl</c>, default
/// https://api.github.com/) and OAuth on https://github.com/ (<see cref="WebHttpClientName"/>). Hosts are system
/// configuration only, never user-supplied (§8, no SSRF). Tokens and JWTs travel only in the Authorization header and
/// never appear in exceptions or logs. Rate limits throw <see cref="GitHubRateLimitedException"/>; other failures
/// <see cref="GitHubApiException"/>.
/// </summary>
public sealed class GitHubApi(IHttpClientFactory clients, TimeProvider time)
{
    /// <summary>The HttpClient for https://github.com/ (OAuth token refresh).</summary>
    public const string WebHttpClientName = "github-web";

    public const string WebBaseUrl = "https://github.com/";

    private const int MaxMessageLength = 300;

    /// <summary>Repository permissions that allow pushing (FR-82, FR-83).</summary>
    private static readonly string[] PushLevels = ["push", "maintain", "admin"];

    // ---------------------------------------------------------------- App (JWT)

    /// <summary><c>GET /app</c>: the App itself (slug, owner, permissions).</summary>
    public async Task<GitHubAppInfo> GetAppAsync(string jwt, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Get, "app", jwt, null, cancellationToken);
        return ReadApp(json.RootElement);
    }

    /// <summary>FR-80: <c>POST /app-manifests/{code}/conversions</c> (no authentication: the code is single-use).</summary>
    public async Task<GitHubManifestConversion> ConvertManifestAsync(string code, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Post, $"app-manifests/{Uri.EscapeDataString(code)}/conversions", null, null, cancellationToken);
        var root = json.RootElement;
        var clientId = Text(root, "client_id");
        var clientSecret = Text(root, "client_secret");
        var pem = Text(root, "pem");
        if (clientId is null || clientSecret is null || pem is null)
        {
            throw new GitHubApiException(GitHubFailureKind.Upstream, null, "GitHub's manifest conversion had no client id, client secret or private key.");
        }

        return new GitHubManifestConversion(ReadApp(root), clientId, clientSecret, pem);
    }

    /// <summary><c>GET /repos/{owner}/{repo}/installation</c>: the App's installation for a repository, or <c>null</c> (404).</summary>
    public async Task<long?> FindRepoInstallationAsync(string jwt, string owner, string repo, CancellationToken cancellationToken)
    {
        using var json = await GetOrNullAsync($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/installation", jwt, cancellationToken);
        return json is null ? null : Long(json.RootElement, "id");
    }

    /// <summary>
    /// FR-82, §8: <c>POST /app/installations/{id}/access_tokens</c>, scoped to <paramref name="repositoryIds"/> when given.
    /// </summary>
    public async Task<GitHubInstallationToken> CreateInstallationTokenAsync(
        string jwt, long installationId, IReadOnlyList<long>? repositoryIds, CancellationToken cancellationToken)
    {
        object? body = repositoryIds is { Count: > 0 } ids ? new { repository_ids = ids } : null;
        using var json = await SendAsync(
            HttpMethod.Post, $"app/installations/{installationId.ToString(CultureInfo.InvariantCulture)}/access_tokens", jwt, body, cancellationToken);
        var root = json.RootElement;
        var token = Text(root, "token") ?? throw new GitHubApiException(GitHubFailureKind.Upstream, null, "GitHub returned no installation token.");
        DateTimeOffset? expires = Text(root, "expires_at") is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        return new GitHubInstallationToken(token, expires, Permissions(root));
    }

    /// <summary><c>GET /users/{login}</c> (unauthenticated public profile), or <c>null</c>.</summary>
    public async Task<GitHubUser?> FindUserAsync(string login, CancellationToken cancellationToken)
    {
        using var json = await GetOrNullAsync($"users/{Uri.EscapeDataString(login)}", null, cancellationToken);
        return json is null ? null : ReadUser(json.RootElement);
    }

    // ---------------------------------------------------------------- user token

    /// <summary><c>GET /user</c>.</summary>
    public async Task<GitHubUser> GetAuthenticatedUserAsync(string userToken, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Get, "user", userToken, null, cancellationToken);
        return ReadUser(json.RootElement);
    }

    /// <summary>FR-81: <c>GET /user/installations</c>: the App's installations the user can access (first 100).</summary>
    public async Task<IReadOnlyList<GitHubInstallation>> ListUserInstallationsAsync(string userToken, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Get, "user/installations?per_page=100", userToken, null, cancellationToken);
        return json.RootElement.TryGetProperty("installations", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(ReadInstallation).OfType<GitHubInstallation>()]
            : [];
    }

    /// <summary>FR-81: <c>GET /user/installations/{id}/repositories</c>, one page (1-based) and the total count.</summary>
    public async Task<(IReadOnlyList<GitHubRepository> Repositories, int TotalCount)> ListInstallationRepositoriesAsync(
        string userToken, long installationId, int page, int perPage, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(
            HttpMethod.Get,
            $"user/installations/{installationId.ToString(CultureInfo.InvariantCulture)}/repositories?per_page={perPage.ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}",
            userToken,
            null,
            cancellationToken);
        var root = json.RootElement;
        var total = root.TryGetProperty("total_count", out var count) && count.TryGetInt32(out var n) ? n : 0;
        IReadOnlyList<GitHubRepository> repositories = root.TryGetProperty("repositories", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(ReadRepository).OfType<GitHubRepository>()]
            : [];
        return (repositories, total);
    }

    /// <summary><c>GET /user/orgs</c>: the organisations the user belongs to (logins; first 100).</summary>
    public async Task<IReadOnlyList<string>> ListUserOrganizationsAsync(string userToken, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Get, "user/orgs?per_page=100", userToken, null, cancellationToken);
        return json.RootElement.ValueKind == JsonValueKind.Array
            ? [.. json.RootElement.EnumerateArray().Select(o => Text(o, "login")).OfType<string>()]
            : [];
    }

    /// <summary><c>GET /repos/{owner}/{repo}</c>, or <c>null</c> when it doesn't exist or the token can't see it.</summary>
    public async Task<GitHubRepository?> FindRepositoryAsync(string token, string owner, string repo, CancellationToken cancellationToken)
    {
        using var json = await GetOrNullAsync($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}", token, cancellationToken);
        return json is null ? null : ReadRepository(json.RootElement);
    }

    /// <summary>
    /// FR-86: the head commit of <paramref name="branch"/> and the names at the root of its tree; <c>null</c> when the
    /// repository has no commits (or no such branch).
    /// </summary>
    public async Task<(string HeadSha, IReadOnlyList<string> RootEntries)?> ReadBranchRootAsync(
        string token, string owner, string repo, string branch, CancellationToken cancellationToken)
    {
        var path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
        string? head;
        using (var json = await GetOrNullAsync($"{path}/branches/{Uri.EscapeDataString(branch)}", token, cancellationToken, emptyIsMissing: true))
        {
            if (json is null)
            {
                return null;
            }

            head = json.RootElement.TryGetProperty("commit", out var commit) ? Text(commit, "sha") : null;
        }

        if (head is null)
        {
            return null;
        }

        using var tree = await GetOrNullAsync($"{path}/git/trees/{Uri.EscapeDataString(head)}", token, cancellationToken, emptyIsMissing: true);
        IReadOnlyList<string> entries = tree?.RootElement.TryGetProperty("tree", out var items) == true && items.ValueKind == JsonValueKind.Array
            ? [.. items.EnumerateArray().Select(e => Text(e, "path")).OfType<string>()]
            : [];
        return (head, entries);
    }

    /// <summary>Pages (100 items each) read from list endpoints before stopping: 1,000 releases or 2,000 tags at most.</summary>
    public const int MaxReleasePages = 10;

    public const int MaxTagPages = 20;

    /// <summary>FR-73: the repository's releases (every page, up to <see cref="MaxReleasePages"/>) and the tag of GitHub's latest release (<c>null</c>: none).</summary>
    public async Task<(IReadOnlyList<(string TagName, string? Name, DateTimeOffset? PublishedAt, bool Draft)> Releases, string? LatestTag)> ListReleasesAsync(
        string token, string owner, string repo, CancellationToken cancellationToken)
    {
        var path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases";
        List<(string, string?, DateTimeOffset?, bool)> releases = [];
        for (var page = 1; page <= MaxReleasePages; page++)
        {
            using var json = await SendAsync(HttpMethod.Get, $"{path}?per_page=100&page={page}", token, null, cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var release in json.RootElement.EnumerateArray())
            {
                if (Text(release, "tag_name") is { } tag)
                {
                    DateTimeOffset? published = Text(release, "published_at") is { } text
                        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
                    releases.Add((tag, Text(release, "name"), published, release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True));
                }
            }

            if (json.RootElement.GetArrayLength() < 100)
            {
                break;
            }
        }

        string? latest = null;
        if (releases.Count > 0)
        {
            using var json = await GetOrNullAsync($"{path}/latest", token, cancellationToken);
            latest = json is null ? null : Text(json.RootElement, "tag_name");
        }

        return (releases, latest);
    }

    /// <summary>FR-73: the repository's tags with their commits (every page, up to <see cref="MaxTagPages"/>).</summary>
    public async Task<IReadOnlyList<(string Name, string Sha)>> ListTagsAsync(string token, string owner, string repo, CancellationToken cancellationToken)
    {
        var tags = new List<(string, string)>();
        for (var page = 1; page <= MaxTagPages; page++)
        {
            using var json = await GetOrNullAsync(
                $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/tags?per_page=100&page={page}", token, cancellationToken, emptyIsMissing: true);
            if (json?.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            tags.AddRange(json.RootElement.EnumerateArray()
                .Select(t => (Name: Text(t, "name"), Sha: t.TryGetProperty("commit", out var c) ? Text(c, "sha") : null))
                .Where(t => t.Name is not null && t.Sha is not null)
                .Select(t => (t.Name!, t.Sha!)));
            if (json.RootElement.GetArrayLength() < 100)
            {
                break;
            }
        }

        return tags;
    }

    /// <summary>
    /// FR-73: the commit the tag <paramref name="tag"/> points at (<c>GET /repos/{o}/{r}/git/ref/tags/{tag}</c>, peeled through
    /// an annotated tag object), or <c>null</c> when the repository has no such tag. An exact lookup: not limited by paging.
    /// </summary>
    public async Task<string?> FindTagCommitAsync(string token, string owner, string repo, string tag, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        var path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/git";
        var refPath = string.Join('/', tag.Split('/').Select(Uri.EscapeDataString));
        string? type;
        string? sha;
        using (var json = await GetOrNullAsync($"{path}/ref/tags/{refPath}", token, cancellationToken, emptyIsMissing: true))
        {
            // Only an object naming exactly this ref is the tag (older API versions answered prefix matches with an array).
            if (json is null || json.RootElement.ValueKind != JsonValueKind.Object || Text(json.RootElement, "ref") != $"refs/tags/{tag}"
                || !json.RootElement.TryGetProperty("object", out var target))
            {
                return null;
            }

            type = Text(target, "type");
            sha = Text(target, "sha");
        }

        // An annotated tag points at a tag object; follow it (a tag of a tag is followed a few times at most).
        for (var hop = 0; hop < 4 && type == "tag" && sha is not null; hop++)
        {
            using var tagObject = await GetOrNullAsync($"{path}/tags/{Uri.EscapeDataString(sha)}", token, cancellationToken);
            if (tagObject is null || !tagObject.RootElement.TryGetProperty("object", out var next))
            {
                return null;
            }

            type = Text(next, "type");
            sha = Text(next, "sha");
        }

        return type == "commit" ? sha : null;
    }

    /// <summary>
    /// FR-81, §4.14: creates a repository for the user (<c>POST /user/repos</c>) or an organisation
    /// (<c>POST /orgs/{org}/repos</c>) with the user token, initialised with a README (<c>auto_init</c>).
    /// </summary>
    public async Task<GitHubRepository> CreateRepositoryAsync(
        string userToken, string? organization, string name, string? description, bool isPrivate, CancellationToken cancellationToken)
    {
        var path = organization is null ? "user/repos" : $"orgs/{Uri.EscapeDataString(organization)}/repos";
        using var json = await SendAsync(
            HttpMethod.Post, path, userToken, new { name, description, @private = isPrivate, auto_init = true }, cancellationToken);
        return ReadRepository(json.RootElement) ?? throw new GitHubApiException(GitHubFailureKind.Upstream, null, "GitHub returned an unreadable repository.");
    }

    /// <summary>FR-81: refreshes a user token (<c>POST https://github.com/login/oauth/access_token</c>, grant <c>refresh_token</c>);
    /// <c>null</c> when GitHub refuses it (e.g. the refresh token expired or was revoked).</summary>
    public async Task<GitHubUserTokens?> RefreshUserTokenAsync(string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(WebHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("login/oauth/access_token", UriKind.Relative))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await SendRawAsync(client, request, cancellationToken);
        if ((int)response.StatusCode >= 500)
        {
            throw new GitHubApiException(GitHubFailureKind.Upstream, (int)response.StatusCode, $"GitHub answered HTTP {(int)response.StatusCode}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var json = await ReadJsonAsync(response, cancellationToken);
        return GitHubUserTokens.FromTokenResponse(json.RootElement, time.GetUtcNow());
    }

    // ---------------------------------------------------------------- transport

    /// <summary>A GET whose 404 (and, with <paramref name="emptyIsMissing"/>, 409 "repository is empty") is <c>null</c>.</summary>
    public async Task<JsonDocument?> GetOrNullAsync(string path, string? bearer, CancellationToken cancellationToken, bool emptyIsMissing = false)
    {
        try
        {
            return await SendAsync(HttpMethod.Get, path, bearer, null, cancellationToken);
        }
        catch (GitHubApiException ex) when (ex.Kind == GitHubFailureKind.NotFound || (emptyIsMissing && ex.Kind == GitHubFailureKind.Conflict))
        {
            return null;
        }
    }

    /// <summary>Sends a request to the REST API and returns its JSON body (an empty object for 204).</summary>
    public async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? bearer, object? body, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(GitHubGitProvider.HttpClientName);
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (!string.IsNullOrEmpty(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await SendRawAsync(client, request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response.StatusCode == HttpStatusCode.NoContent
                ? JsonDocument.Parse("{}")
                : await ReadJsonAsync(response, cancellationToken);
        }

        var status = (int)response.StatusCode;
        var message = await MessageAsync(response, cancellationToken);
        var kind = status switch
        {
            401 => GitHubFailureKind.Unauthorized,
            403 => GitHubFailureKind.Forbidden,
            404 => GitHubFailureKind.NotFound,
            409 => GitHubFailureKind.Conflict,
            422 => GitHubFailureKind.Unprocessable,
            >= 500 => GitHubFailureKind.Upstream,
            _ => GitHubFailureKind.Other,
        };
        throw new GitHubApiException(kind, status, $"GitHub: {message} (HTTP {status})");
    }

    /// <summary>Sends, maps transport failures to <see cref="GitHubApiException"/> and rate limits to
    /// <see cref="GitHubRateLimitedException"/>.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new GitHubApiException(GitHubFailureKind.Upstream, null, "Could not reach GitHub.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException(GitHubFailureKind.Upstream, null, "Could not reach GitHub: the request timed out.");
        }

        if (RateLimitResetAt(response, time.GetUtcNow()) is { } resetAt)
        {
            response.Dispose();
            throw new GitHubRateLimitedException(resetAt);
        }

        return response;
    }

    /// <summary>§4.14: the reset time when <paramref name="response"/> is a GitHub rate limit, else <c>null</c>.</summary>
    public static DateTimeOffset? RateLimitResetAt(HttpResponseMessage response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);
        static string? Header(HttpResponseMessage r, string name) =>
            r.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        var retryAfter = response.Headers.RetryAfter?.Delta is { } delta
            ? ((long)Math.Ceiling(delta.TotalSeconds)).ToString(CultureInfo.InvariantCulture)
            : response.Headers.RetryAfter?.Date is { } date
                ? ((long)Math.Max(0, Math.Ceiling((date - now).TotalSeconds))).ToString(CultureInfo.InvariantCulture)
                : null;
        return GitHubRules.RateLimitResetAt((int)response.StatusCode, Header(response, "x-ratelimit-remaining"), Header(response, "x-ratelimit-reset"), retryAfter, now);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new GitHubApiException(GitHubFailureKind.Upstream, (int)response.StatusCode, "GitHub returned an unreadable reply.");
        }
    }

    private static async Task<string> MessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(text);
            var message = Text(json.RootElement, "message");

            // 422 details (e.g. "name already exists on this account") are in errors[].message.
            if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var details = errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object ? Text(e, "message") : null).OfType<string>().ToList();
                if (details.Count > 0)
                {
                    message = $"{message}: {string.Join("; ", details)}";
                }
            }

            return Clean(message ?? response.ReasonPhrase ?? "request failed");
        }
        catch (JsonException)
        {
            return Clean(response.ReasonPhrase ?? "request failed");
        }
    }

    internal static string Clean(string? text)
    {
        var cleaned = new string((text ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > MaxMessageLength ? cleaned[..MaxMessageLength] : cleaned;
    }

    // ---------------------------------------------------------------- JSON

    internal static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Long(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;

    private static Dictionary<string, string> Permissions(JsonElement element) =>
        element.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Object
            ? permissions.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    private static GitHubAppInfo ReadApp(JsonElement root)
    {
        var owner = root.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
        return new GitHubAppInfo(
            Long(root, "id") ?? throw new GitHubApiException(GitHubFailureKind.Upstream, null, "GitHub returned an App without an id."),
            Text(root, "slug") ?? throw new GitHubApiException(GitHubFailureKind.Upstream, null, "GitHub returned an App without a slug."),
            owner.ValueKind == JsonValueKind.Object ? Text(owner, "login") : null,
            GitHubRules.OwnerTypeOf(owner.ValueKind == JsonValueKind.Object ? Text(owner, "type") : null),
            Text(root, "html_url"),
            Permissions(root));
    }

    private static GitHubUser ReadUser(JsonElement root) =>
        new(
            Long(root, "id") ?? throw new GitHubApiException(GitHubFailureKind.Upstream, null, "GitHub returned a user without an id."),
            Text(root, "login") ?? string.Empty,
            GitHubRules.OwnerTypeOf(Text(root, "type")));

    private static GitHubInstallation? ReadInstallation(JsonElement root)
    {
        if (Long(root, "id") is not { } id || !root.TryGetProperty("account", out var account) || Text(account, "login") is not { } login)
        {
            return null;
        }

        var selection = Text(root, "repository_selection") == "all" ? GitHubRepositorySelection.All : GitHubRepositorySelection.Selected;
        return new GitHubInstallation(id, login, GitHubRules.OwnerTypeOf(Text(account, "type")), selection, Permissions(root));
    }

    private static GitHubRepository? ReadRepository(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || Long(root, "id") is not { } id || Text(root, "full_name") is not { } fullName
            || !root.TryGetProperty("owner", out var owner) || Text(owner, "login") is not { } ownerLogin)
        {
            return null;
        }

        return new GitHubRepository(
            id,
            fullName,
            ownerLogin,
            GitHubRules.OwnerTypeOf(Text(owner, "type")),
            Text(root, "name") ?? fullName.Split('/')[^1],
            root.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True,
            Text(root, "default_branch") ?? "main",
            Long(root, "size") ?? 0,
            Text(root, "html_url") ?? $"https://github.com/{fullName}",
            root.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Object
                && PushLevels.Any(level => permissions.TryGetProperty(level, out var granted) && granted.ValueKind == JsonValueKind.True));
    }
}
