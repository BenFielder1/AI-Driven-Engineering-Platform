using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Auth;
using Aide.Api.Features.GitLab;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.GitLab;

/// <summary>
/// FR-131: "Continue with GitLab" and "Connect GitLab" against a stub GitLab (gitlab.com's OAuth application from
/// configuration): PKCE S256 with the verifier sent on exchange, the signed single-use browser-bound state, account creation
/// on first sign-in, matching by (gitlab, instance, user id), and the explicit email-link confirmation.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitLabSignInTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long NewGitLabId() => Random.Shared.NextInt64(1_000_000, 9_000_000_000);

    [Fact]
    public async Task FR131_Start_redirects_to_GitLab_authorize_with_PKCE_S256_scopes_state_and_the_callback()
    {
        var app = GitLabHarness.App(factory, new FakeGitHubApi(), gitLabComOAuth: true);
        using var browser = new ApiSession(app);

        var authorize = await GitLabHarness.StartAsync(browser, returnUrl: "/projects");

        Assert.Equal("https://gitlab.com/oauth/authorize", authorize["authorize"]);
        Assert.Equal((GitLabHarness.ClientId, GitLabHarness.CallbackUrl, "code", "S256"),
            (authorize["client_id"], authorize["redirect_uri"], authorize["response_type"], authorize["code_challenge_method"]));
        Assert.Equal("api write_repository openid profile email", authorize["scope"]);
        Assert.False(string.IsNullOrEmpty(authorize["state"]));
        Assert.Equal(43, authorize["code_challenge"].Length);
        Assert.Contains(browser.Cookies.Container.GetCookies(new Uri("https://localhost/api/auth/gitlab/callback")), c => c.Name == GitLabOAuthStates.CookieName && c.HttpOnly);
    }

    [Fact]
    public async Task FR131_First_sign_in_on_gitlab_com_creates_an_account_and_a_connection_and_sends_the_PKCE_verifier()
    {
        var gitlabId = NewGitLabId();
        var email = ApiSession.NewEmail("gitlab");
        var gitlab = GitLabHarness.SignInGitLab(gitlabId, "ana-gl", email);
        var app = GitLabHarness.App(factory, gitlab, gitLabComOAuth: true);
        using var browser = new ApiSession(app);

        var authorize = await GitLabHarness.StartAsync(browser, returnUrl: "/projects");
        var redirect = await GitLabHarness.CallbackAsync(browser, authorize["state"]);

        Assert.Equal("/projects", redirect);
        var exchange = GitLabHarness.Form(Assert.Single(gitlab.Requests, r => r.Path == "/oauth/token").Body);
        Assert.Equal(("authorization_code", "the-code", GitLabHarness.CallbackUrl, GitLabHarness.ClientId),
            (exchange["grant_type"], exchange["code"], exchange["redirect_uri"], exchange["client_id"]));
        Assert.Equal(authorize["code_challenge"], GitLabRules.PkceChallenge(exchange["code_verifier"]));
        Assert.Equal("Bearer gla-access-1", Assert.Single(gitlab.Requests, r => r.Path == "/api/v4/user").Authorization);

        var me = await ProjectApi.ReadAsync<UserDto>(await browser.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct));
        Assert.Equal((email, SystemRole.User), (me.Email, me.SystemRole));
        var gitlabCom = await GitLabHarness.GitLabComIdAsync(app);
        var login = await ProjectApi.WithDbAsync(app, db => db.UserExternalLogins.AsNoTracking().SingleAsync(l => l.UserId == me.Id, Ct));
        Assert.Equal((ExternalLoginProvider.Gitlab, (Guid?)gitlabCom, gitlabId.ToString(System.Globalization.CultureInfo.InvariantCulture), "ana-gl"),
            (login.Provider, login.InstanceId, login.ExternalUserId, login.Login));
        var connection = await ProjectApi.WithDbAsync(app, db => db.GitConnections.AsNoTracking().SingleAsync(c => c.UserId == me.Id, Ct));
        Assert.Equal((GitConnectionAuthKind.Oauth, GitConnectionStatus.Valid, gitlabCom), (connection.AuthKind, connection.Status, connection.InstanceId));
        Assert.Equal("gla-access-1", GitLabHarness.Decrypt(app, connection.AccessTokenEncrypted));
        Assert.Equal("glr-refresh-1", GitLabHarness.Decrypt(app, connection.RefreshTokenEncrypted!));
        Assert.True(connection.AccessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(100));
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains("gla-access-1", StringComparison.Ordinal) || e.AllText.Contains(GitLabHarness.ClientSecret, StringComparison.Ordinal));

        var connections = await ProjectApi.ReadAsync<List<GitLabConnectionDto>>(await browser.Client.GetAsync(new Uri("/api/me/connections/gitlab", UriKind.Relative), Ct));
        Assert.Equal(("ana-gl", GitConnectionAuthKind.Oauth), (Assert.Single(connections).Login, connections[0].AuthKind));
    }

    [Fact]
    public async Task FR131_Signing_in_again_matches_the_GitLab_identity_not_the_email()
    {
        var gitlabId = NewGitLabId();
        var email = ApiSession.NewEmail("gitlab");
        var app = GitLabHarness.App(factory, GitLabHarness.SignInGitLab(gitlabId, "ben-gl", email), gitLabComOAuth: true);
        using (var first = new ApiSession(app))
        {
            await GitLabHarness.CallbackAsync(first, (await GitLabHarness.StartAsync(first))["state"]);
        }

        // Same GitLab user id, a different (verified) email: still the same account.
        var again = GitLabHarness.App(factory, GitLabHarness.SignInGitLab(gitlabId, "ben-gl", ApiSession.NewEmail("changed")), gitLabComOAuth: true);
        using var second = new ApiSession(again);
        Assert.Equal("/", await GitLabHarness.CallbackAsync(second, (await GitLabHarness.StartAsync(second))["state"]));

        var me = await ProjectApi.ReadAsync<UserDto>(await second.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct));
        Assert.Equal(email, me.Email);
    }

    [Fact]
    public async Task FR131_S8_A_state_is_single_use_and_bound_to_the_browser_that_started_it()
    {
        var gitlab = GitLabHarness.SignInGitLab(NewGitLabId(), "cleo-gl", ApiSession.NewEmail("gitlab"));
        var app = GitLabHarness.App(factory, gitlab, gitLabComOAuth: true);
        using var browser = new ApiSession(app);
        var state = (await GitLabHarness.StartAsync(browser))["state"];
        Assert.Equal("/", await GitLabHarness.CallbackAsync(browser, state));

        using var replay = new ApiSession(app);
        var replayed = await GitLabHarness.CallbackAsync(replay, state);
        Assert.StartsWith($"/login?error={ErrorCodes.GitAuthFailed}&reason=state", replayed, StringComparison.Ordinal);

        // A fresh state used from another browser (no state cookie) is refused too.
        var other = (await GitLabHarness.StartAsync(browser))["state"];
        using var attacker = new ApiSession(app);
        Assert.StartsWith($"/login?error={ErrorCodes.GitAuthFailed}&reason=state", await GitLabHarness.CallbackAsync(attacker, other), StringComparison.Ordinal);
        Assert.Single(gitlab.Requests, r => r.Path == "/oauth/token");
    }

    [Fact]
    public async Task FR131_A_failed_token_exchange_redirects_with_GIT_AUTH_FAILED()
    {
        var gitlab = new FakeGitHubApi().On(HttpMethod.Post, "/oauth/token", HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var app = GitLabHarness.App(factory, gitlab, gitLabComOAuth: true);
        using var browser = new ApiSession(app);

        var redirect = await GitLabHarness.CallbackAsync(browser, (await GitLabHarness.StartAsync(browser))["state"]);

        Assert.Equal($"/login?error={ErrorCodes.GitAuthFailed}&reason=token_exchange&provider=gitlab", redirect);
    }

    [Fact]
    public async Task FR131_D_V4_18_An_existing_email_is_linked_only_after_its_owner_signs_in_and_confirms()
    {
        var gitlabId = NewGitLabId();
        var email = ApiSession.NewEmail("existing");
        var app = GitLabHarness.App(factory, GitLabHarness.SignInGitLab(gitlabId, "dana-gl", email), gitLabComOAuth: true);
        using var existing = await ApiSession.RegisterAsync(app, email);
        using var browser = new ApiSession(app);

        var redirect = await GitLabHarness.CallbackAsync(browser, (await GitLabHarness.StartAsync(browser))["state"]);

        Assert.StartsWith("/login?link=gitlab", redirect, StringComparison.Ordinal);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.UserExternalLogins.AnyAsync(l => l.UserId == existing.User!.Id, Ct)));
        await Problems.AssertAsync(await browser.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        var pending = await ProjectApi.ReadAsync<GitLabPendingLinkDto>(await browser.Client.GetAsync(new Uri("/api/auth/gitlab/link", UriKind.Relative), Ct));
        Assert.Equal(("dana-gl", email), (pending.Login, pending.Email));

        // The owner signs in with their password in the same browser, then confirms.
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Client.PostAsJsonAsync("/api/auth/login", new { email, password = AideApiFactory.Password }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Client.PostAsync(new Uri("/api/auth/gitlab/link", UriKind.Relative), null, Ct)).StatusCode);

        var login = await ProjectApi.WithDbAsync(app, db => db.UserExternalLogins.AsNoTracking().SingleAsync(l => l.UserId == existing.User!.Id, Ct));
        Assert.Equal(gitlabId.ToString(System.Globalization.CultureInfo.InvariantCulture), login.ExternalUserId);
        Assert.True(await ProjectApi.WithDbAsync(app, db => db.GitConnections.AnyAsync(c => c.UserId == existing.User!.Id && c.AuthKind == GitConnectionAuthKind.Oauth, Ct)));
        await Problems.AssertAsync(await browser.Client.GetAsync(new Uri("/api/auth/gitlab/link", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR131_A_pending_link_cant_be_confirmed_by_another_account()
    {
        var email = ApiSession.NewEmail("victim");
        var app = GitLabHarness.App(factory, GitLabHarness.SignInGitLab(NewGitLabId(), "eve-gl", email), gitLabComOAuth: true);
        using var victim = await ApiSession.RegisterAsync(app, email);
        using var browser = new ApiSession(app);
        await GitLabHarness.CallbackAsync(browser, (await GitLabHarness.StartAsync(browser))["state"]);

        var other = ApiSession.NewEmail("other");
        Assert.Equal(HttpStatusCode.Created, (await browser.Client.PostAsJsonAsync("/api/auth/register", new { email = other, name = "Other", password = AideApiFactory.Password }, Ct)).StatusCode);
        var response = await browser.Client.PostAsync(new Uri("/api/auth/gitlab/link", UriKind.Relative), null, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.UserExternalLogins.AnyAsync(l => l.UserId == victim.User!.Id, Ct)));
    }

    [Fact]
    public async Task FR131_An_unverified_email_never_creates_or_matches_an_account()
    {
        var app = GitLabHarness.App(factory, GitLabHarness.SignInGitLab(NewGitLabId(), "fay-gl", ApiSession.NewEmail("unverified"), emailVerified: false), gitLabComOAuth: true);
        using var browser = new ApiSession(app);

        var redirect = await GitLabHarness.CallbackAsync(browser, (await GitLabHarness.StartAsync(browser))["state"]);

        Assert.Equal($"/login?error={ErrorCodes.GitAuthFailed}&reason=email_required&provider=gitlab", redirect);
    }

    [Fact]
    public async Task FR131_Connect_links_GitLab_to_the_signed_in_user_and_returns_to_the_page()
    {
        var gitlabId = NewGitLabId();
        var app = GitLabHarness.App(factory, GitLabHarness.SignInGitLab(gitlabId, "gil-gl", ApiSession.NewEmail("connect")), gitLabComOAuth: true);
        using var user = await ApiSession.RegisterAsync(app);

        var authorize = await GitLabHarness.StartAsync(user, mode: "connect", returnUrl: "/account/connections");
        Assert.Equal("/account/connections", await GitLabHarness.CallbackAsync(user, authorize["state"]));

        var connection = await ProjectApi.WithDbAsync(app, db => db.GitConnections.AsNoTracking().SingleAsync(c => c.UserId == user.User!.Id, Ct));
        Assert.Equal(("gil-gl", GitConnectionAuthKind.Oauth), (connection.Login, connection.AuthKind));
        Assert.True(await ProjectApi.WithDbAsync(app, db => db.UserExternalLogins.AnyAsync(l => l.UserId == user.User!.Id && l.Provider == ExternalLoginProvider.Gitlab, Ct)));
    }

    [Fact]
    public async Task FR131_Connect_without_a_session_is_401_and_an_instance_without_OAuth_is_404()
    {
        var app = GitLabHarness.App(factory, new FakeGitHubApi());
        using var anonymous = new ApiSession(app);
        var tokenOnly = await GitLabHarness.SeedInstanceAsync(app, oauth: false);

        await Problems.AssertAsync(
            await anonymous.Client.GetAsync(new Uri("/api/auth/gitlab/start?mode=connect", UriKind.Relative), Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        await Problems.AssertAsync(
            await anonymous.Client.GetAsync(new Uri($"/api/auth/gitlab/start?instanceId={tokenOnly}", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await anonymous.Client.GetAsync(new Uri($"/api/auth/gitlab/start?instanceId={Guid.NewGuid()}", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        var providers = await ProjectApi.ReadAsync<AuthProvidersDto>(await anonymous.Client.GetAsync(new Uri("/api/auth/providers", UriKind.Relative), Ct));
        Assert.DoesNotContain(providers.Gitlab, i => i.Id == tokenOnly);
    }
}
