using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aide.Api.Auth;
using Aide.Api.Features.GitHub;
using Aide.Infrastructure.GitHub;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Support;

/// <summary>
/// FR-80–FR-86 tests: the app with an AIDE GitHub App configured through <c>GitHub:App:*</c> (a fresh RSA key per harness)
/// or none, and every GitHub host — the REST API, the OAuth token endpoint and the sign-in backchannel — answered by one
/// <see cref="FakeGitHubApi"/>. Nothing reaches GitHub. A configured App lives only in this app instance, so the shared
/// test database never gets an App row (the manifest tests use their own database).
/// </summary>
public sealed class GitHubAppHarness : IAsyncDisposable
{
    public const long AppId = 424242;
    public const string ClientId = "Iv23liTestAppClient";
    public const string ClientSecret = "test-app-client-secret-0123456789";
    public const string Slug = "aide-test";
    public const string Code = "app-auth-code";
    public const string UserToken = "ghu_testUserAccessToken0123456789";
    public const string RefreshToken = "ghr_testUserRefreshToken0123456789";
    public const string RefreshedToken = "ghu_refreshedUserAccessToken9876543";
    public const string InstallationToken = "ghs_testInstallationToken0123456789";

    /// <summary>The bot user id GitHub reports for <c>aide-test[bot]</c>.</summary>
    public const long BotUserId = 9001;

    private GitHubAppHarness(RSA key, FakeGitHubApi api, WebApplicationFactory<Program> app)
    {
        Key = key;
        Api = api;
        App = app;
    }

    public RSA Key { get; }

    public FakeGitHubApi Api { get; }

    public WebApplicationFactory<Program> App { get; }

