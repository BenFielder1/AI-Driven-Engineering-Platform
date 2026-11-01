using System.Net;
using System.Text;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Bitbucket;
using Aide.Infrastructure.Git;

namespace Aide.UnitTests.GitHosts;

/// <summary>FR-133–FR-136, §4.19: the Bitbucket client against a stub handler (no real Bitbucket), the limiter and the Git header.</summary>
public class BitbucketClientTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (IBitbucketClients Clients, StubHandler Handler, BitbucketRateLimiter Limiter, ManualClock Clock) Create(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var clock = new ManualClock(Start);
        var limiter = new BitbucketRateLimiter(clock);
        var clients = new BitbucketClients(new StubFactory(handler), new StaticMonitor<BitbucketOptions>(new BitbucketOptions()), limiter, clock);
        return (clients, handler, limiter, clock);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    [Fact]
    public async Task FR134_An_API_token_authenticates_with_Basic_email_token_and_OAuth_with_Bearer()
    {
        var (clients, handler, _, _) = Create((_, _) => Json(HttpStatusCode.OK, """{"uuid":"{u-1}","username":"ana","display_name":"Ana"}"""));
        var client = clients.Create();

        var user = await client.GetUserAsync(BitbucketAuth.ApiToken("ana@example.com", "ATATT-token"), Ct);
        await client.GetUserAsync(BitbucketAuth.OAuth("oauth-token"), Ct);

        Assert.Equal(("{u-1}", "ana"), (user.Uuid, user.Login));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ana@example.com:ATATT-token")), handler.Requests[0].Authorization);
        Assert.Equal("Bearer oauth-token", handler.Requests[1].Authorization);
        Assert.All(handler.Requests, r => Assert.StartsWith("https://api.bitbucket.org/2.0/user", r.Url, StringComparison.Ordinal));
        Assert.DoesNotContain("ATATT", BitbucketAuth.ApiToken("ana@example.com", "ATATT-token").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR140_A_202_merge_returns_the_task_status_path_and_the_task_answer_is_read()
    {
        const string location = "https://api.bitbucket.org/2.0/repositories/acme/todo/pullrequests/3/merge/task-status/t-1";
        var (clients, handler, _, _) = Create((r, _) => r.Method == HttpMethod.Post
            ? Json(HttpStatusCode.Accepted, "{}", ("Location", location))
            : Json(HttpStatusCode.OK, """{"task_status":"SUCCESS","merge_result":{"id":3,"state":"MERGED","merge_commit":{"hash":"abcdef012345"}}}"""));
        var client = clients.Create(Guid.NewGuid());
        var auth = BitbucketAuth.OAuth("t");

        var answer = await client.MergePullRequestAsync(auth, "acme/todo", 3, "merge_commit", "msg", Ct);
        Assert.Equal((null, "repositories/acme/todo/pullrequests/3/merge/task-status/t-1"), (answer.PullRequest, answer.TaskPath));
        var task = await client.GetMergeTaskAsync(auth, answer.TaskPath!, Ct);

        Assert.Equal((BitbucketMergeTaskState.Succeeded, "abcdef012345"), (task.State, task.Result!.MergeCommit));
        Assert.Contains("\"merge_strategy\":\"merge_commit\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"close_source_branch\":false", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal(location, handler.Requests[1].Url);
    }

    [Fact]
    public async Task FR140_A_555_merge_timeout_throws_with_its_status()
    {
        var (clients, _, _, _) = Create((_, _) => Json((HttpStatusCode)555, """{"type":"error","error":{"message":"timeout"}}"""));
        var ex = await Assert.ThrowsAsync<BitbucketApiException>(() => clients.Create().MergePullRequestAsync(BitbucketAuth.OAuth("t"), "acme/todo", 3, "squash", "m", Ct));
        Assert.Equal(555, ex.Status);
    }

    [Fact]
    public async Task FR138_Mergeability_checks_are_parsed_and_an_unusable_answer_is_a_failure()
    {
        var garbled = false;
        var (clients, _, _, _) = Create((_, _) => Json(HttpStatusCode.OK, garbled
            ? """{"unexpected":true}"""
            : """{"values":[{"type":"standard_merge_check","key":"require_passing_builds_to_merge","status":"failed","required":true,"blocking":true}]}"""));
        var client = clients.Create();

        var checks = await client.GetMergeabilityChecksAsync(BitbucketAuth.OAuth("t"), "acme/todo", 3, Ct);
        Assert.Equal(new MergeabilityCheck("standard_merge_check", "require_passing_builds_to_merge", "FAILED", true, true), Assert.Single(checks!));

        garbled = true;
        Assert.Equal(200, (await Assert.ThrowsAsync<BitbucketApiException>(() => client.GetMergeabilityChecksAsync(BitbucketAuth.OAuth("t"), "acme/todo", 3, Ct))).Status);
    }

    [Theory]
    [InlineData("https://api.bitbucket.org/2.0/repositories/a/b/src/c/?page=x", "repositories/a/b/src/c/?page=x")]
    [InlineData("https://evil.example/2.0/repositories/a/b", null)]
    [InlineData("http://api.bitbucket.org/2.0/repositories/a/b", null)]
    [InlineData("https://api.bitbucket.org/1.0/x", null)]
    public void FR141_Links_are_followed_only_on_the_API_host(string link, string? expected) =>
        Assert.Equal(expected, BitbucketClient.ToRelative(link));

    [Fact]
    public async Task FR134_The_code_exchange_uses_client_Basic_auth_on_bitbucket_org()
    {
        var (clients, handler, _, _) = Create((_, _) => Json(HttpStatusCode.OK,
            """{"access_token":"at-1","refresh_token":"rt-1","expires_in":7200,"scopes":"account email repository:write","token_type":"bearer"}"""));

        var tokens = await clients.Create().ExchangeCodeAsync("key", "secret", "the-code", Ct);

        Assert.Equal(("at-1", "rt-1", 7200L), (tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresIn!.Value));
        Assert.Equal(["account", "email", "repository:write"], tokens.Scopes);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://bitbucket.org/site/oauth2/access_token", request.Url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("key:secret")), request.Authorization);
        Assert.Contains("grant_type=authorization_code", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", tokens.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FR134_The_authorize_URL_is_on_bitbucket_org_with_the_state()
    {
        var url = BitbucketClient.AuthorizeUrl("key", "st/ate");

        Assert.Equal("https://bitbucket.org/site/oauth2/authorize?client_id=key&response_type=code&state=st%2Fate", url.AbsoluteUri);
    }

    [Fact]
    public async Task FR134_A_scope_error_says_so_and_lists_the_granted_scopes()
    {
        var (clients, _, _, _) = Create((_, _) => Json(HttpStatusCode.Forbidden,
            """{"type":"error","error":{"message":"Your credentials lack one or more required privilege scopes.","detail":{"granted":["read:user:bitbucket"],"required":["write:pullrequest:bitbucket"]}}}"""));

        var probe = await clients.Create().ProbeAsync(BitbucketAuth.ApiToken("a@example.com", "t"), HttpMethod.Post, "repositories/acme/todo/pullrequests", Ct);

        Assert.Equal((403, true), (probe.Status, probe.ScopeError));
        Assert.Equal(["read:user:bitbucket"], probe.GrantedScopes);
    }

    [Fact]
    public async Task FR134_A_permission_403_is_not_a_scope_error()
    {
        var (clients, _, _, _) = Create((_, _) => Json(HttpStatusCode.Forbidden, """{"type":"error","error":{"message":"You don't have admin access to this repository."}}"""));

        var probe = await clients.Create().ProbeAsync(BitbucketAuth.ApiToken("a@example.com", "t"), HttpMethod.Get, "repositories/acme/todo/branch-restrictions", Ct);

        Assert.False(probe.ScopeError);
        Assert.Equal(BitbucketScopeProbe.Unverified, BitbucketRules.ClassifyProbe(probe.Status, probe.ScopeError));
    }

    [Fact]
    public async Task FR135_Repositories_are_read_with_role_contributor_the_query_and_newest_first()
    {
        var (clients, handler, _, _) = Create((_, _) => Json(HttpStatusCode.OK,
            """{"values":[{"uuid":"{r-1}","full_name":"acme/todo-app","name":"todo-app","slug":"todo-app","is_private":true,"workspace":{"slug":"acme"},"project":{"key":"TODO"},"mainbranch":{"name":"main"},"links":{"html":{"href":"https://bitbucket.org/acme/todo-app"}},"updated_on":"2026-10-28T10:00:00.000000+00:00"}],"next":"https://api.bitbucket.org/2.0/repositories/acme?page=2"}"""));

        var page = await clients.Create().ListRepositoriesAsync(BitbucketAuth.OAuth("t"), "acme", BitbucketRules.RepoQuery("todo"), 1, Ct);

        var repo = Assert.Single(page.Items);
        Assert.True(page.HasNext);
        Assert.Equal(("acme/todo-app", "TODO", "main", "acme"), (repo.FullName, repo.ProjectKey, repo.MainBranch, repo.WorkspaceSlug));
        var url = Uri.UnescapeDataString(handler.Requests[0].Url);
        Assert.Contains("repositories/acme?role=contributor&sort=-updated_on&pagelen=30&page=1&q=name~\"todo\"", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S4_19_The_connection_budget_refuses_without_calling_Bitbucket_once_900_requests_are_used()
    {
        var (clients, handler, _, clock) = Create((_, _) => Json(HttpStatusCode.OK, """{"uuid":"{u-1}"}"""));
        var connectionId = Guid.NewGuid();
        var client = clients.Create(connectionId);
        for (var i = 0; i < GitHostPollBudget.BitbucketBackgroundPerHour; i++)
        {
            await client.GetUserAsync(BitbucketAuth.OAuth("t"), Ct);
        }

        var refused = await Assert.ThrowsAsync<BitbucketApiException>(() => client.GetUserAsync(BitbucketAuth.OAuth("t"), Ct));

        Assert.Equal((429, true), (refused.Status, refused.LocalBudget));
        Assert.Equal(TimeSpan.FromSeconds(4), refused.RetryAfter);
        Assert.Equal(GitHostPollBudget.BitbucketBackgroundPerHour, handler.Requests.Count);

        // Another connection has its own bucket; the first one refills over time.
        await clients.Create(Guid.NewGuid()).GetUserAsync(BitbucketAuth.OAuth("t"), Ct);
        clock.Advance(TimeSpan.FromSeconds(5));
        await client.GetUserAsync(BitbucketAuth.OAuth("t"), Ct);
    }

    [Fact]
    public async Task S4_19_A_Bitbucket_429_holds_the_connection_back_until_Retry_After()
    {
        var calls = 0;
        var (clients, handler, _, clock) = Create((_, _) => ++calls == 1
            ? Json(HttpStatusCode.TooManyRequests, """{"type":"error","error":{"message":"Rate limit for this resource has been exceeded"}}""", ("Retry-After", "60"))
            : Json(HttpStatusCode.OK, """{"uuid":"{u-1}"}"""));
        var connectionId = Guid.NewGuid();

        var first = await Assert.ThrowsAsync<BitbucketApiException>(() => clients.Create(connectionId).GetUserAsync(BitbucketAuth.OAuth("t"), Ct));
        var held = await Assert.ThrowsAsync<BitbucketApiException>(() => clients.Create(connectionId).GetUserAsync(BitbucketAuth.OAuth("t"), Ct));

        Assert.Equal((429, false, TimeSpan.FromSeconds(60)), (first.Status, first.LocalBudget, first.RetryAfter));
        Assert.True(held.LocalBudget);
        Assert.Single(handler.Requests);
        clock.Advance(TimeSpan.FromSeconds(65));
        await clients.Create(connectionId).GetUserAsync(BitbucketAuth.OAuth("t"), Ct);
    }

    [Fact]
    public async Task S4_19_FR134_Token_checks_take_from_a_budget_per_AIDE_user_so_repeated_saves_are_throttled()
    {
        var (clients, handler, _, _) = Create((_, _) => Json(HttpStatusCode.OK, """{"uuid":"{u-1}"}"""));
        var userId = Guid.NewGuid();
        for (var i = 0; i < GitHostPollBudget.BitbucketBackgroundPerHour; i++)
        {
            // Each save makes a new client; the bucket is the user's, not the client's.
            await clients.CreateForUser(userId).GetUserAsync(BitbucketAuth.ApiToken("a@example.com", "t"), Ct);
        }

        var refused = await Assert.ThrowsAsync<BitbucketApiException>(() => clients.CreateForUser(userId).GetUserAsync(BitbucketAuth.ApiToken("a@example.com", "t"), Ct));

        Assert.Equal((429, true), (refused.Status, refused.LocalBudget));
        Assert.Equal(GitHostPollBudget.BitbucketBackgroundPerHour, handler.Requests.Count);
        await clients.CreateForUser(Guid.NewGuid()).GetUserAsync(BitbucketAuth.ApiToken("b@example.com", "t"), Ct);
        await clients.Create(userId).GetUserAsync(BitbucketAuth.OAuth("t"), Ct);
    }

    [Theory]
    [InlineData(GitCredentialKind.BitbucketOauth, "x-token-auth")]
    [InlineData(GitCredentialKind.BitbucketApiToken, "x-bitbucket-api-token-auth")]
    public void FR136_The_Bitbucket_token_goes_only_in_the_per_command_extraHeader(GitCredentialKind credential, string user)
    {
        var remote = GitHostRemotes.Create(new Uri(BitbucketRules.WebBaseUrl), "acme/todo-app", credential, "secret-token", GitTransportOverride.None);

        Assert.Equal("https://bitbucket.org/acme/todo-app.git", remote.Url);
        Assert.DoesNotContain("secret-token", remote.Url, StringComparison.Ordinal);
        var header = remote.ConfigEnvironment.Single(e => e.Value.StartsWith("Authorization: Basic ", StringComparison.Ordinal));
        Assert.Equal("GIT_CONFIG_VALUE_0", header.Key);
        Assert.Equal("http.https://bitbucket.org/.extraHeader", remote.ConfigEnvironment["GIT_CONFIG_KEY_0"]);
        Assert.Equal($"{user}:secret-token", Encoding.UTF8.GetString(Convert.FromBase64String(header.Value["Authorization: Basic ".Length..])));
        Assert.Contains(remote.ConfigEnvironment, e => e.Value == "http.followRedirects");
        Assert.Equal("***", remote.Redact("secret-token"));
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
