using System.Net;
using System.Text.Json;
using Aide.Domain.Entities;
using Aide.Infrastructure.GitLab;
using Aide.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Support;

/// <summary>
/// Stub DNS for Git hosts: explicit names first, then any <c>*.example</c>, <c>*.example.com</c> or <c>gitlab.com</c> name
/// resolves to a public address (never contacted: the GitLab handler is a stub). Anything else doesn't resolve.
/// </summary>
public sealed class StubGitHostDns(params (string Host, string Address)[] names) : IGitHostDns
{
    public const string PublicAddress = "20.0.0.10";

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var explicitMatch = names.Where(n => string.Equals(n.Host, host, StringComparison.OrdinalIgnoreCase)).Select(n => IPAddress.Parse(n.Address)).ToArray();
        if (explicitMatch.Length > 0)
        {
            return Task.FromResult(explicitMatch);
        }

        var name = host.ToLowerInvariant();
        return Task.FromResult(name == "gitlab.com" || name.EndsWith(".example", StringComparison.Ordinal) || name.EndsWith(".example.com", StringComparison.Ordinal)
            ? new[] { IPAddress.Parse(PublicAddress) }
            : []);
    }
}

/// <summary>
/// FR-130–FR-132 test helpers: the app with the <c>gitlab</c> HttpClient answered by a stub GitLab API (never a real
/// GitLab, CLAUDE.md rule 10) and stub DNS, seeded instances and connections, and the OAuth start/callback round trip.
/// </summary>
public static class GitLabHarness
{
    public const string ClientId = "gitlab-app-id";
    public const string ClientSecret = "gitlab-app-secret-0123456789";
    public const string PublicBaseUrl = "https://aide.test";
    public const string CallbackUrl = PublicBaseUrl + "/api/auth/gitlab/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The app whose GitLab traffic goes to <paramref name="gitlab"/>. With <paramref name="gitLabComOAuth"/>, gitlab.com's OAuth
    /// application comes from configuration (<c>GitLab:Instances:0:*</c>, BaseUrl empty), like secrets.env.
    /// </summary>
    public static WebApplicationFactory<Program> App(
        WebApplicationFactory<Program> factory,
        FakeGitHubApi gitlab,
        StubGitHostDns? dns = null,
        bool allowPrivateNetworks = true,
        bool gitLabComOAuth = false,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("App:PublicBaseUrl", PublicBaseUrl);
            b.UseSetting("GitLab:AllowPrivateNetworks", allowPrivateNetworks ? "true" : "false");
            if (gitLabComOAuth)
            {
                b.UseSetting("GitLab:Instances:0:Name", "GitLab.com");
                b.UseSetting("GitLab:Instances:0:BaseUrl", string.Empty);
                b.UseSetting("GitLab:Instances:0:ClientId", ClientId);
                b.UseSetting("GitLab:Instances:0:ClientSecret", ClientSecret);
            }

            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                b.UseSetting(key, value);
            }

