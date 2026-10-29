using System.Net;
using System.Text;
using System.Text.Json;
using Aide.Infrastructure.GitLab;
using Microsoft.Extensions.Options;

namespace Aide.UnitTests.GitHosts;

/// <summary>§8 SSRF guard at connect time (DNS-rebinding), and the GitLab client against a stub handler (no real GitLab).</summary>
public class GitHostConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GitHostConnector Connector(bool allowPrivate, params (string Host, string[] Addresses)[] names) =>
        new(new StubDns(names), new StaticMonitor<GitLabOptions>(new GitLabOptions { AllowPrivateNetworks = allowPrivate }));

    [Fact]
    public async Task S8_FR130_The_metadata_address_is_refused_even_with_private_networks_allowed()
    {
        var connector = Connector(true, ("metadata.example.com", ["169.254.169.254"]));

        await Assert.ThrowsAsync<GitHostBlockedException>(() => connector.ResolveAllowedAsync("169.254.169.254", Ct));
        await Assert.ThrowsAsync<GitHostBlockedException>(() => connector.ResolveAllowedAsync("metadata.example.com", Ct));
    }

    [Fact]
    public async Task S8_FR130_A_name_resolving_to_10_0_4_7_is_allowed_only_with_private_networks()
    {
        var name = ("gitlab.internal.example", new[] { "10.0.4.7" });

        var allowed = await Connector(true, name).ResolveAllowedAsync("gitlab.internal.example", Ct);
        Assert.Equal(IPAddress.Parse("10.0.4.7"), Assert.Single(allowed));
        await Assert.ThrowsAsync<GitHostBlockedException>(() => Connector(false, name).ResolveAllowedAsync("gitlab.internal.example", Ct));
    }

    [Fact]
    public async Task S8_DNS_rebinding_a_name_with_any_refused_address_is_refused()
    {
        var connector = Connector(true, ("rebind.example.com", ["20.1.2.3", "127.0.0.1"]));

        await Assert.ThrowsAsync<GitHostBlockedException>(() => connector.ResolveAllowedAsync("rebind.example.com", Ct));
    }

    [Fact]
    public async Task S8_Localhost_and_railway_internal_names_are_refused_before_resolving()
    {
        var dns = new StubDns(("gitlab.railway.internal", ["10.1.1.1"]));
        var connector = new GitHostConnector(dns, new StaticMonitor<GitLabOptions>(new GitLabOptions { AllowPrivateNetworks = false }));

        await Assert.ThrowsAsync<GitHostBlockedException>(() => connector.ResolveAllowedAsync("localhost", Ct));
        await Assert.ThrowsAsync<GitHostBlockedException>(() => connector.ResolveAllowedAsync("gitlab.railway.internal", Ct));
        Assert.Equal(0, dns.Lookups);
    }

    [Fact]
    public async Task S8_A_public_host_is_allowed()
    {
        var addresses = await Connector(false, ("gitlab.example.com", ["20.1.2.3"])).ResolveAllowedAsync("gitlab.example.com", Ct);

        Assert.Equal(IPAddress.Parse("20.1.2.3"), Assert.Single(addresses));
    }

    [Fact]
    public void S8_The_GitLab_handler_never_follows_redirects_or_uses_a_proxy()
    {
        using var handler = Connector(false).CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public void FR130_Configured_instances_must_pass_the_SSRF_guard()
    {
        var validator = new GitLabOptionsValidator();

        Assert.True(validator.Validate(null, new GitLabOptions { Instances = [new() { BaseUrl = "" }, new() { BaseUrl = "https://gitlab.acme.example" }] }).Succeeded);
        Assert.True(validator.Validate(null, new GitLabOptions { Instances = [new() { BaseUrl = "https://169.254.169.254" }] }).Failed);
        Assert.True(validator.Validate(null, new GitLabOptions { Instances = [new() { BaseUrl = "http://gitlab.acme.example" }] }).Failed);
        Assert.True(validator.Validate(null, new GitLabOptions { Instances = [new() { BaseUrl = "" }, new() { BaseUrl = "https://gitlab.com/" }] }).Failed);
    }
}

