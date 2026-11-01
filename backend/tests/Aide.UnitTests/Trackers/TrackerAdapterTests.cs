using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aide.Domain.GitHub;
using Aide.Domain.Trackers;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.GitLab;
using Aide.Infrastructure.Trackers;
using Aide.UnitTests.Deployments;
using Aide.UnitTests.GitHosts;

namespace Aide.UnitTests.Trackers;

/// <summary>One request a tracker stub saw, with its whole URL and raw Authorization header.</summary>
internal sealed record TrackerCall(string Method, Uri Url, string? Authorization, string Body);

/// <summary>A scripted tracker: routes by method and URL prefix (first match), in order; unmatched requests are 404. No network: every request is recorded.</summary>
internal sealed class TrackerStub : HttpMessageHandler
{
    private readonly List<(string Method, string Prefix, Func<TrackerCall, HttpResponseMessage> Respond)> _routes = [];

    public List<TrackerCall> Calls { get; } = [];

    public TrackerStub On(string method, string urlPrefix, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(method, urlPrefix, _ => Response(json, status));

    public TrackerStub On(string method, string urlPrefix, Func<TrackerCall, HttpResponseMessage> respond)
    {
        _routes.Add((method, urlPrefix, respond));
        return this;
    }

    public static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var authorization = request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null;
        var call = new TrackerCall(request.Method.Method, request.RequestUri!, authorization, body);
        Calls.Add(call);
        foreach (var route in _routes)
        {
            if (route.Method == call.Method && Uri.UnescapeDataString(call.Url.AbsoluteUri).StartsWith(route.Prefix, StringComparison.Ordinal))
            {
                return route.Respond(call);
            }
        }

        return Response("""{"message":"Not Found"}""", HttpStatusCode.NotFound);
    }
}

internal sealed class TrackerStubFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = name switch
        {
            TrackerHosts.JiraGatewayClient => new Uri(TrackerHosts.JiraGatewayBaseUrl),
            TrackerHosts.LinearClient => new Uri(TrackerHosts.LinearBaseUrl),
            "github" => new Uri("https://api.github.com/"),
            _ => null,
        };
        return client;
    }
}

internal sealed class StubInstallationTokens(IReadOnlyDictionary<string, string> permissions) : IGitHubInstallationTokens
{
    public Task<GitHubInstallationToken> GetAsync(GitHubAppCredentials app, long installationId, long? repoId, CancellationToken cancellationToken) =>
        Task.FromResult(new GitHubInstallationToken("ghs_installation", null, permissions));
}

/// <summary>FR-165–FR-168, §8: the four adapters on recorded-shape JSON with stub HTTP: 401, 403, 429 and pagination. No test hits a real tracker.</summary>
public class TrackerAdapterTests
{
    private const string Secret = "ATATT-secret-token";
    private const string Gateway = "https://api.atlassian.com/ex/jira/cloud-1/rest/api/3/";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TrackerHttp Http(TrackerStub stub) => new(new TrackerStubFactory(stub), new ManualTime(Now));

    private static TrackerCredential JiraCredential(string? cloudId = "cloud-1") =>
        new(Secret) { Email = "ana@example.com", Site = "acme.atlassian.net", CloudId = cloudId };

    private const string JiraIssue = """
        {"id":"10001","key":"PROJ-1","fields":{
          "summary":"Export to CSV","updated":"2026-10-01T09:30:00.000+0000",
          "issuetype":{"name":"Story"},"status":{"name":"In Progress","statusCategory":{"key":"indeterminate"}},
          "priority":{"name":"High"},"labels":["mvp"],"parent":{"key":"PROJ-0"},
          "assignee":{"displayName":"Ana","emailAddress":"ana@example.com"},
          "description":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"As a user"}]}]},
          "issuelinks":[{"type":{"outward":"blocks","inward":"is blocked by"},"outwardIssue":{"key":"PROJ-9"}}],
          "attachment":[{"filename":"mock.png"}],"subtasks":[{"key":"PROJ-2"}],
          "timetracking":{"originalEstimate":"3d"},
          "customfield_10042":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Given a file"}]}]}
        }}
        """;

