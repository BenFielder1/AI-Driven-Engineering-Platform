using System.Net;
using Aide.Domain.Entities;
using Aide.Infrastructure.Bitbucket;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Support;

/// <summary>
/// FR-133–FR-135 test helpers: the app with the <c>bitbucket</c> HttpClient answered by a stub Bitbucket (both
/// <c>bitbucket.org</c> OAuth and <c>api.bitbucket.org/2.0</c>, routed by path; never a real Bitbucket, CLAUDE.md rule 10),
/// the consumer from configuration or the database, seeded connections, the OAuth start/callback round trip, and a folder of
/// bare repositories standing in for bitbucket.org's Git over HTTPS.
/// </summary>
public static class BitbucketHarness
{
    public const string ClientId = "bitbucket-consumer-key";
    public const string ClientSecret = "bitbucket-consumer-secret-0123456789";
    public const string PublicBaseUrl = "https://aide.test";
    public const string CallbackUrl = PublicBaseUrl + "/api/auth/bitbucket/callback";

    /// <summary>Every scope an API token needs (FR-134).</summary>
    public static readonly string[] AllScopes = [.. Domain.GitHosts.GitTokenRules.BitbucketTokenScopes];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The app whose Bitbucket traffic goes to <paramref name="bitbucket"/>. With <paramref name="configuredConsumer"/>, the
    /// OAuth consumer comes from configuration (<c>Bitbucket:OAuth:*</c>), like secrets.env.
    /// </summary>
    public static WebApplicationFactory<Program> App(
        WebApplicationFactory<Program> factory,
        FakeGitHubApi bitbucket,
        bool configuredConsumer = false,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("App:PublicBaseUrl", PublicBaseUrl);
            if (configuredConsumer)
            {
                b.UseSetting("Bitbucket:OAuth:ClientId", ClientId);
                b.UseSetting("Bitbucket:OAuth:ClientSecret", ClientSecret);
            }

            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                b.UseSetting(key, value);
            }