            b.ConfigureTestServices(services =>
            {
                services.AddHttpClient(GitLabClients.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gitlab);
                services.AddSingleton<IGitHostDns>(dns ?? new StubGitHostDns());
            });
        });
    }

    /// <summary>A unique self-managed base URL (every test gets its own instance in the shared database).</summary>
    public static string UniqueBaseUrl(string label = "gitlab") => $"https://{label}-{Guid.NewGuid():N}.example";

    /// <summary>A self-managed instance row (OAuth application secret encrypted as in production).</summary>
    public static async Task<Guid> SeedInstanceAsync(WebApplicationFactory<Program> app, string? baseUrl = null, bool oauth = true, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(app);
        var protector = app.Services.GetRequiredService<ISecretProtectors>().ForPurpose(SecretPurposes.GitHostClientSecret);
        var row = new GitHostInstance
        {
            Kind = GitHostKind.Gitlab,
            Name = "Acme GitLab",
            BaseUrl = baseUrl ?? UniqueBaseUrl(),
            OauthClientId = oauth ? ClientId : string.Empty,
            OauthClientSecretEncrypted = oauth ? protector.Protect(ClientSecret) : string.Empty,
            Enabled = enabled,
            Version = "17.11.0",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.GitHostInstances.Add(row);
            await db.SaveChangesAsync(Ct);
        });
        return row.Id;
    }

    /// <summary>gitlab.com's instance id (its row is created on first use).</summary>
    public static async Task<Guid> GitLabComIdAsync(WebApplicationFactory<Program> app)
    {
        ArgumentNullException.ThrowIfNull(app);
        await using var scope = app.Services.CreateAsyncScope();
        var all = await scope.ServiceProvider.GetRequiredService<IGitLabInstances>().ListAsync(Ct);
        return all.Single(i => i.IsGitLabCom).Id;
    }

    /// <summary>A connection of <paramref name="userId"/> on <paramref name="instanceId"/> with encrypted tokens.</summary>
    public static async Task<Guid> SeedConnectionAsync(
        WebApplicationFactory<Program> app,
        Guid userId,
        Guid instanceId,
        string accessToken = "glpat-seeded-token",
        string? refreshToken = null,
        DateTimeOffset? accessExpiresAt = null,
        GitConnectionAuthKind authKind = GitConnectionAuthKind.Token,
        string externalUserId = "4242",
        string login = "ana")
    {
        ArgumentNullException.ThrowIfNull(app);
        var protector = app.Services.GetRequiredService<ISecretProtectors>().ForPurpose(SecretPurposes.GitHostUserToken);
        var row = new GitConnection
        {
            UserId = userId,
            Provider = GitHostKind.Gitlab,
            InstanceId = instanceId,
            AuthKind = authKind,
            ExternalUserId = externalUserId,
            Login = login,
            AccessTokenEncrypted = protector.Protect(accessToken),
            AccessExpiresAt = accessExpiresAt,
            RefreshTokenEncrypted = refreshToken is null ? null : protector.Protect(refreshToken),
            Scopes = ["api", "write_repository"],
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
    public static string Decrypt(WebApplicationFactory<Program> app, string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Services.GetRequiredService<ISecretProtectors>().ForPurpose(SecretPurposes.GitHostUserToken).Unprotect(ciphertext);
    }

    /// <summary>The fake GitLab's OAuth endpoints and identity for one user (<paramref name="userId"/>, verified email).</summary>
    public static FakeGitHubApi SignInGitLab(long userId, string username, string email, bool emailVerified = true, string accessToken = "gla-access-1", string refreshToken = "glr-refresh-1") =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, "/oauth/token", HttpStatusCode.OK,
                $$"""{"access_token":"{{accessToken}}","token_type":"Bearer","expires_in":7200,"refresh_token":"{{refreshToken}}","scope":"api write_repository openid profile email","created_at":1761650000}""")
            .On(HttpMethod.Get, "/api/v4/user", HttpStatusCode.OK,
                $$"""{"id":{{userId}},"username":"{{username}}","name":"{{username}} Example","email":"{{email}}","avatar_url":"https://gitlab.com/uploads/{{username}}.png","state":"active"}""")
            .On(HttpMethod.Get, "/oauth/userinfo", HttpStatusCode.OK,
                $$"""{"sub":"{{userId}}","email":"{{email}}","email_verified":{{(emailVerified ? "true" : "false")}},"nickname":"{{username}}"}""");

    /// <summary>The OAuth start: returns the authorize URL's query (state, challenge…). The state cookie stays in the session.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> StartAsync(ApiSession session, Guid? instanceId = null, string mode = "signin", string? returnUrl = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var query = new List<string> { $"mode={mode}" };
        if (instanceId is { } id)
        {
            query.Add($"instanceId={id}");
        }

        if (returnUrl is not null)
        {
            query.Add($"returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        var response = await session.Client.GetAsync(new Uri($"/api/auth/gitlab/start?{string.Join('&', query)}", UriKind.Relative), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"Expected 302, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        var location = response.Headers.Location!;
        var parameters = location.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
        parameters["authorize"] = location.GetLeftPart(UriPartial.Path);
        return parameters;
    }

    /// <summary>GitLab's redirect back to AIDE; returns the redirect AIDE answers with.</summary>
    public static async Task<string> CallbackAsync(ApiSession session, string state, string code = "the-code")
    {
        ArgumentNullException.ThrowIfNull(session);
        var response = await session.Client.GetAsync(
            new Uri($"/api/auth/gitlab/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    /// <summary>The form fields of a recorded <c>application/x-www-form-urlencoded</c> body.</summary>
    public static IReadOnlyDictionary<string, string> Form(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : string.Empty), StringComparer.Ordinal);

    public static JsonElement Body(GitHubRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonDocument.Parse(request.Body).RootElement.Clone();
    }

    /// <summary>GitLab's <c>project_creation_level</c> etc. as stored on a seeded project (FR-136 shape).</summary>
    public static Task BindProjectAsync(WebApplicationFactory<Program> app, Guid projectId, Guid instanceId) =>
        ProjectApi.WithDbAsync(app, async db =>
        {
            var settings = await db.ProjectGitSettings.SingleAsync(s => s.ProjectId == projectId, Ct);
            settings.Provider = GitProvider.Gitlab;
            settings.InstanceId = instanceId;
            settings.ExternalRepoId = "42";
            settings.RepoFullName = "acme/todo";
            settings.RepoUrl = "https://gitlab.example/acme/todo";
            await db.SaveChangesAsync(Ct);
        });
}
