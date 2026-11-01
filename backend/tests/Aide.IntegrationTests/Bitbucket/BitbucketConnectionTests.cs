using System.Net;
using System.Net.Http.Json;
using System.Text;
using Aide.Api.Features.Bitbucket;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Security;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Bitbucket;

/// <summary>
/// FR-134 token fallback and token lifecycle against a stub Bitbucket: the API token's Basic <c>email:token</c>, the scope
/// check with harmless calls (missing scopes listed, nothing saved), encryption, refresh persisting whatever refresh token
/// comes back, and <c>invalid</c> after a second 401 with <c>connection_invalid</c>.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class BitbucketConnectionTests(AideApiFactory factory)
{
    private const string Token = "ATATT3xFfGF0-bitbucket-api-token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Basic(string email, string token) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}"));

    [Fact]
    public async Task FR134_Given_an_API_token_without_write_pullrequest_then_CONNECTION_SCOPE_INSUFFICIENT_lists_it_and_nothing_is_saved()
    {
        var bitbucket = BitbucketHarness.TokenBitbucket(BitbucketHarness.NewUuid())
            .On(HttpMethod.Post, "/2.0/repositories/acme/todo/pullrequests", HttpStatusCode.Forbidden, BitbucketHarness.ScopeError);
        var app = BitbucketHarness.App(factory, bitbucket);
        using var user = await ApiSession.RegisterAsync(app);

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/bitbucket", new { token = Token, email = "ana@example.com" }, Ct);

        var problem = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ConnectionScopeInsufficient);
        Assert.Equal(["write:pullrequest:bitbucket"], problem.GetProperty("details").EnumerateArray().Select(e => e.GetString()));
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.GitConnections.AnyAsync(c => c.UserId == user.User!.Id, Ct)));
        // The write probes are deliberately invalid, so nothing can be created.
        Assert.Contains(bitbucket.Requests, r => r.Method == "POST" && r.Path.Contains("aide-scope-check", StringComparison.Ordinal)
            && r.Body.Contains("\"scm\":\"aide-scope-check\"", StringComparison.Ordinal));
        Assert.All(bitbucket.Requests.Where(r => r.Method == "POST" && r.Path.EndsWith("/pullrequests", StringComparison.Ordinal)), r => Assert.Equal("{}", r.Body));
    }

    [Fact]
    public async Task FR134_A_token_whose_repository_scopes_can_t_be_checked_is_refused_with_them_listed_as_unverifiable()
    {
        // No repository yet: read:pipeline, admin:repository and write:pullrequest have nothing to be tried on.
        var bitbucket = BitbucketHarness.TokenBitbucket(BitbucketHarness.NewUuid())
            .On(HttpMethod.Get, "/2.0/repositories/acme", HttpStatusCode.OK, """{"values":[]}""");
        var app = BitbucketHarness.App(factory, bitbucket);
        using var user = await ApiSession.RegisterAsync(app);

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/bitbucket", new { token = Token, email = "ana@example.com" }, Ct);

        var problem = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ConnectionScopeInsufficient);
        Assert.False(problem.TryGetProperty("details", out _));
        Assert.Equal(["admin:repository:bitbucket", "write:pullrequest:bitbucket", "read:pipeline:bitbucket"],
            problem.GetProperty("unverifiableScopes").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("Connect with Bitbucket", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.GitConnections.AnyAsync(c => c.UserId == user.User!.Id, Ct)));
    }

    [Fact]
    public async Task FR134_An_OAuth_style_scope_list_is_ignored_and_the_scopes_are_probed()
    {
        var uuid = BitbucketHarness.NewUuid();
        var bitbucket = BitbucketHarness.TokenBitbucket(uuid);
        bitbucket.On(HttpMethod.Get, "/2.0/user", HttpStatusCode.OK, BitbucketHarness.UserJson(uuid, "ana"),
            new Dictionary<string, string> { ["X-OAuth-Scopes"] = "account, repository:write, pullrequest:write" });
        var app = BitbucketHarness.App(factory, bitbucket);
        using var user = await ApiSession.RegisterAsync(app);

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/bitbucket", new { token = Token, email = "ana@example.com" }, Ct);

        var dto = await ProjectApi.ReadAsync<BitbucketConnectionDto>(response, HttpStatusCode.Created);
        Assert.Equal(BitbucketHarness.AllScopes, dto.Scopes);
        Assert.Contains(bitbucket.Requests, r => r.Path.EndsWith("/branch-restrictions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR134_Bitbucket_s_scope_list_decides_when_it_sends_one()
    {
        var granted = string.Join(' ', BitbucketHarness.AllScopes.Where(s => s != "read:pipeline:bitbucket"));
        var bitbucket = BitbucketHarness.TokenBitbucket(BitbucketHarness.NewUuid());
        bitbucket.On(HttpMethod.Get, "/2.0/user", HttpStatusCode.OK, BitbucketHarness.UserJson(BitbucketHarness.NewUuid(), "ana"), new Dictionary<string, string> { ["X-OAuth-Scopes"] = granted });
        var app = BitbucketHarness.App(factory, bitbucket);
        using var user = await ApiSession.RegisterAsync(app);

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/bitbucket", new { token = Token, email = "ana@example.com" }, Ct);

        var problem = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ConnectionScopeInsufficient);
        Assert.Equal(["read:pipeline:bitbucket"], problem.GetProperty("details").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(bitbucket.Requests, r => r.Path.StartsWith("/2.0/repositories", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR134_A_valid_API_token_is_saved_encrypted_with_its_email_and_never_returned()
    {
        var uuid = BitbucketHarness.NewUuid();
        var bitbucket = BitbucketHarness.TokenBitbucket(uuid);
        var app = BitbucketHarness.App(factory, bitbucket);
        using var user = await ApiSession.RegisterAsync(app);
        var email = ApiSession.NewEmail("atlassian");

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/bitbucket", new { token = Token, email }, Ct);

        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(email, text, StringComparison.Ordinal);
        var dto = System.Text.Json.JsonSerializer.Deserialize<BitbucketConnectionDto>(text, ApiSession.Json)!;
        Assert.Equal(("ana", uuid, GitConnectionAuthKind.Token, GitConnectionStatus.Valid), (dto.Login, dto.ExternalUserId, dto.AuthKind, dto.Status));
        Assert.Equal(BitbucketHarness.AllScopes, dto.Scopes);
        Assert.All(bitbucket.Requests, r => Assert.Equal(Basic(email, Token), r.Authorization));

        var row = await ProjectApi.WithDbAsync(app, db => db.GitConnections.AsNoTracking().SingleAsync(c => c.Id == dto.Id, Ct));
        Assert.Equal((GitHostKind.Bitbucket, await BitbucketHarness.InstanceIdAsync(app)), (row.Provider, row.InstanceId));
        Assert.Equal(Token, BitbucketHarness.Decrypt(app, row.AccessTokenEncrypted));
        Assert.Equal(email, BitbucketHarness.Decrypt(app, row.TokenEmailEncrypted!, SecretPurposes.GitHostTokenEmail));
        Assert.DoesNotContain(Token, row.AccessTokenEncrypted, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR134_A_token_Bitbucket_rejects_is_CREDENTIAL_INVALID_and_bad_input_is_VALIDATION()
    {
        var bitbucket = new FakeGitHubApi().On(HttpMethod.Get, "/2.0/user", HttpStatusCode.Unauthorized, """{"type":"error","error":{"message":"Unauthorized"}}""");
        var app = BitbucketHarness.App(factory, bitbucket);
        using var user = await ApiSession.RegisterAsync(app);

        var rejected = await user.Client.PostAsJsonAsync("/api/me/connections/bitbucket", new { token = Token, email = "ana@example.com" }, Ct);
        var invalid = await user.Client.PostAsJsonAsync("/api/me/connections/bitbucket", new { token = Token, email = "not-an-email" }, Ct);

        await Problems.AssertAsync(rejected, HttpStatusCode.BadRequest, ErrorCodes.CredentialInvalid);
        var problem = await Problems.AssertAsync(invalid, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(problem.GetProperty("errors").TryGetProperty("email", out _));
        Assert.Single(bitbucket.Requests);
    }

    [Fact]
    public async Task FR134_Connection_mutations_need_a_session_and_others_connections_are_404()
    {
        var app = BitbucketHarness.App(factory, new FakeGitHubApi());
        using var owner = await ApiSession.RegisterAsync(app);
        using var other = await ApiSession.RegisterAsync(app);
        using var anonymous = new ApiSession(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, owner.User!.Id);

        await Problems.AssertAsync(await anonymous.Client.GetAsync(new Uri("/api/me/connections/bitbucket", UriKind.Relative), Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        await Problems.AssertAsync(await other.Client.DeleteAsync(new Uri($"/api/me/connections/bitbucket/{connection}", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await other.Client.PostAsync(new Uri($"/api/me/connections/bitbucket/{connection}/test", UriKind.Relative), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await other.Client.GetAsync(new Uri($"/api/me/git/bitbucket/repos?connectionId={connection}", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Empty(await ProjectApi.ReadAsync<List<BitbucketConnectionDto>>(await other.Client.GetAsync(new Uri("/api/me/connections/bitbucket", UriKind.Relative), Ct)));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync(new Uri($"/api/me/connections/bitbucket/{connection}", UriKind.Relative), Ct)).StatusCode);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.GitConnections.AnyAsync(c => c.Id == connection, Ct)));
    }

    [Fact]
    public async Task FR134_An_expiring_OAuth_token_is_refreshed_and_whatever_refresh_token_comes_back_is_persisted()
    {
        var bitbucket = new FakeGitHubApi()
            .On(HttpMethod.Post, "/site/oauth2/access_token", HttpStatusCode.OK, """{"access_token":"bb-access-2","expires_in":7200,"refresh_token":"bb-refresh-2","scopes":"account repository:write"}""")
            .On(HttpMethod.Get, "/2.0/user/workspaces", HttpStatusCode.OK, BitbucketHarness.WorkspacesJson("acme"))
            .On(HttpMethod.Get, "/2.0/repositories/acme", HttpStatusCode.OK, BitbucketHarness.ReposJson(("acme", "todo")));
        var app = BitbucketHarness.App(factory, bitbucket, configuredConsumer: true);
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, user.User!.Id, "bb-access-1", "bb-refresh-1", DateTimeOffset.UtcNow.AddMinutes(1));

        var page = await ProjectApi.ReadAsync<BitbucketRepoPageDto>(await user.Client.GetAsync(new Uri($"/api/me/git/bitbucket/repos?connectionId={connection}", UriKind.Relative), Ct));

        Assert.Equal("acme/todo", Assert.Single(page.Items).FullName);
        var refresh = Assert.Single(bitbucket.Requests, r => r.Path == "/site/oauth2/access_token");
        Assert.Equal(("refresh_token", "bb-refresh-1"), (GitLabHarness.Form(refresh.Body)["grant_type"], GitLabHarness.Form(refresh.Body)["refresh_token"]));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{BitbucketHarness.ClientId}:{BitbucketHarness.ClientSecret}")), refresh.Authorization);
        Assert.All(bitbucket.Requests.Where(r => r.Path.StartsWith("/2.0/", StringComparison.Ordinal)), r => Assert.Equal("Bearer bb-access-2", r.Authorization));
        var row = await ProjectApi.WithDbAsync(app, db => db.GitConnections.AsNoTracking().SingleAsync(c => c.Id == connection, Ct));
        Assert.Equal(("bb-access-2", "bb-refresh-2"), (BitbucketHarness.Decrypt(app, row.AccessTokenEncrypted), BitbucketHarness.Decrypt(app, row.RefreshTokenEncrypted!)));
        Assert.True(row.AccessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(100));
    }

    [Fact]
    public async Task FR134_A_401_after_one_refresh_marks_the_connection_invalid_and_notifies_its_owner()
    {
        var bitbucket = new FakeGitHubApi()
            .On(HttpMethod.Post, "/site/oauth2/access_token", HttpStatusCode.OK, """{"access_token":"bb-access-2","expires_in":7200,"refresh_token":"bb-refresh-2"}""")
            .On(HttpMethod.Get, "/2.0/user/workspaces", HttpStatusCode.Unauthorized, """{"type":"error","error":{"message":"Access token expired."}}""");
        var app = BitbucketHarness.App(factory, bitbucket, configuredConsumer: true);
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, user.User!.Id, "bb-access-1", "bb-refresh-1", DateTimeOffset.UtcNow.AddHours(1));

        var response = await user.Client.GetAsync(new Uri($"/api/me/git/bitbucket/repos?connectionId={connection}", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.GitNotConnected);
        Assert.Single(bitbucket.Requests, r => r.Path == "/site/oauth2/access_token");
        var row = await ProjectApi.WithDbAsync(app, db => db.GitConnections.AsNoTracking().SingleAsync(c => c.Id == connection, Ct));
        Assert.Equal(GitConnectionStatus.Invalid, row.Status);
        Assert.Equal("bb-refresh-2", BitbucketHarness.Decrypt(app, row.RefreshTokenEncrypted!));
        var events = await ProjectApi.WithDbAsync(app, db => db.Events.AsNoTracking().Where(e => e.Type == EventTypes.ConnectionInvalid).ToListAsync(Ct));
        Assert.Contains(events, e => e.Payload.Contains(connection.ToString(), StringComparison.Ordinal) && e.Payload.Contains("\"bitbucket\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR134_An_API_token_rejected_by_Bitbucket_is_invalid_at_once()
    {
        var bitbucket = new FakeGitHubApi().On(HttpMethod.Get, "/2.0/user", HttpStatusCode.Unauthorized, """{"type":"error","error":{"message":"Unauthorized"}}""");
        var app = BitbucketHarness.App(factory, bitbucket);
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, user.User!.Id, Token, authKind: GitConnectionAuthKind.Token, email: "ana@example.com");

        var tested = await ProjectApi.ReadAsync<BitbucketConnectionDto>(await user.Client.PostAsync(new Uri($"/api/me/connections/bitbucket/{connection}/test", UriKind.Relative), null, Ct));

        Assert.Equal(GitConnectionStatus.Invalid, tested.Status);
        Assert.Equal(Basic("ana@example.com", Token), Assert.Single(bitbucket.Requests).Authorization);
    }

    [Fact]
    public async Task FR134_Testing_an_API_token_that_lost_a_scope_marks_it_insufficient_scope()
    {
        var bitbucket = BitbucketHarness.TokenBitbucket(BitbucketHarness.NewUuid())
            .On(HttpMethod.Get, "/2.0/repositories/acme/todo/pipelines", HttpStatusCode.Forbidden, BitbucketHarness.ScopeError);
        var app = BitbucketHarness.App(factory, bitbucket);
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, user.User!.Id, Token, authKind: GitConnectionAuthKind.Token, email: "ana@example.com");

        var tested = await ProjectApi.ReadAsync<BitbucketConnectionDto>(await user.Client.PostAsync(new Uri($"/api/me/connections/bitbucket/{connection}/test", UriKind.Relative), null, Ct));

        Assert.Equal(GitConnectionStatus.InsufficientScope, tested.Status);
    }
}
