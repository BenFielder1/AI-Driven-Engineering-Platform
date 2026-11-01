using System.Net;
using System.Net.Http.Json;
using System.Text;
using Aide.Api.Features.Auth;
using Aide.Api.Features.Bitbucket;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Bitbucket;

/// <summary>
/// FR-134, D-V4-18: "Continue with Bitbucket" and "Connect Bitbucket" against a stub Bitbucket (the consumer from
/// configuration): the authorize redirect with a signed single-use browser-bound state, the code exchange with client Basic
/// auth, account creation from a confirmed email, matching by (bitbucket, account uuid) and never silently by email, and
/// the stored OAuth connection with its refresh token.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class BitbucketSignInTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR134_Start_redirects_to_Bitbucket_authorize_with_the_consumer_key_and_a_browser_bound_state()
    {
        var app = BitbucketHarness.App(factory, new FakeGitHubApi(), configuredConsumer: true);
        using var browser = new ApiSession(app);

        var authorize = await BitbucketHarness.StartAsync(browser, returnUrl: "/projects");

        Assert.Equal("https://bitbucket.org/site/oauth2/authorize", authorize["authorize"]);
        Assert.Equal((BitbucketHarness.ClientId, "code"), (authorize["client_id"], authorize["response_type"]));
        Assert.False(string.IsNullOrEmpty(authorize["state"]));
        Assert.False(authorize.ContainsKey("client_secret"));
        Assert.Contains(browser.Cookies.Container.GetCookies(new Uri("https://localhost/api/auth/bitbucket/callback")), c => c.Name == BitbucketOAuthStates.CookieName && c.HttpOnly);
    }

    [Fact]
    public async Task FR134_First_sign_in_creates_an_account_from_the_confirmed_email_and_stores_the_OAuth_connection()
    {
        var uuid = BitbucketHarness.NewUuid();
        var email = ApiSession.NewEmail("bitbucket");
        var bitbucket = BitbucketHarness.SignInBitbucket(uuid, "ana-bb", email);
        var app = BitbucketHarness.App(factory, bitbucket, configuredConsumer: true);
        using var browser = new ApiSession(app);

        var redirect = await BitbucketHarness.CallbackAsync(browser, (await BitbucketHarness.StartAsync(browser, returnUrl: "/projects"))["state"]);

        Assert.Equal("/projects", redirect);
        var exchange = Assert.Single(bitbucket.Requests, r => r.Path == "/site/oauth2/access_token");
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{BitbucketHarness.ClientId}:{BitbucketHarness.ClientSecret}")), exchange.Authorization);
        var form = GitLabHarness.Form(exchange.Body);
        Assert.Equal(("authorization_code", "the-code"), (form["grant_type"], form["code"]));
        Assert.False(form.ContainsKey("client_secret"));
        Assert.Equal("Bearer bb-access-1", Assert.Single(bitbucket.Requests, r => r.Path == "/2.0/user").Authorization);
        Assert.Single(bitbucket.Requests, r => r.Path == "/2.0/user/emails");

        var me = await ProjectApi.ReadAsync<UserDto>(await browser.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct));
        Assert.Equal((email, SystemRole.User), (me.Email, me.SystemRole));
        var instance = await BitbucketHarness.InstanceIdAsync(app);
        var login = await ProjectApi.WithDbAsync(app, db => db.UserExternalLogins.AsNoTracking().SingleAsync(l => l.UserId == me.Id, Ct));
        Assert.Equal((ExternalLoginProvider.Bitbucket, (Guid?)instance, uuid, "ana-bb"), (login.Provider, login.InstanceId, login.ExternalUserId, login.Login));
        var connection = await ProjectApi.WithDbAsync(app, db => db.GitConnections.AsNoTracking().SingleAsync(c => c.UserId == me.Id, Ct));
        Assert.Equal((GitHostKind.Bitbucket, GitConnectionAuthKind.Oauth, GitConnectionStatus.Valid, instance),
            (connection.Provider, connection.AuthKind, connection.Status, connection.InstanceId));
        Assert.Equal("bb-access-1", BitbucketHarness.Decrypt(app, connection.AccessTokenEncrypted));
        Assert.Equal("bb-refresh-1", BitbucketHarness.Decrypt(app, connection.RefreshTokenEncrypted!));
        Assert.Null(connection.TokenEmailEncrypted);
        Assert.True(connection.AccessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(100));
        Assert.Contains("pullrequest:write", connection.Scopes);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains("bb-access-1", StringComparison.Ordinal)
            || e.AllText.Contains("bb-refresh-1", StringComparison.Ordinal)
            || e.AllText.Contains(BitbucketHarness.ClientSecret, StringComparison.Ordinal));

        var connections = await ProjectApi.ReadAsync<List<BitbucketConnectionDto>>(await browser.Client.GetAsync(new Uri("/api/me/connections/bitbucket", UriKind.Relative), Ct));
        Assert.Equal(("ana-bb", uuid, GitConnectionAuthKind.Oauth), (Assert.Single(connections).Login, connections[0].ExternalUserId, connections[0].AuthKind));
    }

    [Fact]
    public async Task FR134_D_V4_18_Signing_in_again_matches_the_account_uuid_not_the_email()
    {
        var uuid = BitbucketHarness.NewUuid();
        var email = ApiSession.NewEmail("bitbucket");
        var app = BitbucketHarness.App(factory, BitbucketHarness.SignInBitbucket(uuid, "ben-bb", email), configuredConsumer: true);
        using (var first = new ApiSession(app))
        {
            await BitbucketHarness.CallbackAsync(first, (await BitbucketHarness.StartAsync(first))["state"]);
        }

        var again = BitbucketHarness.App(factory, BitbucketHarness.SignInBitbucket(uuid, "ben-bb", ApiSession.NewEmail("changed")), configuredConsumer: true);
        using var second = new ApiSession(again);
        Assert.Equal("/", await BitbucketHarness.CallbackAsync(second, (await BitbucketHarness.StartAsync(second))["state"]));

        var me = await ProjectApi.ReadAsync<UserDto>(await second.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct));
        Assert.Equal(email, me.Email);
        Assert.Single(await ProjectApi.WithDbAsync(again, db => db.GitConnections.AsNoTracking().Where(c => c.UserId == me.Id).ToListAsync(Ct)));
    }

    [Fact]
    public async Task FR134_D_V4_18_An_existing_email_is_linked_only_after_its_owner_signs_in_and_confirms()
    {
        var uuid = BitbucketHarness.NewUuid();
        var email = ApiSession.NewEmail("existing");
        var app = BitbucketHarness.App(factory, BitbucketHarness.SignInBitbucket(uuid, "dana-bb", email), configuredConsumer: true);
        using var existing = await ApiSession.RegisterAsync(app, email);
        using var browser = new ApiSession(app);

        var redirect = await BitbucketHarness.CallbackAsync(browser, (await BitbucketHarness.StartAsync(browser))["state"]);

        Assert.StartsWith("/login?link=bitbucket", redirect, StringComparison.Ordinal);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.UserExternalLogins.AnyAsync(l => l.UserId == existing.User!.Id, Ct)));
        await Problems.AssertAsync(await browser.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        var pending = await ProjectApi.ReadAsync<BitbucketPendingLinkDto>(await browser.Client.GetAsync(new Uri("/api/auth/bitbucket/link", UriKind.Relative), Ct));
        Assert.Equal(("dana-bb", email), (pending.Login, pending.Email));

        Assert.Equal(HttpStatusCode.NoContent, (await browser.Client.PostAsJsonAsync("/api/auth/login", new { email, password = AideApiFactory.Password }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Client.PostAsync(new Uri("/api/auth/bitbucket/link", UriKind.Relative), null, Ct)).StatusCode);

        var login = await ProjectApi.WithDbAsync(app, db => db.UserExternalLogins.AsNoTracking().SingleAsync(l => l.UserId == existing.User!.Id, Ct));
        Assert.Equal((ExternalLoginProvider.Bitbucket, uuid), (login.Provider, login.ExternalUserId));
        Assert.True(await ProjectApi.WithDbAsync(app, db => db.GitConnections.AnyAsync(c => c.UserId == existing.User!.Id && c.Provider == GitHostKind.Bitbucket, Ct)));
        await Problems.AssertAsync(await browser.Client.GetAsync(new Uri("/api/auth/bitbucket/link", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR134_A_pending_link_can_t_be_confirmed_by_another_account()
    {
        var email = ApiSession.NewEmail("victim");
        var app = BitbucketHarness.App(factory, BitbucketHarness.SignInBitbucket(BitbucketHarness.NewUuid(), "eve-bb", email), configuredConsumer: true);
        using var victim = await ApiSession.RegisterAsync(app, email);
        using var browser = new ApiSession(app);
        await BitbucketHarness.CallbackAsync(browser, (await BitbucketHarness.StartAsync(browser))["state"]);

        Assert.Equal(HttpStatusCode.Created, (await browser.Client.PostAsJsonAsync("/api/auth/register", new { email = ApiSession.NewEmail("other"), name = "Other", password = AideApiFactory.Password }, Ct)).StatusCode);
        var response = await browser.Client.PostAsync(new Uri("/api/auth/bitbucket/link", UriKind.Relative), null, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.UserExternalLogins.AnyAsync(l => l.UserId == victim.User!.Id, Ct)));
    }

    [Fact]
    public async Task FR134_An_unconfirmed_email_can_t_create_an_account()
    {
        var app = BitbucketHarness.App(factory, BitbucketHarness.SignInBitbucket(BitbucketHarness.NewUuid(), "fay-bb", ApiSession.NewEmail("unconfirmed"), confirmed: false), configuredConsumer: true);
        using var browser = new ApiSession(app);

        var redirect = await BitbucketHarness.CallbackAsync(browser, (await BitbucketHarness.StartAsync(browser))["state"]);

        Assert.Equal($"/login?error={ErrorCodes.GitAuthFailed}&reason=email_required&provider=bitbucket", redirect);
    }

    [Fact]
    public async Task FR134_S8_A_failed_exchange_or_a_replayed_state_redirects_with_GIT_AUTH_FAILED()
    {
        var bitbucket = new FakeGitHubApi().On(HttpMethod.Post, "/site/oauth2/access_token", HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"The code is invalid."}""");
        var app = BitbucketHarness.App(factory, bitbucket, configuredConsumer: true);
        using var browser = new ApiSession(app);
        var state = (await BitbucketHarness.StartAsync(browser))["state"];

        Assert.Equal($"/login?error={ErrorCodes.GitAuthFailed}&reason=token_exchange&provider=bitbucket", await BitbucketHarness.CallbackAsync(browser, state));

        using var replay = new ApiSession(app);
        Assert.StartsWith($"/login?error={ErrorCodes.GitAuthFailed}&reason=state", await BitbucketHarness.CallbackAsync(replay, state), StringComparison.Ordinal);
        Assert.Single(bitbucket.Requests, r => r.Path == "/site/oauth2/access_token");
    }

    [Fact]
    public async Task FR134_Connect_links_the_Bitbucket_identity_to_the_signed_in_user()
    {
        var uuid = BitbucketHarness.NewUuid();
        var app = BitbucketHarness.App(factory, BitbucketHarness.SignInBitbucket(uuid, "gus-bb", ApiSession.NewEmail("elsewhere")), configuredConsumer: true);
        using var user = await ApiSession.RegisterAsync(app);

        var redirect = await BitbucketHarness.CallbackAsync(user, (await BitbucketHarness.StartAsync(user, mode: "connect", returnUrl: "/account/connections"))["state"]);

        Assert.Equal("/account/connections", redirect);
        var connection = await ProjectApi.WithDbAsync(app, db => db.GitConnections.AsNoTracking().SingleAsync(c => c.UserId == user.User!.Id, Ct));
        Assert.Equal((uuid, GitConnectionAuthKind.Oauth), (connection.ExternalUserId, connection.AuthKind));

        using var anonymous = new ApiSession(app);
        var refused = await anonymous.Client.GetAsync(new Uri("/api/auth/bitbucket/start?mode=connect", UriKind.Relative), Ct);
        await Problems.AssertAsync(refused, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }
}