    /// <summary>
    /// The app with the App configured (<paramref name="configureApp"/>) and the OAuth endpoints faked. The token endpoint
    /// accepts <see cref="Code"/> with the App's client secret and a PKCE verifier, and the <see cref="RefreshToken"/>.
    /// </summary>
    public static GitHubAppHarness Create(
        WebApplicationFactory<Program> factory, bool configureApp = true, Action<IWebHostBuilder>? configure = null, Action<IServiceCollection>? services = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var key = RSA.Create(2048);
        var pem = key.ExportRSAPrivateKeyPem();
        var api = new FakeGitHubApi();
        var app = factory.WithWebHostBuilder(b =>
        {
            if (configureApp)
            {
                b.UseSetting("GitHub:App:AppId", AppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                b.UseSetting("GitHub:App:ClientId", ClientId);
                b.UseSetting("GitHub:App:ClientSecret", ClientSecret);
                // As in secrets.env: one line with \n escapes.
                b.UseSetting("GitHub:App:PrivateKey", pem.ReplaceLineEndings("\\n"));
                b.UseSetting("GitHub:App:Slug", Slug);
            }

            configure?.Invoke(b);
            b.ConfigureTestServices(s =>
            {
                s.AddHttpClient("github").ConfigurePrimaryHttpMessageHandler(() => api);
                s.AddHttpClient(GitHubApi.WebHttpClientName).ConfigurePrimaryHttpMessageHandler(() => api);
                s.Configure<OAuthOptions>(AuthConstants.GitHubScheme, o => o.BackchannelHttpHandler = api);
                services?.Invoke(s);
            });
        });
        var harness = new GitHubAppHarness(key, api, app);
        harness.OnTokenEndpoint();
        api.On(HttpMethod.Get, "/app", HttpStatusCode.OK, AppJson(administration: true));
        api.On(HttpMethod.Get, "/users/aide-test%5Bbot%5D", HttpStatusCode.OK, $$"""{"id":{{BotUserId}},"login":"aide-test[bot]","type":"Bot"}""");
        return harness;
    }

    /// <summary><c>GET /app</c>'s body, with or without Administration write.</summary>
    public static string AppJson(bool administration) =>
        JsonSerializer.Serialize(new
        {
            id = AppId,
            slug = Slug,
            owner = new { login = "acme", type = "Organization" },
            html_url = $"https://github.com/apps/{Slug}",
            permissions = administration
                ? new Dictionary<string, string> { ["contents"] = "write", ["pull_requests"] = "write", ["metadata"] = "read", ["administration"] = "write" }
                : new Dictionary<string, string> { ["contents"] = "write", ["pull_requests"] = "write", ["metadata"] = "read" },
        });

    /// <summary>The GitHub profile the sign-in backchannel returns for <c>GET /user</c>.</summary>
    public void OnUser(long id, string login, string? email, string? name = "App User")
    {
        Api.On(HttpMethod.Get, "/user", r => r.Authorization is $"Bearer {UserToken}" or $"Bearer {RefreshedToken}"
            ? (HttpStatusCode.OK, JsonSerializer.Serialize(new { id, login, name, email, avatar_url = $"https://avatars.example.com/u/{id}", type = "User" }))
            : (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}"""));
        Api.On(HttpMethod.Get, "/user/emails", HttpStatusCode.OK, "[]");
    }

    /// <summary>The installations <c>GET /user/installations</c> lists.</summary>
    public void OnInstallations(params (long Id, string Login, string Type, string Selection)[] installations) =>
        Api.On(HttpMethod.Get, "/user/installations", HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            total_count = installations.Length,
            installations = installations.Select(i => new
            {
                id = i.Id,
                account = new { login = i.Login, type = i.Type },
                repository_selection = i.Selection,
                permissions = new { contents = "write" },
            }),
        }));

    /// <summary>A repository as GitHub returns it; <c>push</c> is the caller's <c>permissions.push</c> on it (user-token views).</summary>
    public static string RepoJson(long id, string owner, string name, string ownerType = "Organization", bool isPrivate = true, string defaultBranch = "main", long size = 0, bool push = true) =>
        JsonSerializer.Serialize(new
        {
            id,
            name,
            full_name = $"{owner}/{name}",
            owner = new { login = owner, type = ownerType },
            @private = isPrivate,
            default_branch = defaultBranch,
            size,
            html_url = $"https://github.com/{owner}/{name}",
            permissions = new { admin = false, maintain = false, push, triage = true, pull = true },
        });

    /// <summary>The App's installation for a repository (<c>GET /repos/{o}/{r}/installation</c>, App JWT).</summary>
    public void OnRepoInstallation(string owner, string repo, long installationId) =>
        Api.On(HttpMethod.Get, $"/repos/{owner}/{repo}/installation", r =>
        {
            AssertAppJwt(r.Authorization);
            return (HttpStatusCode.OK, JsonSerializer.Serialize(new { id = installationId, account = new { login = owner } }));
        });

    /// <summary>The default branch head and its root entries (<paramref name="files"/>; none: the repository is empty).</summary>
    public void OnBranchRoot(string owner, string repo, string branch, string headSha, params string[] files)
    {
        if (files.Length == 0)
        {
            Api.On(HttpMethod.Get, $"/repos/{owner}/{repo}/branches/{branch}", HttpStatusCode.NotFound, """{"message":"Branch not found"}""");
            return;
        }

        Api.On(HttpMethod.Get, $"/repos/{owner}/{repo}/branches/{branch}", HttpStatusCode.OK, JsonSerializer.Serialize(new { name = branch, commit = new { sha = headSha } }));
        Api.On(HttpMethod.Get, $"/repos/{owner}/{repo}/git/trees/{headSha}", HttpStatusCode.OK,
            JsonSerializer.Serialize(new { sha = "tree", tree = files.Select(f => new { path = f, type = "blob" }) }));
    }

    /// <summary>Installation tokens for <paramref name="installationId"/> (<see cref="InstallationToken"/>, valid an hour).</summary>
    public void OnInstallationToken(long installationId, string token = InstallationToken) =>
        Api.On(HttpMethod.Post, $"/app/installations/{installationId}/access_tokens", r =>
        {
            AssertAppJwt(r.Authorization);
            return (HttpStatusCode.Created, JsonSerializer.Serialize(new
            {
                token,
                expires_at = DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                permissions = new { contents = "write", pull_requests = "write", metadata = "read" },
                repository_selection = "selected",
            }));
        });

    /// <summary>Stores a GitHub connection for <paramref name="userId"/> directly (as sign-in would).</summary>
    public async Task SeedConnectionAsync(Guid userId, long githubUserId, string login, DateTimeOffset? accessExpiresAt = null, string token = UserToken)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<GitHubConnections>().SaveAsync(
            userId,
            githubUserId,
            login,
            new GitHubUserTokens(token, accessExpiresAt ?? DateTimeOffset.UtcNow.AddHours(8), RefreshToken, DateTimeOffset.UtcNow.AddMonths(6)),
            TestContext.Current.CancellationToken);
        Assert.True(saved);
    }

    /// <summary>
    /// §4.14: <paramref name="authorization"/> is <c>Bearer &lt;App JWT&gt;</c>: RS256 signed with the App key, issued at most
    /// 60 s in the past, expiring within 10 minutes, issuer the App id.
    /// </summary>
    public void AssertAppJwt(string? authorization)
    {
        Assert.NotNull(authorization);
        Assert.StartsWith("Bearer ", authorization, StringComparison.Ordinal);
        var parts = authorization["Bearer ".Length..].Split('.');
        Assert.Equal(3, parts.Length);
        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var iat = payload.RootElement.GetProperty("iat").GetInt64();
        var exp = payload.RootElement.GetProperty("exp").GetInt64();
        Assert.Equal(AppId.ToString(System.Globalization.CultureInfo.InvariantCulture), payload.RootElement.GetProperty("iss").GetString());
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(now - iat, 55, 70);
        Assert.InRange(exp - iat, 1, 600);
        Assert.True(Key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64UrlDecode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    /// <summary>
    /// Starts GitHub sign-in (or connect) at <paramref name="startPath"/>, checks it uses the App's user authorization with
    /// PKCE (S256) and no scope, then calls back with GitHub's code and state.
    /// </summary>
    public static async Task<HttpResponseMessage> RunAppFlowAsync(ApiSession session, string startPath)
    {
        ArgumentNullException.ThrowIfNull(session);
        var ct = TestContext.Current.CancellationToken;
        var start = await session.Client.GetAsync(new Uri(startPath, UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = start.Headers.Location!;
        Assert.Equal(GitHubAuth.AuthorizationEndpoint, authorize.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(authorize.Query);
        Assert.Equal(ClientId, query["client_id"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrEmpty(query["code_challenge"].ToString()));
        Assert.False(query.ContainsKey("scope"));
        Assert.Equal("https://localhost/api/auth/github/callback", query["redirect_uri"].ToString());
        var state = Uri.EscapeDataString(query["state"].ToString());
        return await session.Client.GetAsync(new Uri($"/api/auth/github/callback?code={Code}&state={state}", UriKind.Relative), ct);
    }

    public ValueTask DisposeAsync()
    {
        Key.Dispose();
        return App.DisposeAsync();
    }

    private void OnTokenEndpoint() =>
        Api.On(HttpMethod.Post, "/login/oauth/access_token", r =>
        {
            var form = QueryHelpers.ParseQuery(r.Body);
            if (form["client_id"] != ClientId || form["client_secret"] != ClientSecret)
            {
                return (HttpStatusCode.OK, """{"error":"incorrect_client_credentials"}""");
            }

            if (form["grant_type"] == "refresh_token")
            {
                return form["refresh_token"] == RefreshToken
                    ? (HttpStatusCode.OK, $$"""{"access_token":"{{RefreshedToken}}","expires_in":28800,"refresh_token":"{{RefreshToken}}","refresh_token_expires_in":15811200,"token_type":"bearer","scope":""}""")
                    : (HttpStatusCode.OK, """{"error":"bad_refresh_token"}""");
            }

            return form["code"] == Code && !string.IsNullOrEmpty(form["code_verifier"])
                ? (HttpStatusCode.OK, $$"""{"access_token":"{{UserToken}}","expires_in":28800,"refresh_token":"{{RefreshToken}}","refresh_token_expires_in":15811200,"token_type":"bearer","scope":""}""")
                : (HttpStatusCode.OK, """{"error":"bad_verification_code"}""");
        });

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }
}
