using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Aide.Api.Auth;
using Aide.Api.Features.Auth;
using Aide.Domain.Access;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aide.IntegrationTests.Auth;

/// <summary>FR-2: GitHub sign-in and linking, through the real OAuth handler against a fake GitHub (no network).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitHubAuthTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/api/auth/github")]
    [InlineData("/api/auth/github/callback?code=x&state=y")]
    [InlineData("/api/auth/github/link")]
    public async Task FR2_GitHub_routes_return_404_when_not_configured(string path)
    {
        using var session = await ApiSession.RegisterAsync(factory);

        var response = await session.Client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task FR2_Providers_reports_github_enabled_when_configured()
    {
        var (app, _) = EnabledFactory();
        await using var _app = app;
        using var session = new ApiSession(app);

        var providers = await session.Client.GetFromJsonAsync<AuthProvidersDto>("/api/auth/providers", ApiSession.Json, Ct);

        Assert.True(providers?.Github);
        Assert.Equal(Aide.Domain.GitHub.GitHubSignInMode.Oauth, providers?.GithubMode); // D-V2-7: no App registered
    }

    [Fact]
    public async Task FR2_First_GitHub_sign_in_with_an_unknown_email_creates_an_account()
    {
        var (app, github) = EnabledFactory();
        await using var _app = app;
        var email = ApiSession.NewEmail("gh-new");
        github.Profile = FakeGitHub.NewProfile(email, "Grace Hopper");
        using var session = new ApiSession(app);

        var callback = await RunFlowAsync(session, "/api/auth/github?returnUrl=%2Fprojects%3Ftab%3Dmine");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/projects?tab=mine", callback.Headers.Location?.OriginalString);
        var me = await session.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        Assert.Equal(email, me!.Email);
        Assert.Equal("Grace Hopper", me.Name);
        Assert.True(me.GithubLinked);
        Assert.Equal(github.Profile.AvatarUrl, me.AvatarUrl);
        Assert.Equal(SystemRole.User, me.SystemRole);
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"), c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR2_Linked_GitHub_account_signs_in_as_the_linked_user()
    {
        var (app, github) = EnabledFactory();
        await using var _app = app;
        using var owner = await ApiSession.RegisterAsync(app);
        github.Profile = FakeGitHub.NewProfile(ApiSession.NewEmail("gh-other-email"), "Owner on GitHub");
        await LinkInDbAsync(owner.User!.Id, github.Profile.Id);
        using var session = new ApiSession(app);

        var callback = await RunFlowAsync(session, "/api/auth/github");

        Assert.Equal("/", callback.Headers.Location?.OriginalString);
        var me = await session.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        Assert.Equal(owner.User.Id, me!.Id);
    }

    [Fact]
    public async Task FR2_Signed_in_user_links_GitHub_and_can_then_sign_in_with_it()
    {
        var (app, github) = EnabledFactory();
        await using var _app = app;
        using var owner = await ApiSession.RegisterAsync(app);
        github.Profile = FakeGitHub.NewProfile(ApiSession.NewEmail("gh-link"), "Linker");

        var callback = await RunFlowAsync(owner, "/api/auth/github/link?returnUrl=%2Faccount");

        Assert.Equal("/account", callback.Headers.Location?.OriginalString);
        var me = await owner.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        Assert.True(me!.GithubLinked);
        Assert.Equal(github.Profile.AvatarUrl, me.AvatarUrl);

        using var fresh = new ApiSession(app);
        await RunFlowAsync(fresh, "/api/auth/github");
        var again = await fresh.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        Assert.Equal(owner.User!.Id, again!.Id);
    }

    [Fact]
    public async Task FR2_Link_flow_requires_sign_in()
    {
        var (app, _) = EnabledFactory();
        await using var _app = app;
        using var anonymous = new ApiSession(app);

        var response = await anonymous.Client.GetAsync(new Uri("/api/auth/github/link", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task FR2_Linking_a_GitHub_account_owned_by_someone_else_fails()
    {
        var (app, github) = EnabledFactory();
        await using var _app = app;
        using var first = await ApiSession.RegisterAsync(app);
        using var second = await ApiSession.RegisterAsync(app);
        github.Profile = FakeGitHub.NewProfile(ApiSession.NewEmail("gh-shared"), "Shared");
        await LinkInDbAsync(first.User!.Id, github.Profile.Id);

        var callback = await RunFlowAsync(second, "/api/auth/github/link");

        AssertFailure(callback, "already_linked");
        var me = await second.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        Assert.False(me!.GithubLinked);
    }

    [Fact]
    public async Task FR2_GitHub_email_matching_an_unlinked_account_is_not_auto_linked()
    {
        var (app, github) = EnabledFactory();
        await using var _app = app;
        var email = ApiSession.NewEmail("gh-existing");
        using var existing = await ApiSession.RegisterAsync(app, email);
        github.Profile = FakeGitHub.NewProfile(email, "Impostor");
        using var session = new ApiSession(app);

        var callback = await RunFlowAsync(session, "/api/auth/github");

        AssertFailure(callback, "link_required");
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct)).StatusCode);
        var stillUnlinked = await existing.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        Assert.False(stillUnlinked!.GithubLinked);
    }

    [Fact]
    public async Task FR2_Uses_the_primary_verified_email_when_the_profile_email_is_private()
    {
        var (app, github) = EnabledFactory();
        await using var _app = app;
        var email = ApiSession.NewEmail("gh-private");
        github.Profile = FakeGitHub.NewProfile(null, "Private");
        github.Emails = [("unverified@example.com", true, false), ("secondary@example.com", false, true), (email, true, true)];
        using var session = new ApiSession(app);

        await RunFlowAsync(session, "/api/auth/github");

        var me = await session.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        Assert.Equal(email, me!.Email);
    }

    [Fact]
    public async Task FR2_GitHub_account_without_a_verified_email_is_refused()
    {
        var (app, github) = EnabledFactory();
        await using var _app = app;
        github.Profile = FakeGitHub.NewProfile(null, "No email");
        using var session = new ApiSession(app);

        var callback = await RunFlowAsync(session, "/api/auth/github");

        AssertFailure(callback, "email_required");
    }

    [Fact]
    public async Task FR2_Token_exchange_failure_redirects_with_GITHUB_AUTH_FAILED()
    {
        var (app, github) = EnabledFactory();
        await using var _app = app;
        github.Profile = FakeGitHub.NewProfile(ApiSession.NewEmail("gh-fail"), "Fail");
        github.FailTokenExchange = true;
        using var session = new ApiSession(app);

        var callback = await RunFlowAsync(session, "/api/auth/github");

        AssertFailure(callback, "oauth_failed");
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task FR2_Callback_with_a_forged_state_redirects_with_GITHUB_AUTH_FAILED()
    {
        var (app, _) = EnabledFactory();
        await using var _app = app;
        using var session = new ApiSession(app);

        var callback = await session.Client.GetAsync(new Uri("/api/auth/github/callback?code=abc&state=forged", UriKind.Relative), Ct);

        AssertFailure(callback, "oauth_failed");
    }

    [Theory]
    [InlineData("https%3A%2F%2Fevil.example%2F")]
    [InlineData("%2F%2Fevil.example")]
    [InlineData("%2F%5Cevil.example")]
    public async Task FR2_External_return_urls_are_replaced_with_the_home_page(string returnUrl)
    {
        var (app, github) = EnabledFactory();
        await using var _app = app;
        github.Profile = FakeGitHub.NewProfile(ApiSession.NewEmail("gh-redirect"), "Redirect");
        using var session = new ApiSession(app);

        var callback = await RunFlowAsync(session, $"/api/auth/github?returnUrl={returnUrl}");

        Assert.Equal("/", callback.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task NFR_SEC_GitHub_client_secret_and_access_token_are_never_logged()
    {
        var (app, github) = EnabledFactory();
        await using var _app = app;
        github.Profile = FakeGitHub.NewProfile(ApiSession.NewEmail("gh-log"), "Log");
        using var session = new ApiSession(app);

        await RunFlowAsync(session, "/api/auth/github");

        Assert.DoesNotContain(factory.Logs.Entries, e =>
            e.AllText.Contains(FakeGitHub.ClientSecret, StringComparison.Ordinal)
            || e.AllText.Contains(FakeGitHub.AccessToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR2_Concurrent_link_of_the_same_GitHub_account_on_create_redirects_already_linked()
    {
        using var rival = await ApiSession.RegisterAsync(factory);
        var profile = FakeGitHub.NewProfile(ApiSession.NewEmail("gh-race-create"), "Race");
        var race = new LinkRaceInterceptor(factory.ConnectionString, profile.Id, rival.User!.Id);
        var (app, github) = EnabledFactory(s => s.ConfigureDbContext<AideDbContext>(o => o.AddInterceptors(race)));
        await using var _app = app;
        github.Profile = profile;
        using var session = new ApiSession(app);

        var callback = await RunFlowAsync(session, "/api/auth/github");

        Assert.True(race.Fired);
        AssertFailure(callback, "already_linked");
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task FR2_Concurrent_link_of_the_same_GitHub_account_on_link_redirects_already_linked()
    {
        using var rival = await ApiSession.RegisterAsync(factory);
        var profile = FakeGitHub.NewProfile(ApiSession.NewEmail("gh-race-link"), "Race");
        var race = new LinkRaceInterceptor(factory.ConnectionString, profile.Id, rival.User!.Id);
        var (app, github) = EnabledFactory(s => s.ConfigureDbContext<AideDbContext>(o => o.AddInterceptors(race)));
        await using var _app = app;
        github.Profile = profile;
        using var owner = await ApiSession.RegisterAsync(app);

        var callback = await RunFlowAsync(owner, "/api/auth/github/link");

        Assert.True(race.Fired);
        AssertFailure(callback, "already_linked");
        var me = await owner.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        Assert.False(me!.GithubLinked);
    }

    /// <summary>Starts the flow, checks the redirect to GitHub, then calls back with GitHub's code and state.</summary>
    private static async Task<HttpResponseMessage> RunFlowAsync(ApiSession session, string startPath)
    {
        var start = await session.Client.GetAsync(new Uri(startPath, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = start.Headers.Location!;
        Assert.StartsWith(GitHubAuth.AuthorizationEndpoint, authorize.GetLeftPart(UriPartial.Path), StringComparison.Ordinal);

        var query = QueryHelpers.ParseQuery(authorize.Query);
        Assert.Equal(FakeGitHub.ClientId, query["client_id"].ToString());
        Assert.Equal("https://localhost/api/auth/github/callback", query["redirect_uri"].ToString());
        Assert.False(string.IsNullOrEmpty(query["code_challenge"].ToString()));

        var state = Uri.EscapeDataString(query["state"].ToString());
        return await session.Client.GetAsync(new Uri($"/api/auth/github/callback?code={FakeGitHub.Code}&state={state}", UriKind.Relative), Ct);
    }

    private static void AssertFailure(HttpResponseMessage callback, string reason)
    {
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal($"/login?error=GITHUB_AUTH_FAILED&reason={reason}", callback.Headers.Location?.OriginalString);
    }

    private (WebApplicationFactory<Program> App, FakeGitHub GitHub) EnabledFactory(Action<IServiceCollection>? configure = null)
    {
        var github = new FakeGitHub();
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting(GitHubAuth.ClientIdKey, FakeGitHub.ClientId);
            b.UseSetting(GitHubAuth.ClientSecretKey, FakeGitHub.ClientSecret);
            b.ConfigureTestServices(services =>
            {
                services.Configure<OAuthOptions>(AuthConstants.GitHubScheme, o => o.BackchannelHttpHandler = github);
                configure?.Invoke(services);
            });
        });
        return (app, github);
    }

    private async Task LinkInDbAsync(Guid userId, long githubId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.GithubId, githubId.ToString(System.Globalization.CultureInfo.InvariantCulture)), Ct);
    }

    /// <summary>GitHub's token, user and emails endpoints, answered in memory.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public const string ClientId = "test-client-id";
        public const string ClientSecret = "test-client-secret-value";
        public const string Code = "fake-code";
        public const string AccessToken = "gho_fake_access_token_value";

        public GitHubProfile Profile { get; set; } = NewProfile(null, null);

        public IReadOnlyList<(string Email, bool Primary, bool Verified)> Emails { get; set; } = [];

        public bool FailTokenExchange { get; set; }

        public static GitHubProfile NewProfile(string? email, string? name) =>
            new(RandomNumberGenerator.GetInt32(1, int.MaxValue) + 10_000_000_000L, $"login{Guid.NewGuid():N}"[..20], name, email,
                $"https://avatars.example.com/u/{Guid.NewGuid():N}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
            if (url == GitHubAuth.TokenEndpoint)
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                var fields = QueryHelpers.ParseQuery(form);
                if (FailTokenExchange || fields["code"] != Code || fields["client_secret"] != ClientSecret
                    || string.IsNullOrEmpty(fields["code_verifier"]))
                {
                    return Json(HttpStatusCode.BadRequest, new { error = "bad_verification_code" });
                }

                return Json(HttpStatusCode.OK, new { access_token = AccessToken, token_type = "bearer", scope = "read:user,user:email" });
            }

            if (request.Headers.Authorization?.Parameter != AccessToken)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (url == GitHubAuth.UserEndpoint)
            {
                return Json(HttpStatusCode.OK, new
                {
                    id = Profile.Id,
                    login = Profile.Login,
                    name = Profile.Name,
                    email = Profile.Email,
                    avatar_url = Profile.AvatarUrl,
                });
            }

            if (url == GitHubAuth.EmailsEndpoint)
            {
                return Json(HttpStatusCode.OK, Emails.Select(e => new { email = e.Email, primary = e.Primary, verified = e.Verified }));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = JsonContent.Create(body) };
    }

    /// <summary>Simulates a concurrent request: just before the app saves a user carrying the GitHub id, links that id
    /// to another user on a separate connection, so the app's save hits the unique index.</summary>
    private sealed class LinkRaceInterceptor(string connectionString, long githubId, Guid rivalUserId) : SaveChangesInterceptor
    {
        private int _fired;

        public bool Fired => _fired == 1;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var id = githubId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (eventData.Context!.ChangeTracker.Entries<AppUser>().Any(e => e.Entity.GithubId == id && e.State is EntityState.Added or EntityState.Modified)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand("UPDATE users SET github_id = @g WHERE id = @id", connection);
                command.Parameters.AddWithValue("g", id);
                command.Parameters.AddWithValue("id", rivalUserId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed record GitHubProfile(long Id, string Login, string? Name, string? Email, string AvatarUrl);
}