public class GitLabClientTests
{
    private static readonly Uri Base = new("https://gitlab.acme.example/gitlab");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (GitLabClient Client, StubHandler Handler) Client(Func<HttpRequestMessage, string, HttpResponseMessage> respond, TimeProvider? time = null)
    {
        var handler = new StubHandler(respond);
        var clients = new GitLabClients(new StubFactory(handler), new StaticMonitor<GitLabOptions>(new GitLabOptions()), time ?? TimeProvider.System);
        return (clients.ForInstance(Base), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task FR130_Version_200_with_a_version_and_401_with_GitLabs_error_shape_are_reachable()
    {
        var (ok, _) = Client((_, _) => Json(HttpStatusCode.OK, """{"version":"17.11.1-ee","revision":"abc"}"""));
        var (unauth, handler) = Client((_, _) => Json(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}"""));
        var (html, _) = Client((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") });
        var (redirect, _) = Client((_, _) => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/") } });

        Assert.Equal(new GitLabVersionProbe(true, "17.11.1-ee", 200), await ok.ProbeVersionAsync(Ct));
        Assert.Equal(new GitLabVersionProbe(true, null, 401), await unauth.ProbeVersionAsync(Ct));
        Assert.False((await html.ProbeVersionAsync(Ct)).Reachable);
        Assert.False((await redirect.ProbeVersionAsync(Ct)).Reachable);
        Assert.Equal("https://gitlab.acme.example/gitlab/api/v4/version", Assert.Single(handler.Requests).Url);
        Assert.Null(handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task FR131_The_code_exchange_sends_the_PKCE_verifier_and_returns_the_rotating_pair()
    {
        var (client, handler) = Client((_, _) => Json(HttpStatusCode.OK,
            """{"access_token":"gla-access","token_type":"Bearer","expires_in":7200,"refresh_token":"glr-refresh","scope":"api write_repository openid profile email","created_at":1}"""));

        var tokens = await client.ExchangeCodeAsync("app-id", "app-secret", "the-code", "https://aide.example/api/auth/gitlab/callback", "verifier-123", Ct);

        Assert.Equal(("gla-access", "glr-refresh", 7200L), (tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresIn!.Value));
        Assert.Contains("write_repository", tokens.Scopes);
        Assert.DoesNotContain("gla-access", tokens.ToString(), StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(("POST", "https://gitlab.acme.example/gitlab/oauth/token"), (request.Method, request.Url));
        Assert.Contains("grant_type=authorization_code", request.Body, StringComparison.Ordinal);
        Assert.Contains("code_verifier=verifier-123", request.Body, StringComparison.Ordinal);
        Assert.Contains("code=the-code", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR131_A_refused_refresh_carries_GitLabs_oauth_error()
    {
        var (client, _) = Client((_, _) => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"The provided authorization grant is invalid"}"""));

        var ex = await Assert.ThrowsAsync<GitLabApiException>(() => client.RefreshAsync("id", "secret", "old-refresh", "https://aide.example/cb", Ct));

        Assert.Equal((400, "invalid_grant"), (ex.Status, ex.OauthError));
    }

    [Fact]
    public void FR131_The_authorize_URL_asks_for_code_with_PKCE_S256_and_the_scopes()
    {
        var (client, _) = Client((_, _) => Json(HttpStatusCode.OK, "{}"));

        var url = client.AuthorizeUrl("app-id", "https://aide.example/api/auth/gitlab/callback", "st", "challenge");

        Assert.StartsWith("https://gitlab.acme.example/gitlab/oauth/authorize?", url.AbsoluteUri, StringComparison.Ordinal);
        var query = Uri.UnescapeDataString(url.Query);
        Assert.Contains("response_type=code", query, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", query, StringComparison.Ordinal);
        Assert.Contains("code_challenge=challenge", query, StringComparison.Ordinal);
        Assert.Contains("scope=api write_repository openid profile email", query, StringComparison.Ordinal);
        Assert.Contains("state=st", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR132_Projects_are_listed_with_membership_developer_access_and_keyset_cursor()
    {
        var (client, handler) = Client((_, _) =>
        {
            var response = Json(HttpStatusCode.OK, """[{"id":42,"name":"todo","path":"todo","path_with_namespace":"acme/todo","default_branch":"main","web_url":"https://gitlab.acme.example/acme/todo","namespace":{"full_path":"acme"},"last_activity_at":"2026-10-01T10:00:00Z"}]""");
            response.Headers.Add("Link", "<https://gitlab.acme.example/gitlab/api/v4/projects?id_before=42&membership=true&pagination=keyset>; rel=\"next\"");
            return response;
        });

        var page = await client.ListProjectsAsync("tok", "to", "100", Ct);

        var project = Assert.Single(page.Items);
        Assert.Equal(("42", "acme/todo", "main", "acme"), (project.ExternalId, project.PathWithNamespace, project.DefaultBranch, project.NamespaceFullPath));
        Assert.Equal("42", page.NextCursor);
        var query = Uri.UnescapeDataString(new Uri(handler.Requests[0].Url).Query);
        foreach (var part in new[] { "membership=true", "min_access_level=30", "simple=true", "pagination=keyset", "order_by=id", "search=to", "id_before=100" })
        {
            Assert.Contains(part, query, StringComparison.Ordinal);
        }

        Assert.Equal("Bearer tok", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task FR132_Create_project_initialises_main_with_a_README_and_reports_name_taken_messages()
    {
        var (client, handler) = Client((_, _) => Json(HttpStatusCode.BadRequest, """{"message":{"name":["has already been taken"],"path":["has already been taken"]}}"""));

        var ex = await Assert.ThrowsAsync<GitLabApiException>(() => client.CreateProjectAsync("tok", new GitLabCreateProject("todo-app", null, 7, null, true), Ct));

        Assert.Equal(400, ex.Status);
        Assert.Equal("name has already been taken; path has already been taken", ex.UpstreamMessage);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.True(body.RootElement.GetProperty("initialize_with_readme").GetBoolean());
        Assert.Equal("main", body.RootElement.GetProperty("default_branch").GetString());
        Assert.Equal("private", body.RootElement.GetProperty("visibility").GetString());
        Assert.Equal(7, body.RootElement.GetProperty("namespace_id").GetInt64());
    }

    [Fact]
    public async Task S8_A_redirect_is_not_followed_and_fails()
    {
        var (client, handler) = Client((_, _) => new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://other.example/api/v4/user") } });

        var ex = await Assert.ThrowsAsync<GitLabApiException>(() => client.GetUserAsync("tok", Ct));

        Assert.Equal(301, ex.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FR132_A_429_carries_Retry_After()
    {
        var (client, _) = Client((_, _) =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, """{"message":"Retry later"}""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return response;
        });

        var ex = await Assert.ThrowsAsync<GitLabApiException>(() => client.ListProjectsAsync("tok", null, null, Ct));

        Assert.True(ex.IsRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(60), ex.RetryAfter);
    }

    [Fact]
    public async Task FR132_A_429_wait_from_a_Retry_After_date_or_RateLimit_Reset_is_measured_on_the_injected_clock()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var (byDate, _) = Client((_, _) =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, """{"message":"Retry later"}""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddSeconds(90));
            return response;
        }, new FixedClock(now));
        var (byReset, _) = Client((_, _) =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, """{"message":"Retry later"}""");
            response.Headers.TryAddWithoutValidation("RateLimit-Reset", now.AddSeconds(120).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        }, new FixedClock(now));

        var dated = await Assert.ThrowsAsync<GitLabApiException>(() => byDate.ListProjectsAsync("tok", null, null, Ct));
        var reset = await Assert.ThrowsAsync<GitLabApiException>(() => byReset.ListProjectsAsync("tok", null, null, Ct));

        Assert.Equal(TimeSpan.FromSeconds(90), dated.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(120), reset.RetryAfter);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task FR138_The_merge_request_details_carry_the_readiness_fields_and_the_rate_limit_headers()
    {
        var (client, handler) = Client((_, _) =>
        {
            var response = Json(HttpStatusCode.OK, """
                {"iid":3,"title":"Draft: Release 0.1.0","web_url":"https://gitlab.acme.example/gitlab/acme/widgets/-/merge_requests/3","state":"opened",
                 "draft":true,"detailed_merge_status":"not_approved","has_conflicts":false,"blocking_discussions_resolved":false,
                 "sha":"1111111111111111111111111111111111111111","merge_commit_sha":null,"squash_commit_sha":null,"head_pipeline":{"id":9,"status":"failed"}}
                """);
            response.Headers.TryAddWithoutValidation("RateLimit-Remaining", "5");
            response.Headers.TryAddWithoutValidation("RateLimit-Limit", "100");
            return response;
        });

        var mr = await client.GetMergeRequestDetailsAsync("tok", "77", 3, Ct);

        Assert.Equal(
            new GitLabMergeRequestDetails(3, "Draft: Release 0.1.0", "https://gitlab.acme.example/gitlab/acme/widgets/-/merge_requests/3", "opened", true, "not_approved", false, false,
                "1111111111111111111111111111111111111111", null, null, "failed"),
            mr);
        Assert.Equal("https://gitlab.acme.example/gitlab/api/v4/projects/77/merge_requests/3", handler.Requests[0].Url);
        Assert.True(Aide.Domain.GitHosts.GitHostPollBudget.IsLow(client.LastRateLimit));
    }

    [Fact]
    public async Task FR140_Merge_pins_the_sha_never_removes_the_source_branch_and_returns_the_merged_commits()
    {
        var (client, handler) = Client((_, _) => Json(HttpStatusCode.OK,
            """{"iid":3,"state":"merged","sha":"1111111111111111111111111111111111111111","merge_commit_sha":"2222222222222222222222222222222222222222","squash_commit_sha":null}"""));

        var merged = await client.MergeAsync("tok", "77", 3, "1111111111111111111111111111111111111111", squash: false, "Release v0.1.0", Ct);

        Assert.Equal(new GitLabMerged("2222222222222222222222222222222222222222", null, "1111111111111111111111111111111111111111"), merged);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(("PUT", "https://gitlab.acme.example/gitlab/api/v4/projects/77/merge_requests/3/merge"), (request.Method, request.Url));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(
            ("1111111111111111111111111111111111111111", false, false, "Release v0.1.0"),
            (body.RootElement.GetProperty("sha").GetString(), body.RootElement.GetProperty("squash").GetBoolean(),
                body.RootElement.GetProperty("should_remove_source_branch").GetBoolean(), body.RootElement.GetProperty("merge_commit_message").GetString()));
    }

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task FR140_A_refused_merge_throws_with_GitLabs_status(HttpStatusCode status)
    {
        var (client, _) = Client((_, _) => Json(status, """{"message":"Branch cannot be merged"}"""));

        var ex = await Assert.ThrowsAsync<GitLabApiException>(() => client.MergeAsync("tok", "77", 3, "1111111111111111111111111111111111111111", squash: true, null, Ct));

        Assert.Equal((int)status, ex.Status);
    }

    [Fact]
    public async Task FR140_The_release_is_created_with_the_tag_on_the_ref_the_tag_message_and_the_notes()
    {
        var (client, handler) = Client((_, _) => Json(HttpStatusCode.Created,
            """{"tag_name":"v0.1.0","name":"0.1.0","_links":{"self":"https://gitlab.acme.example/gitlab/acme/widgets/-/releases/v0.1.0"}}"""));

        var url = await client.CreateReleaseAsync("tok", "77", new GitLabNewRelease("v0.1.0", "2222222222222222222222222222222222222222", "0.1.0", "## Notes", "Release v0.1.0"), Ct);

        Assert.Equal("https://gitlab.acme.example/gitlab/acme/widgets/-/releases/v0.1.0", url);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(
            ("v0.1.0", "2222222222222222222222222222222222222222", "0.1.0", "## Notes", "Release v0.1.0"),
            (body.RootElement.GetProperty("tag_name").GetString(), body.RootElement.GetProperty("ref").GetString(), body.RootElement.GetProperty("name").GetString(),
                body.RootElement.GetProperty("description").GetString(), body.RootElement.GetProperty("tag_message").GetString()));
    }

    [Fact]
    public async Task FR139_Commit_statuses_read_name_status_allow_failure_and_the_job_URL()
    {
        var (client, handler) = Client((_, _) => Json(HttpStatusCode.OK, """
            [{"name":"test:backend","status":"failed","allow_failure":false,"target_url":"https://gitlab.acme.example/gitlab/acme/widgets/-/jobs/7"},
             {"name":"lint","status":"failed","allow_failure":true,"target_url":"javascript:alert(1)"}]
            """));

        var statuses = await client.ListCommitStatusesAsync("tok", "77", "1111111111111111111111111111111111111111", Ct);

        Assert.Equal(
            [
                new Aide.Domain.GitHosts.GitLabCommitStatus("test:backend", "failed", false, "https://gitlab.acme.example/gitlab/acme/widgets/-/jobs/7"),
                new Aide.Domain.GitHosts.GitLabCommitStatus("lint", "failed", true, null),
            ],
            statuses);
        Assert.StartsWith("https://gitlab.acme.example/gitlab/api/v4/projects/77/repository/commits/1111111111111111111111111111111111111111/statuses", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR131_Token_self_reads_scopes_and_expiry()
    {
        var (client, _) = Client((_, _) => Json(HttpStatusCode.OK, """{"id":1,"name":"aide","revoked":false,"active":true,"scopes":["api","read_repository"],"expires_at":"2026-11-05"}"""));

        var info = await client.GetTokenSelfAsync("tok", Ct);

        Assert.Equal(["api", "read_repository"], info.Scopes);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), info.ExpiresAt);
        Assert.True(info.Active);
    }
}

internal sealed record StubRequest(string Method, string Url, string? Authorization, string Body);

internal sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<StubRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new StubRequest(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
        return respond(request, body);
    }
}

internal sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class StubDns(params (string Host, string[] Addresses)[] names) : IGitHostDns
{
    public int Lookups { get; private set; }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        Lookups++;
        var match = names.FirstOrDefault(n => string.Equals(n.Host, host, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(match.Addresses is null ? [] : match.Addresses.Select(IPAddress.Parse).ToArray());
    }
}

internal sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