            b.ConfigureTestServices(services =>
                services.AddHttpClient(BitbucketClients.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => bitbucket));
        });
    }

    /// <summary>
    /// As <see cref="App"/>, with Git over HTTPS redirected to <paramref name="gitRoot"/> (a <c>file://</c> folder of bare
    /// repositories, tests-only <c>Bitbucket:GitBaseUrlOverride</c>) and every git command recorded.
    /// </summary>
    public static WebApplicationFactory<Program> AppWithGit(
        WebApplicationFactory<Program> factory, FakeGitHubApi bitbucket, string gitRoot, out RecordingGitCli git, bool configuredConsumer = false)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var recorder = new RecordingGitCli[1];
        var app = App(factory, bitbucket, configuredConsumer, new Dictionary<string, string>
        {
            ["Bitbucket:GitBaseUrlOverride"] = new Uri(gitRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri,
            ["Bitbucket:AllowInsecureGitBaseUrl"] = "true",
        }).WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var real = services.Last(d => d.ServiceType == typeof(IGitCli));
            services.Remove(real);
            services.AddSingleton<IGitCli>(sp =>
            {
                var inner = real.ImplementationInstance as IGitCli ?? (IGitCli)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!);
                return recorder[0] = new RecordingGitCli(inner);
            });
        }));
        _ = app.Services.GetRequiredService<IGitCli>();
        git = recorder[0];
        return app;
    }

    /// <summary>A new Bitbucket account UUID (with braces, as Bitbucket sends it).</summary>
    public static string NewUuid() => "{" + Guid.NewGuid().ToString("D") + "}";

    /// <summary>The Bitbucket Cloud row's id (created on first use).</summary>
    public static async Task<Guid> InstanceIdAsync(WebApplicationFactory<Program> app)
    {
        ArgumentNullException.ThrowIfNull(app);
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IBitbucketConsumer>().GetAsync(Ct)).InstanceId;
    }

    /// <summary>Sets (or with <c>null</c> clears) the consumer saved in the database, as an Admin would.</summary>
    public static async Task SetDatabaseConsumerAsync(WebApplicationFactory<Program> app, string? clientId, string? clientSecret)
    {
        ArgumentNullException.ThrowIfNull(app);
        var id = await InstanceIdAsync(app);
        var protector = app.Services.GetRequiredService<ISecretProtectors>().ForPurpose(SecretPurposes.GitHostClientSecret);
        await ProjectApi.WithDbAsync(app, async db =>
        {
            var row = await db.GitHostInstances.SingleAsync(i => i.Id == id, Ct);
            row.OauthClientId = clientId ?? string.Empty;
            row.OauthClientSecretEncrypted = clientSecret is null ? string.Empty : protector.Protect(clientSecret);
            await db.SaveChangesAsync(Ct);
        });
    }

    /// <summary>A Bitbucket connection of <paramref name="userId"/> with encrypted tokens (an API token needs <paramref name="email"/>).</summary>
    public static async Task<Guid> SeedConnectionAsync(
        WebApplicationFactory<Program> app,
        Guid userId,
        string accessToken = "bb-seeded-token",
        string? refreshToken = null,
        DateTimeOffset? accessExpiresAt = null,
        GitConnectionAuthKind authKind = GitConnectionAuthKind.Oauth,
        string? email = null,
        string? externalUserId = null,
        string login = "ana")
    {
        ArgumentNullException.ThrowIfNull(app);
        var instanceId = await InstanceIdAsync(app);
        var protectors = app.Services.GetRequiredService<ISecretProtectors>();
        var row = new GitConnection
        {
            UserId = userId,
            Provider = GitHostKind.Bitbucket,
            InstanceId = instanceId,
            AuthKind = authKind,
            ExternalUserId = externalUserId ?? NewUuid(),
            Login = login,
            AccessTokenEncrypted = protectors.ForPurpose(SecretPurposes.GitHostUserToken).Protect(accessToken),
            AccessExpiresAt = accessExpiresAt,
            RefreshTokenEncrypted = refreshToken is null ? null : protectors.ForPurpose(SecretPurposes.GitHostUserToken).Protect(refreshToken),
            TokenEmailEncrypted = email is null ? null : protectors.ForPurpose(SecretPurposes.GitHostTokenEmail).Protect(email),
            Scopes = authKind == GitConnectionAuthKind.Token ? AllScopes : ["account", "repository:write"],
            Status = GitConnectionStatus.Valid,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.GitConnections.Add(row);
            await db.SaveChangesAsync(Ct);
        });
        return row.Id;
    }

    /// <summary>Decrypts a stored token (assertions only).</summary>
    public static string Decrypt(WebApplicationFactory<Program> app, string ciphertext, string purpose = SecretPurposes.GitHostUserToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Services.GetRequiredService<ISecretProtectors>().ForPurpose(purpose).Unprotect(ciphertext);
    }

    /// <summary>Bitbucket's <c>GET /2.0/user</c> body.</summary>
    public static string UserJson(string uuid, string username) =>
        $$"""{"type":"user","uuid":"{{uuid}}","username":"{{username}}","display_name":"{{username}} Example","account_id":"5f-{{username}}","links":{"avatar":{"href":"https://bitbucket.org/account/{{username}}/avatar/"} } }""";

    /// <summary>The fake Bitbucket's OAuth token endpoint, identity and emails for one user.</summary>
    public static FakeGitHubApi SignInBitbucket(string uuid, string username, string email, bool confirmed = true, string accessToken = "bb-access-1", string refreshToken = "bb-refresh-1") =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, "/site/oauth2/access_token", HttpStatusCode.OK,
                $$"""{"access_token":"{{accessToken}}","token_type":"bearer","expires_in":7200,"refresh_token":"{{refreshToken}}","scopes":"account email repository:write repository:admin pullrequest:write project webhook"}""")
            .On(HttpMethod.Get, "/2.0/user", HttpStatusCode.OK, UserJson(uuid, username))
            .On(HttpMethod.Get, "/2.0/user/emails", HttpStatusCode.OK,
                $$"""{"values":[{"type":"email","email":"{{email}}","is_primary":true,"is_confirmed":{{(confirmed ? "true" : "false")}}}],"pagelen":100}""");

    /// <summary>
    /// The calls an API token's scope check makes, all answering as for a token with every scope, on workspace <c>acme</c>
    /// and its repository <c>todo</c>; override single routes to take a scope away. The <c>write:repository</c> probe's
    /// create on the invalid slug answers 400 (registered escaped and unescaped).
    /// </summary>
    public static FakeGitHubApi TokenBitbucket(string uuid, string username = "ana") =>
        new FakeGitHubApi()
            .On(HttpMethod.Get, "/2.0/user", HttpStatusCode.OK, UserJson(uuid, username))
            .On(HttpMethod.Get, "/2.0/pullrequests/" + Uri.EscapeDataString(uuid), HttpStatusCode.OK, """{"values":[]}""")
            .On(HttpMethod.Get, "/2.0/pullrequests/" + uuid, HttpStatusCode.OK, """{"values":[]}""")
            .On(HttpMethod.Post, "/2.0/repositories/acme/aide-scope-check%21", HttpStatusCode.BadRequest, """{"type":"error","error":{"message":"Invalid slug."}}""")
            .On(HttpMethod.Post, "/2.0/repositories/acme/aide-scope-check!", HttpStatusCode.BadRequest, """{"type":"error","error":{"message":"Invalid slug."}}""")
            .On(HttpMethod.Get, "/2.0/user/workspaces", HttpStatusCode.OK, WorkspacesJson("acme"))
            .On(HttpMethod.Get, "/2.0/workspaces/acme/projects", HttpStatusCode.OK, """{"values":[{"key":"TODO","name":"Todo","uuid":"{p-1}","is_private":true}]}""")
            .On(HttpMethod.Get, "/2.0/repositories/acme", HttpStatusCode.OK, ReposJson(("acme", "todo")))
            .On(HttpMethod.Get, "/2.0/repositories/acme/todo/pullrequests", HttpStatusCode.OK, """{"values":[]}""")
            .On(HttpMethod.Get, "/2.0/repositories/acme/todo/pipelines", HttpStatusCode.OK, """{"values":[]}""")
            .On(HttpMethod.Get, "/2.0/repositories/acme/todo/branch-restrictions", HttpStatusCode.OK, """{"values":[]}""")
            .On(HttpMethod.Post, "/2.0/repositories/acme/todo/refs/branches", HttpStatusCode.BadRequest, """{"type":"error","error":{"message":"name: This field is required."}}""")
            .On(HttpMethod.Post, "/2.0/repositories/acme/todo/pullrequests", HttpStatusCode.BadRequest, """{"type":"error","error":{"message":"title: This field is required."}}""");

    /// <summary>A Bitbucket 403 for missing scopes.</summary>
    public const string ScopeError = """{"type":"error","error":{"message":"Your credentials lack one or more required privilege scopes.","detail":{"required":["write:pullrequest:bitbucket"]}}}""";

    public static string WorkspacesJson(params string[] slugs) =>
        "{\"values\":[" + string.Join(',', slugs.Select(s => $$"""{"type":"workspace_access","administrator":true,"workspace":{"type":"workspace_base","slug":"{{s}}","name":"{{s}} team","uuid":"{w-{{s}}}"} }""")) + "]}";

    public static string ReposJson(params (string Workspace, string Slug)[] repos) => ReposJson(next: false, repos);

    public static string ReposJson(bool next, params (string Workspace, string Slug)[] repos) =>
        "{\"values\":[" + string.Join(',', repos.Select(r => RepoJson(r.Workspace, r.Slug))) + "]"
        + (next ? ",\"next\":\"https://api.bitbucket.org/2.0/repositories/x?page=2\"" : string.Empty) + "}";

    public static string RepoJson(string workspace, string slug, string? mainBranch = "main")
    {
        var main = mainBranch is null ? "null" : "{\"name\":\"" + mainBranch + "\"}";
        return $$"""{"type":"repository","uuid":"{r-{{workspace}}-{{slug}}}","full_name":"{{workspace}}/{{slug}}","name":"{{slug}}","slug":"{{slug}}","is_private":true,"workspace":{"slug":"{{workspace}}"},"project":{"key":"TODO"},"mainbranch":{{main}},"links":{"html":{"href":"https://bitbucket.org/{{workspace}}/{{slug}}"} },"updated_on":"2026-10-28T10:00:00.000000+00:00"}""";
    }

    /// <summary>The OAuth start: returns the authorize URL's query (client_id, state…). The state cookie stays in the session.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> StartAsync(ApiSession session, string mode = "signin", string? returnUrl = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var query = $"mode={mode}" + (returnUrl is null ? string.Empty : $"&returnUrl={Uri.EscapeDataString(returnUrl)}");
        var response = await session.Client.GetAsync(new Uri($"/api/auth/bitbucket/start?{query}", UriKind.Relative), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"Expected 302, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        var location = response.Headers.Location!;
        var parameters = location.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
        parameters["authorize"] = location.GetLeftPart(UriPartial.Path);
        return parameters;
    }

    /// <summary>Bitbucket's redirect back to AIDE; returns the redirect AIDE answers with.</summary>
    public static async Task<string> CallbackAsync(ApiSession session, string state, string code = "the-code")
    {
        ArgumentNullException.ThrowIfNull(session);
        var response = await session.Client.GetAsync(
            new Uri($"/api/auth/bitbucket/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }
}