    // ---------------------------------------------------------------- Jira

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("evil.example.com")]
    public async Task FR165_Given_a_bad_site_nothing_is_fetched(string site)
    {
        var stub = new TrackerStub();
        var jira = new JiraIssueTrackerProvider(Http(stub));

        await Assert.ThrowsAsync<TrackerApiException>(() => jira.ValidateAsync(new TrackerCredential(Secret) { Email = "a@b.c", Site = site }, Ct));

        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task FR165_Given_a_valid_scoped_token_the_cloud_id_is_resolved_and_only_the_gateway_is_called_with_Basic_auth()
    {
        var stub = new TrackerStub()
            .On("GET", "https://acme.atlassian.net/_edge/tenant_info", """{"cloudId":"cloud-1"}""")
            .On("GET", Gateway + "myself", """{"accountId":"acc-1","displayName":"Ana Dev","emailAddress":"ana@example.com"}""")
            .On("GET", Gateway + "project/search", """{"values":[{"key":"PROJ"}],"isLast":true}""");
        var jira = new JiraIssueTrackerProvider(Http(stub));

        var check = await jira.ValidateAsync(new TrackerCredential(Secret) { Email = "ana@example.com", Site = "acme.atlassian.net" }, Ct);

        Assert.Equal(("acc-1", "Ana Dev", "acme.atlassian.net", "cloud-1"), (check.AccountId, check.AccountName, check.Site, check.CloudId));
        Assert.Equal(["read"], check.Scopes);
        Assert.Empty(check.MissingPermissions);
        Assert.Null(stub.Calls[0].Authorization);
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"ana@example.com:{Secret}"));
        Assert.All(stub.Calls.Skip(1), c =>
        {
            Assert.Equal("api.atlassian.com", c.Url.Host);
            Assert.Equal(expected, c.Authorization);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task FR165_A_token_that_answers_myself_but_cant_read_projects_lacks_read_access(HttpStatusCode status)
    {
        var stub = new TrackerStub()
            .On("GET", "https://acme.atlassian.net/_edge/tenant_info", """{"cloudId":"cloud-1"}""")
            .On("GET", Gateway + "myself", """{"accountId":"acc-1"}""")
            .On("GET", Gateway + "project/search", """{"errorMessages":["no"]}""", status);

        var check = await new JiraIssueTrackerProvider(Http(stub)).ValidateAsync(new TrackerCredential(Secret) { Email = "a@b.c", Site = "acme.atlassian.net" }, Ct);

        Assert.Equal(["read:jira-work"], check.MissingPermissions);
        Assert.Empty(check.Scopes);
    }

    [Fact]
    public async Task FR165_A_rejected_token_is_rejected_and_its_message_never_holds_it()
    {
        var stub = new TrackerStub()
            .On("GET", "https://acme.atlassian.net/_edge/tenant_info", """{"cloudId":"cloud-1"}""")
            .On("GET", Gateway + "myself", $$"""{"message":"bad token {{Secret}}"}""", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<TrackerApiException>(() =>
            new JiraIssueTrackerProvider(Http(stub)).ValidateAsync(new TrackerCredential(Secret) { Email = "a@b.c", Site = "acme.atlassian.net" }, Ct));

        Assert.Equal(TrackerFailure.Rejected, ex.Failure);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR165_A_redirect_from_the_site_is_a_failure_and_is_never_followed()
    {
        var stub = new TrackerStub().On("GET", "https://acme.atlassian.net/_edge/tenant_info", _ =>
        {
            var redirect = TrackerStub.Response("{}", HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("http://169.254.169.254/");
            return redirect;
        });

        var ex = await Assert.ThrowsAsync<TrackerApiException>(() =>
            new JiraIssueTrackerProvider(Http(stub)).ValidateAsync(new TrackerCredential(Secret) { Email = "a@b.c", Site = "acme.atlassian.net" }, Ct));

        Assert.Equal(TrackerFailure.Upstream, ex.Failure);
        Assert.Single(stub.Calls);
    }

    [Fact]
    public async Task FR169_Jira_search_posts_to_search_jql_with_nextPageToken_and_hides_done_items()
    {
        var stub = new TrackerStub().On("POST", Gateway + "search/jql", $$"""{"issues":[{{JiraIssue}}],"nextPageToken":"t2"}""");
        var jira = new JiraIssueTrackerProvider(Http(stub));

        var page = await jira.SearchAsync(JiraCredential(), new TrackerSearch("PROJ", new TrackerFilter { Text = "CSV \"x\"" }, null, "t1"), Ct);

        Assert.Equal("t2", page.NextCursor);
        Assert.Equal("PROJ-1", Assert.Single(page.Items).Key);
        using var body = System.Text.Json.JsonDocument.Parse(stub.Calls[0].Body);
        Assert.Equal("t1", body.RootElement.GetProperty("nextPageToken").GetString());
        var jql = body.RootElement.GetProperty("jql").GetString()!;
        Assert.Contains("statusCategory != Done", jql, StringComparison.Ordinal);
        Assert.Contains("project = \"PROJ\"", jql, StringComparison.Ordinal);
        Assert.Contains("summary ~ \"CSV \\\"x\\\"\"", jql, StringComparison.Ordinal);
        Assert.Contains("/rest/api/3/search/jql", stub.Calls[0].Url.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void FR169_Asking_for_done_items_shows_them() =>
        Assert.Contains(
            "statusCategory in (\"Done\")",
            JiraIssueTrackerProvider.Jql(new TrackerSearch(null, new TrackerFilter { StatusCategories = [SourceItemStatusCategory.Done] }, null, null)),
            StringComparison.Ordinal);

    [Fact]
    public async Task FR169_Given_invalid_JQL_then_TRACKER_QUERY_INVALID_with_Jiras_message()
    {
        var stub = new TrackerStub().On("POST", Gateway + "search/jql", """{"errorMessages":["Field 'nope' does not exist"]}""", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<TrackerApiException>(() =>
            new JiraIssueTrackerProvider(Http(stub)).SearchAsync(JiraCredential(), new TrackerSearch(null, null, "nope = 1", null), Ct));

        Assert.Equal(TrackerFailure.QueryInvalid, ex.Failure);
        Assert.Equal("Field 'nope' does not exist", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TrackerFailure.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, TrackerFailure.MissingPermissions)]
    public async Task FR169_Jira_401_and_403_are_classified(HttpStatusCode status, TrackerFailure failure)
    {
        var stub = new TrackerStub().On("POST", Gateway + "search/jql", "{}", status);

        var ex = await Assert.ThrowsAsync<TrackerApiException>(() =>
            new JiraIssueTrackerProvider(Http(stub)).SearchAsync(JiraCredential(), new TrackerSearch("P", null, null, null), Ct));

        Assert.Equal(failure, ex.Failure);
    }

    [Fact]
    public async Task FR170_Given_a_429_with_Retry_After_60_then_the_retry_time_is_60_seconds_on()
    {
        var stub = new TrackerStub().On("POST", Gateway + "search/jql", _ =>
        {
            var limited = TrackerStub.Response("{}", HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return limited;
        });

        var ex = await Assert.ThrowsAsync<TrackerApiException>(() =>
            new JiraIssueTrackerProvider(Http(stub)).GetItemsAsync(JiraCredential(), new TrackerSelection { Query = "x" }, new TrackerImportOptions(IncludeChildren: false), Ct));

        Assert.Equal(TrackerFailure.RateLimited, ex.Failure);
        Assert.Equal(Now.AddSeconds(60), ex.RetryAt);
    }

    [Fact]
    public async Task FR170_S4_26_Jira_items_are_normalised_with_ADF_links_hierarchy_and_the_acceptance_field_and_no_email()
    {
        var stub = new TrackerStub()
            .On("POST", Gateway + "search/jql", $$"""{"issues":[{{JiraIssue}}]}""")
            .On("GET", Gateway + "issue/PROJ-1/comment", """{"total":25,"comments":[{"created":"2026-10-01T08:00:00.000+0000","author":{"displayName":"Bob","emailAddress":"bob@example.com"},"body":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Nice"}]}]}}]}""");
        var jira = new JiraIssueTrackerProvider(Http(stub));

        var item = Assert.Single(await jira.GetItemsAsync(
            JiraCredential() with { AcceptanceCriteriaField = "customfield_10042" },
            new TrackerSelection { Keys = ["PROJ-1"] },
            new TrackerImportOptions(IncludeChildren: false, IncludeComments: true),
            Ct));

        Assert.Equal(("https://acme.atlassian.net/browse/PROJ-1", SourceItemType.Story, SourceItemStatusCategory.InProgress), (item.Url, item.Type, item.StatusCategory));
        Assert.Equal("As a user", item.DescriptionMarkdown);
        Assert.Equal("Given a file", item.AcceptanceCriteria);
        Assert.Equal(("PROJ-0", "PROJ-2"), (item.ParentKey, item.ChildKeys[0]));
        Assert.Equal(new SourceItemLink("blocks", "PROJ-9"), item.Links[0]);
        Assert.Equal(["mock.png"], item.AttachmentNames);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero), item.UpdatedAt);
        Assert.Equal(("Bob", "Nice", 24), (item.Comments![0].Author, item.Comments[0].BodyMarkdown, item.CommentsDropped));
        Assert.DoesNotContain("example.com", System.Text.Json.JsonSerializer.Serialize(item), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR173_A_key_Jira_refuses_is_simply_absent()
    {
        var stub = new TrackerStub().On("POST", Gateway + "search/jql", call =>
            call.Body.Contains("PROJ-404", StringComparison.Ordinal)
                ? TrackerStub.Response("""{"errorMessages":["An issue with key 'PROJ-404' does not exist"]}""", HttpStatusCode.BadRequest)
                : TrackerStub.Response($$"""{"issues":[{{JiraIssue}}]}"""));

        var items = await new JiraIssueTrackerProvider(Http(stub)).GetItemsAsync(
            JiraCredential(), new TrackerSelection { Keys = ["PROJ-1", "PROJ-404"] }, new TrackerImportOptions(IncludeChildren: false), Ct);

        Assert.Equal("PROJ-1", Assert.Single(items).Key);
    }

    [Fact]
    public async Task FR165_Fields_come_from_GET_field_for_the_acceptance_picker()
    {
        var stub = new TrackerStub().On("GET", Gateway + "field", """[{"id":"summary","name":"Summary"},{"id":"customfield_10042","name":"Acceptance"}]""");

        var fields = await new JiraIssueTrackerProvider(Http(stub)).ListFieldsAsync(JiraCredential(), Ct);

        Assert.Equal(["customfield_10042", "summary"], fields.Select(f => f.Id));
    }

    // ---------------------------------------------------------------- Linear

    private const string LinearUrl = "https://api.linear.app/graphql";

    [Fact]
    public async Task FR166_The_key_goes_in_Authorization_with_no_Bearer_and_the_email_isnt_kept()
    {
        var stub = new TrackerStub().On("POST", LinearUrl, """{"data":{"viewer":{"id":"u1","name":"Ana","email":"ana@example.com","organization":{"name":"Acme","urlKey":"acme"}}}}""");

        var check = await new LinearIssueTrackerProvider(Http(stub)).ValidateAsync(new TrackerCredential("lin_api_abc"), Ct);

        Assert.Equal("lin_api_abc", stub.Calls[0].Authorization);
        Assert.Contains("viewer { id name email organization { name urlKey } }", stub.Calls[0].Body, StringComparison.Ordinal);
        Assert.Equal(("u1", "Ana", "Acme", "acme"), (check.AccountId, check.AccountName, check.Organization, check.OrganizationUrlKey));
        Assert.DoesNotContain("example.com", check.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"errors":[{"message":"Authentication required","extensions":{"code":"AUTHENTICATION_ERROR"}}]}""", TrackerFailure.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, """{"errors":[{"message":"no"}]}""", TrackerFailure.Rejected)]
    [InlineData(HttpStatusCode.OK, """{"errors":[{"message":"Forbidden","extensions":{"code":"FORBIDDEN"}}]}""", TrackerFailure.MissingPermissions)]
    [InlineData(HttpStatusCode.BadRequest, """{"errors":[{"message":"Too many","extensions":{"code":"RATELIMITED"}}]}""", TrackerFailure.RateLimited)]
    public async Task FR166_Linear_errors_are_classified_by_code(HttpStatusCode status, string json, TrackerFailure failure)
    {
        var stub = new TrackerStub().On("POST", LinearUrl, json, status);

        var ex = await Assert.ThrowsAsync<TrackerApiException>(() => new LinearIssueTrackerProvider(Http(stub)).ValidateAsync(new TrackerCredential("lin_api_abc"), Ct));

        Assert.Equal(failure, ex.Failure);
    }

    [Fact]
    public async Task FR170_Linears_reset_header_gives_the_retry_time()
    {
        var reset = Now.AddMinutes(5);
        var stub = new TrackerStub().On("POST", LinearUrl, _ =>
        {
            var limited = TrackerStub.Response("""{"errors":[{"extensions":{"code":"RATELIMITED"}}]}""", HttpStatusCode.BadRequest);
            limited.Headers.Add("X-RateLimit-Requests-Reset", reset.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return limited;
        });

        var ex = await Assert.ThrowsAsync<TrackerApiException>(() => new LinearIssueTrackerProvider(Http(stub)).ValidateAsync(new TrackerCredential("k"), Ct));

        Assert.Equal(reset, ex.RetryAt);
    }

    private const string LinearPage = """
        {"data":{"issues":{"nodes":[{"id":"i1","identifier":"ENG-1","title":"Parent","url":"https://linear.app/x/ENG-1","updatedAt":"2026-10-01T09:00:00.000Z","priorityLabel":"High","estimate":3,
          "description":"Markdown body","state":{"name":"Doing","type":"started"},"labels":{"nodes":[{"name":"bug"}]},"parent":null,"project":{"id":"p1","name":"Launch"},"assignee":{"displayName":"Ana"},
          "children":{"nodes":[{"identifier":"ENG-2"}]},"attachments":{"nodes":[{"title":"spec.pdf"}]}}],"pageInfo":{"hasNextPage":true,"endCursor":"c2"}}}}
        """;

    [Fact]
    public async Task FR169_Linear_search_uses_relay_cursors_and_hides_done()
    {
        var stub = new TrackerStub().On("POST", LinearUrl, LinearPage);

        var page = await new LinearIssueTrackerProvider(Http(stub)).SearchAsync(new TrackerCredential("k"), new TrackerSearch("team:ENG", null, null, "c1"), Ct);

        Assert.Equal("c2", page.NextCursor);
        var body = stub.Calls[0].Body;
        Assert.Contains("\"after\":\"c1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"nin\":[\"completed\",\"canceled\"]", body, StringComparison.Ordinal);
        Assert.Contains("\"key\":{\"eq\":\"ENG\"}", body, StringComparison.Ordinal);
        var item = Assert.Single(page.Items);
        Assert.Equal((SourceItemType.Bug, SourceItemStatusCategory.InProgress, "Launch"), (item.Type, item.StatusCategory, item.ParentKey));
    }

    [Fact]
    public async Task FR169_S4_26_Linear_children_are_fetched_per_page_by_team_and_number()
    {
        var calls = 0;
        var stub = new TrackerStub().On("POST", LinearUrl, call =>
        {
            calls++;
            return TrackerStub.Response(calls == 1
                ? LinearPage.Replace("\"hasNextPage\":true", "\"hasNextPage\":false", StringComparison.Ordinal)
                : """{"data":{"issues":{"nodes":[{"id":"i2","identifier":"ENG-2","title":"Child","url":"u","updatedAt":"2026-10-01T09:00:00.000Z","state":{"name":"Todo","type":"unstarted"},"labels":{"nodes":[]},"parent":{"identifier":"ENG-1"},"children":{"nodes":[]},"attachments":{"nodes":[]}}],"pageInfo":{"hasNextPage":false}}}}""");
        });

        var items = await new LinearIssueTrackerProvider(Http(stub)).GetItemsAsync(new TrackerCredential("k"), new TrackerSelection { ContainerId = "team:ENG" }, new TrackerImportOptions(), Ct);

        Assert.Equal(["ENG-1", "ENG-2"], items.Select(i => i.Key));
        Assert.Equal(SourceItemType.Subtask, items[1].Type);
        Assert.Contains("\"number\":{\"in\":[2]}", stub.Calls[1].Body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- GitHub

    private static GitHubApi GitHub(TrackerStub stub) => new(new TrackerStubFactory(stub), new ManualTime(Now));

    private const string GitHubIssues = """
        [
          {"id":1,"number":1,"title":"Bug one","state":"open","html_url":"https://github.com/o/r/issues/1","updated_at":"2026-10-01T09:00:00Z","body":"First","labels":[{"name":"bug"}],"assignee":{"login":"ana"}},
          {"id":2,"number":2,"title":"PR","state":"open","pull_request":{"url":"x"},"updated_at":"2026-10-01T09:00:00Z"},
          {"id":3,"number":3,"title":"Typed","state":"closed","type":{"name":"Epic"},"updated_at":"2026-10-01T09:00:00Z","labels":[]},
          {"id":4,"number":4,"title":"PR2","state":"closed","pull_request":{"url":"y"},"updated_at":"2026-10-01T09:00:00Z"},
          {"id":5,"number":5,"title":"Plain","state":"open","updated_at":"2026-10-01T09:00:00Z","labels":[]}
        ]
        """;

    [Fact]
    public async Task FR167_Given_a_repo_with_3_issues_and_2_PRs_then_a_search_returns_3_items()
    {
        var stub = new TrackerStub().On("GET", "https://api.github.com/repos/o/r/issues", GitHubIssues);

        var page = await new GitHubIssueTrackerProvider(GitHub(stub)).SearchAsync(new TrackerCredential("ghs_x"), new TrackerSearch("o/r", null, null, null), Ct);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(["o/r#1", "o/r#3", "o/r#5"], page.Items.Select(i => i.Key));
        Assert.Equal((SourceItemType.Bug, SourceItemType.Epic), (page.Items[0].Type, page.Items[1].Type));
        Assert.Equal(SourceItemStatusCategory.Done, page.Items[1].StatusCategory);
        Assert.Contains("state=open", stub.Calls[0].Url.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer ghs_x", stub.Calls[0].Authorization);
    }

    [Fact]
    public async Task FR169_A_GitHub_native_query_stays_in_this_repository_and_on_issues()
    {
        var stub = new TrackerStub().On("GET", "https://api.github.com/search/issues", """{"total_count":0,"items":[]}""");

        await new GitHubIssueTrackerProvider(GitHub(stub)).SearchAsync(new TrackerCredential("t"), new TrackerSearch("o/r", null, "label:mvp", null), Ct);

        Assert.Contains("repo:o/r is:issue label:mvp", Uri.UnescapeDataString(stub.Calls[0].Url.Query), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TrackerFailure.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, TrackerFailure.MissingPermissions)]
    [InlineData(HttpStatusCode.UnprocessableEntity, TrackerFailure.QueryInvalid)]
    public async Task FR167_GitHub_failures_are_classified(HttpStatusCode status, TrackerFailure failure)
    {
        var stub = new TrackerStub().On("GET", "https://api.github.com/repos/o/r/issues", """{"message":"no"}""", status);

        var ex = await Assert.ThrowsAsync<TrackerApiException>(() =>
            new GitHubIssueTrackerProvider(GitHub(stub)).SearchAsync(new TrackerCredential("t"), new TrackerSearch("o/r", null, null, null), Ct));

        Assert.Equal(failure, ex.Failure);
    }

    [Fact]
    public async Task FR170_A_GitHub_429_is_rate_limited_with_its_retry_time()
    {
        var stub = new TrackerStub().On("GET", "https://api.github.com/repos/o/r/issues", _ =>
        {
            var limited = TrackerStub.Response("{}", HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return limited;
        });

        var ex = await Assert.ThrowsAsync<TrackerApiException>(() =>
            new GitHubIssueTrackerProvider(GitHub(stub)).SearchAsync(new TrackerCredential("t"), new TrackerSearch("o/r", null, null, null), Ct));

        Assert.Equal((TrackerFailure.RateLimited, Now.AddSeconds(60)), (ex.Failure, ex.RetryAt!.Value));
    }

    [Fact]
    public async Task FR169_GitHub_pages_while_a_page_is_full()
    {
        var full = "[" + string.Join(',', Enumerable.Range(1, 50).Select(n => $$"""{"id":{{n}},"number":{{n}},"title":"x","state":"open","updated_at":"2026-10-01T09:00:00Z"}""")) + "]";
        var stub = new TrackerStub().On("GET", "https://api.github.com/repos/o/r/issues", full);

        var page = await new GitHubIssueTrackerProvider(GitHub(stub)).SearchAsync(new TrackerCredential("t"), new TrackerSearch("o/r", null, null, "2"), Ct);

        Assert.Equal("3", page.NextCursor);
        Assert.Contains("page=2", stub.Calls[0].Url.Query, StringComparison.Ordinal);
    }

    private static string Comments(int from, int to) =>
        "[" + string.Join(',', Enumerable.Range(from, to - from + 1).Select(n =>
            "{\"body\":\"c" + n + "\",\"created_at\":\"" + new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(n).ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\",\"user\":{\"login\":\"ana\"}}")) + "]";

    [Theory]
    [InlineData(250, 3)]
    [InlineData(205, 3)]
    public async Task S4_26_Over_100_GitHub_comments_keep_the_newest_20_with_the_count_dropped(int total, int lastPage)
    {
        var stub = new TrackerStub()
            .On("GET", "https://api.github.com/repos/o/r/issues/1/comments", call =>
            {
                var page = int.Parse(System.Web.HttpUtility.ParseQueryString(call.Url.Query)["page"]!, System.Globalization.CultureInfo.InvariantCulture);
                return TrackerStub.Response(page == 1 ? Comments(1, 100) : Comments(((page - 1) * 100) + 1, Math.Min(page * 100, total)));
            })
            .On("GET", "https://api.github.com/repos/o/r/issues/1", $$"""{"id":1,"number":1,"title":"x","state":"open","updated_at":"2026-10-01T09:00:00Z","comments":{{total}}}""");

        var item = Assert.Single(await new GitHubIssueTrackerProvider(GitHub(stub)).GetItemsAsync(
            new TrackerCredential("t"), new TrackerSelection { Keys = ["o/r#1"] }, new TrackerImportOptions(IncludeChildren: false, IncludeComments: true), Ct));
        var normalized = SourceItemLimits.Normalize(item, new TrackerImportOptions(IncludeComments: true));

        Assert.Equal(20, normalized.Comments!.Count);
        Assert.Equal($"c{total}", normalized.Comments[0].BodyMarkdown);
        Assert.Equal($"c{total - 19}", normalized.Comments[^1].BodyMarkdown);
        Assert.Equal(total - 20, normalized.CommentsDropped);
        Assert.Contains(stub.Calls, c => c.Url.Query.Contains($"page={lastPage}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR167_Sub_issues_are_paginated()
    {
        static string Issues(int from, int count) =>
            "[" + string.Join(',', Enumerable.Range(from, count).Select(n => $$"""{"id":{{n}},"number":{{n}},"title":"x","state":"open","updated_at":"2026-10-01T09:00:00Z"}""")) + "]";
        var stub = new TrackerStub()
            .On("GET", "https://api.github.com/repos/o/r/issues/1/sub_issues", call => TrackerStub.Response(call.Url.Query.Contains("&page=2", StringComparison.Ordinal) ? Issues(150, 2) : Issues(100, 50)))
            .On("GET", "https://api.github.com/repos/o/r/issues/1", """{"id":1,"number":1,"title":"Parent","state":"open","updated_at":"2026-10-01T09:00:00Z"}""");

        var items = await new GitHubIssueTrackerProvider(GitHub(stub)).GetItemsAsync(new TrackerCredential("t"), new TrackerSelection { Keys = ["o/r#1"] }, new TrackerImportOptions(), Ct);

        Assert.Equal(53, items.Count);
    }

    [Fact]
    public async Task FR167_Sub_issues_come_from_the_sub_issues_API()
    {
        var stub = new TrackerStub()
            .On("GET", "https://api.github.com/repos/o/r/issues/1/sub_issues", """[{"id":9,"number":9,"title":"Child","state":"open","updated_at":"2026-10-01T09:00:00Z"}]""")
            .On("GET", "https://api.github.com/repos/o/r/issues/9/sub_issues", "[]")
            .On("GET", "https://api.github.com/repos/o/r/issues/1", """{"id":1,"number":1,"title":"Parent","state":"open","updated_at":"2026-10-01T09:00:00Z"}""");

        var items = await new GitHubIssueTrackerProvider(GitHub(stub)).GetItemsAsync(new TrackerCredential("t"), new TrackerSelection { Keys = ["o/r#1"] }, new TrackerImportOptions(), Ct);

        Assert.Equal(["o/r#1", "o/r#9"], items.Select(i => i.Key));
        Assert.Equal("o/r#1", items[1].ParentKey);
    }

    private static GitHubAppCredentials App()
    {
        using var rsa = RSA.Create(2048);
        return new GitHubAppCredentials(7, "aide-app", "cid", "secret", rsa.ExportRSAPrivateKeyPem(), GitHubAppSource.Database, null, null, "https://github.com/apps/aide-app", null);
    }

    private static TrackerStub GitHubAccessStub() => new TrackerStub()
        .On("GET", "https://api.github.com/repos/o/r/installation", """{"id":55}""")
        .On("GET", "https://api.github.com/repos/o/r", """{"id":99,"full_name":"o/r","name":"r","owner":{"login":"o","type":"User"},"private":true,"default_branch":"main","size":1,"html_url":"https://github.com/o/r"}""")
        .On("GET", "https://api.github.com/user/installations", """{"installations":[{"id":55,"account":{"login":"o","type":"User"},"repository_selection":"all","permissions":{"issues":"read"}}]}""");

    [Fact]
    public async Task FR167_The_user_token_decides_visibility_and_the_installation_token_reads()
    {
        var access = new GitHubTrackerAccess(GitHub(GitHubAccessStub()), new StubInstallationTokens(new Dictionary<string, string> { ["issues"] = "read" }), new ManualTime(Now));

        Assert.Equal("ghs_installation", await access.ReadTokenAsync("ghu_user", App(), "o", "r", Ct));
    }

    [Fact]
    public async Task FR167_Given_an_App_without_the_permission_then_TRACKER_PERMISSION_MISSING_with_the_App_settings_link()
    {
        var access = new GitHubTrackerAccess(GitHub(GitHubAccessStub()), new StubInstallationTokens(new Dictionary<string, string> { ["contents"] = "write" }), new ManualTime(Now));

        var ex = await Assert.ThrowsAsync<TrackerApiException>(() => access.ReadTokenAsync("ghu_user", App(), "o", "r", Ct));

        Assert.Equal((TrackerFailure.AppPermissionMissing, "https://github.com/apps/aide-app"), (ex.Failure, ex.Link));
    }

    [Fact]
    public async Task FR167_A_repo_the_user_token_cant_see_is_not_found_and_no_token_is_minted()
    {
        var stub = new TrackerStub().On("GET", "https://api.github.com/user/installations", """{"installations":[]}""");
        var access = new GitHubTrackerAccess(GitHub(stub), new StubInstallationTokens(new Dictionary<string, string>()), new ManualTime(Now));

        var ex = await Assert.ThrowsAsync<TrackerApiException>(() => access.ReadTokenAsync("ghu_user", App(), "o", "secret", Ct));

        Assert.Equal(TrackerFailure.NotFound, ex.Failure);
    }

    [Fact]
    public async Task FR167_An_installation_the_user_cant_see_isnt_used()
    {
        var stub = new TrackerStub()
            .On("GET", "https://api.github.com/repos/o/r/installation", """{"id":77}""")
            .On("GET", "https://api.github.com/repos/o/r", """{"id":99,"full_name":"o/r","name":"r","owner":{"login":"o","type":"User"},"private":true,"default_branch":"main","size":1,"html_url":"h"}""")
            .On("GET", "https://api.github.com/user/installations", """{"installations":[]}""");
        var access = new GitHubTrackerAccess(GitHub(stub), new StubInstallationTokens(new Dictionary<string, string> { ["issues"] = "read" }), new ManualTime(Now));

        var ex = await Assert.ThrowsAsync<TrackerApiException>(() => access.ReadTokenAsync("ghu_user", App(), "o", "r", Ct));

        Assert.Equal(TrackerFailure.NotConnected, ex.Failure);
    }

    // ---------------------------------------------------------------- GitLab

    private static GitLabIssueTrackerProvider GitLab(TrackerStub stub) =>
        new(new GitLabClients(new TrackerStubFactory(stub), new StaticMonitor<GitLabOptions>(new GitLabOptions()), new ManualTime(Now)));

    private static TrackerCredential GitLabCredential => new("glpat-x") { InstanceBaseUrl = new Uri("https://gitlab.example.com/") };

    private const string GitLabIssue = """
        [{"id":1,"iid":7,"title":"Incident","description":"Body","state":"opened","issue_type":"incident","web_url":"https://gitlab.example.com/g/p/-/issues/7","updated_at":"2026-10-01T09:00:00.000Z",
          "labels":["ops"],"milestone":{"title":"v1"},"assignee":{"name":"Ana","username":"ana"},"time_stats":{"human_time_estimate":"3h"}}]
        """;

    [Fact]
    public async Task FR168_GitLab_goes_only_to_its_instance_with_keyset_pagination_and_groups_by_milestone()
    {
        var stub = new TrackerStub().On("GET", "https://gitlab.example.com/api/v4/projects/g/p/issues", _ =>
        {
            var response = TrackerStub.Response(GitLabIssue);
            response.Headers.Add("Link", "<https://gitlab.example.com/api/v4/projects/g%2Fp/issues?cursor=abc%3D&pagination=keyset>; rel=\"next\"");
            return response;
        });

        var page = await GitLab(stub).SearchAsync(GitLabCredential, new TrackerSearch("g/p", new TrackerFilter { Labels = ["ops"] }, null, "prev"), Ct);

        var call = Assert.Single(stub.Calls);
        Assert.Equal("gitlab.example.com", call.Url.Host);
        Assert.Equal("Bearer glpat-x", call.Authorization);
        Assert.Contains("pagination=keyset", call.Url.Query, StringComparison.Ordinal);
        Assert.Contains("state=opened", call.Url.Query, StringComparison.Ordinal);
        Assert.Contains("cursor=prev", call.Url.Query, StringComparison.Ordinal);
        Assert.Equal("abc=", page.NextCursor);
        var item = Assert.Single(page.Items);
        Assert.Equal(("g/p#7", SourceItemType.Bug, "3h"), (item.Key, item.Type, item.Estimate));
        Assert.Equal(["ops", "milestone:v1"], item.Labels);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TrackerFailure.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, TrackerFailure.MissingPermissions)]
    [InlineData(HttpStatusCode.TooManyRequests, TrackerFailure.RateLimited)]
    public async Task FR168_GitLab_failures_are_classified(HttpStatusCode status, TrackerFailure failure)
    {
        var stub = new TrackerStub().On("GET", "https://gitlab.example.com/api/v4/projects/g/p/issues", """{"message":"no"}""", status);

        var ex = await Assert.ThrowsAsync<TrackerApiException>(() => GitLab(stub).SearchAsync(GitLabCredential, new TrackerSearch("g/p", null, null, null), Ct));

        Assert.Equal(failure, ex.Failure);
        Assert.Equal(failure == TrackerFailure.RateLimited, ex.RetryAt is not null);
    }

    [Fact]
    public async Task FR168_A_missing_key_is_absent_and_system_notes_arent_comments()
    {
        var stub = new TrackerStub()
            .On("GET", "https://gitlab.example.com/api/v4/projects/g/p/issues/7/notes", """[{"body":"Moved","system":true,"created_at":"2026-10-01T09:00:00Z"},{"body":"Real","system":false,"created_at":"2026-10-01T09:00:00Z","author":{"name":"Ana"}}]""")
            .On("GET", "https://gitlab.example.com/api/v4/projects/g/p/issues/7", GitLabIssue.Trim().TrimStart('[').TrimEnd(']'));

        var items = await GitLab(stub).GetItemsAsync(GitLabCredential, new TrackerSelection { Keys = ["g/p#7", "g/p#8"] }, new TrackerImportOptions(IncludeComments: true), Ct);

        var item = Assert.Single(items);
        Assert.Equal("Real", Assert.Single(item.Comments!).BodyMarkdown);
    }

    // ---------------------------------------------------------------- Fake and credentials

    [Fact]
    public void S8_A_tracker_credential_never_prints_its_secret()
    {
        var text = JiraCredential().ToString();

        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("ana@example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FR165_A_Jira_secret_round_trips_email_and_token_together_and_Linears_is_the_key()
    {
        var encoded = TrackerSecrets.Encode(Aide.Domain.Entities.PlatformProvider.Jira, " ana@example.com ", Secret);

        Assert.Equal(("ana@example.com", Secret), TrackerSecrets.Decode(Aide.Domain.Entities.PlatformProvider.Jira, encoded));
        Assert.Equal("lin", TrackerSecrets.Encode(Aide.Domain.Entities.PlatformProvider.Linear, null, "lin"));
        Assert.Equal((null, "lin"), TrackerSecrets.Decode(Aide.Domain.Entities.PlatformProvider.Linear, "lin"));
        Assert.Equal((null, string.Empty), TrackerSecrets.Decode(Aide.Domain.Entities.PlatformProvider.Jira, "not json"));
    }

    [Fact]
    public async Task FR170_The_Fake_scripts_12_items_a_done_one_an_injected_one_and_failures()
    {
        var script = new FakeTrackerScript();
        var fake = new FakeIssueTrackerProvider(TrackerProvider.Jira, script);
        var credential = new TrackerCredential("t");

        var all = await fake.GetItemsAsync(credential, new TrackerSelection { Query = "anything" }, new TrackerImportOptions(), Ct);
        Assert.Equal(12, all.Count);
        Assert.Contains(all, i => i.StatusCategory == SourceItemStatusCategory.Done);
        Assert.Contains(all, i => i.DescriptionMarkdown == FakeTrackerScript.InjectionBody);

        var browse = await fake.SearchAsync(credential, new TrackerSearch(null, null, null, null), Ct);
        Assert.Equal(11, browse.Items.Count);
        Assert.Equal(201, (await fake.GetItemsAsync(credential, new TrackerSelection { ContainerId = FakeIssueTrackerProvider.HugeContainer }, new TrackerImportOptions(), Ct)).Count);

        var invalid = await Assert.ThrowsAsync<TrackerApiException>(() => fake.SearchAsync(credential, new TrackerSearch(null, null, FakeIssueTrackerProvider.InvalidQuery, null), Ct));
        Assert.Equal(TrackerFailure.QueryInvalid, invalid.Failure);
        var rejected = await Assert.ThrowsAsync<TrackerApiException>(() => fake.ValidateAsync(new TrackerCredential("fake_invalid_x"), Ct));
        Assert.Equal(TrackerFailure.Rejected, rejected.Failure);
        Assert.Equal(["read:jira-work"], (await fake.ValidateAsync(new TrackerCredential("fake_scope_x"), Ct)).MissingPermissions);

        script.RateLimitNext(TimeSpan.FromSeconds(60));
        var limited = await Assert.ThrowsAsync<TrackerApiException>(() => fake.SearchAsync(credential, new TrackerSearch(null, null, null, null), Ct));
        Assert.Equal(TrackerFailure.RateLimited, limited.Failure);
        Assert.Equal(11, (await fake.SearchAsync(credential, new TrackerSearch(null, null, null, null), Ct)).Items.Count);
    }
}
