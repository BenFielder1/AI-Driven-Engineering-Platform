using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aide.Api.Features.Trackers;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Trackers;
using Aide.Infrastructure.Security;
using Aide.Infrastructure.Trackers;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Trackers;

/// <summary>
/// §8 on the real Jira, Linear and GitHub adapters, with every tracker host answered by a stub (no test reaches a tracker):
/// the search timeout (<c>Trackers:SearchTimeout</c>), the <c>sources/check</c> cap (<c>Trackers:CheckTimeout</c>, set low
/// here), secrets kept out of the logs, and FR-167's PAT-mode rule.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class TrackerLimitsTests(AideApiFactory factory)
{
    private const string Site = "acme.atlassian.net";
    private const string CloudId = "cloud-limits";
    private const string JiraEmail = "ana@example.com";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri U(string path) => new(path, UriKind.Relative);

    private WebApplicationFactory<Program> App(TrackerApiStub stub, params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Trackers:Provider", "Real");
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            b.ConfigureTestServices(services =>
            {
                foreach (var name in TrackerHosts.AllClients)
                {
                    services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
                }
            });
        });

    /// <summary>A Jira or Linear connection stored as saving one would (encrypted, valid), so no validation call is needed.</summary>
    private static async Task<Guid> SeedAsync(WebApplicationFactory<Program> app, Guid userId, PlatformProvider provider, string token)
    {
        var protector = app.Services.GetRequiredService<ISecretProtectors>().ForPurpose(SecretPurposes.TrackerToken);
        var connection = new PlatformConnection
        {
            UserId = userId,
            Provider = provider,
            Label = $"Seeded {Guid.NewGuid():N}",
            SecretEncrypted = protector.Protect(TrackerSecrets.Encode(provider, provider == PlatformProvider.Jira ? JiraEmail : null, token)),
            Last4 = token[^4..],
            AccountId = "acc-1",
            AccountName = "Seeded",
            Metadata = provider == PlatformProvider.Jira
                ? new PlatformConnectionMetadata { Site = Site, CloudId = CloudId }
                : new PlatformConnectionMetadata { Organization = "Acme", OrganizationUrlKey = "acme" },
            Scopes = ["read"],
            Status = PlatformConnectionStatus.Valid,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.PlatformConnections.Add(connection);
            await db.SaveChangesAsync(Ct);
        });
        return connection.Id;
    }

    private static string JiraIssues(params string[] keys) => JsonSerializer.Serialize(new
    {
        issues = keys.Select((key, i) => new
        {
            id = (10_000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
            key,
            fields = new
            {
                summary = $"Item {key}",
                status = new { name = "To Do", statusCategory = new { key = "new" } },
                issuetype = new { name = "Story" },
                labels = Array.Empty<string>(),
                updated = "2026-10-01T09:00:00+00:00",
            },
        }),
    });

    private static bool IsJiraSearch(TrackerApiRequest r) => r.Url.AbsolutePath.EndsWith("/search/jql", StringComparison.Ordinal);

    // ---------------------------------------------------------------- §8 Performance

    [Fact]
    public async Task S8_Tracker_search_gives_up_after_Trackers_SearchTimeout_with_UPSTREAM_ERROR()
    {
        var stub = new TrackerApiStub { Respond = async (r, ct) => IsJiraSearch(r) ? await TrackerApiStub.HangAsync(ct) : TrackerApiStub.Json("{}") };
        await using var app = App(stub, ("Trackers:SearchTimeout", "00:00:01"));
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await SeedAsync(app, user.User!.Id, PlatformProvider.Jira, "atlassian_timeout_1234");

        var watch = Stopwatch.StartNew();
        var response = await user.Client.PostAsJsonAsync("/api/me/trackers/jira/search", new { connectionId = connection, containerId = "ENG" }, Ct);
        watch.Stop();

        await Problems.AssertAsync(response, HttpStatusCode.BadGateway, ErrorCodes.UpstreamError);
        Assert.Contains(stub.Requests, IsJiraSearch);

        // The gateway client's own timeout is 30 s: answering well before it means the configured timeout cut the call.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The search took {watch.Elapsed.TotalSeconds} s with a 1 s timeout.");
    }

    [Fact]
    public async Task S8_FR173_sources_check_is_capped_and_returns_unknown_for_what_remains()
    {
        var hang = false;
        var stub = new TrackerApiStub
        {
            Respond = async (r, ct) =>
            {
                if (!IsJiraSearch(r))
                {
                    return TrackerApiStub.Json("{}");
                }

                if (r.Body.Contains("ENG-2", StringComparison.Ordinal))
                {
                    return Volatile.Read(ref hang) ? await TrackerApiStub.HangAsync(ct) : TrackerApiStub.Json(JiraIssues("ENG-2"));
                }

                return TrackerApiStub.Json(JiraIssues("ENG-1"));
            },
        };
        await using var app = App(stub, ("Trackers:CheckTimeout", "00:00:03"));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var connection = await SeedAsync(app, fixture.Owner.User!.Id, PlatformProvider.Jira, "atlassian_check_1234");
        foreach (var key in new[] { "ENG-1", "ENG-2" })
        {
            await ProjectApi.ReadAsync<SourceImportDto>(
                await fixture.Owner.Client.PostAsJsonAsync($"/api/projects/{id}/sources", new { provider = "jira", connectionId = connection, selection = new { keys = new[] { key } }, options = new { includeChildren = false } }, Ct),
                HttpStatusCode.Accepted);
            var job = (await LifecycleSeed.JobsAsync(app, id)).Single(j => j.Type == JobTypes.TrackerImport && j.Status == JobStatus.Queued);
            Assert.False(await AgentJobs.RunCoreAsync(app, job));
        }

        Assert.All((await ProjectApi.ReadAsync<ProjectSourcesDto>(await fixture.Owner.Client.GetAsync(U($"/api/projects/{id}/sources"), Ct))).Imports, i => Assert.Equal(TrackerImportStatus.Completed, i.Status));

        // Jira now answers the first import's items but never the second's.
        Volatile.Write(ref hang, true);
        var watch = Stopwatch.StartNew();
        var check = await ProjectApi.ReadAsync<SourcesCheckDto>(await fixture.Owner.Client.PostAsync(U($"/api/projects/{id}/sources/check"), null, Ct));
        watch.Stop();

        Assert.Equal(SourceItemDrift.Unchanged, check.Items.Single(i => i.Key == "ENG-1").Drift);
        Assert.Equal(SourceItemDrift.Unknown, check.Items.Single(i => i.Key == "ENG-2").Drift);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The check took {watch.Elapsed.TotalSeconds} s with a 1 s cap.");
    }

    // ---------------------------------------------------------------- §8 Security

    [Fact]
    public async Task S8_The_Jira_Authorization_header_and_the_raw_Linear_key_never_reach_the_logs()
    {
        const string jiraToken = "atlassian_logsecret_7731";
        const string linearKey = "lin_api_logsecret_8842";
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{JiraEmail}:{jiraToken}"));
        var linearCalls = 0;
        var stub = new TrackerApiStub
        {
            Respond = (r, _) =>
            {
                if (IsJiraSearch(r))
                {
                    return Task.FromResult(TrackerApiStub.Json(JiraIssues("ENG-1")));
                }

                // Linear: the first search answers; the second rejects the key, echoing it as a careless API might.
                return Task.FromResult(Interlocked.Increment(ref linearCalls) == 1
                    ? TrackerApiStub.Json("""{"data":{"issues":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""")
                    : TrackerApiStub.Json($$"""{"errors":[{"message":"Authentication required, not {{linearKey}}"}]}""", HttpStatusCode.Unauthorized));
            },
        };
        await using var app = App(stub, ("Logging:LogLevel:System.Net.Http.HttpClient", "Trace"));
        using var user = await ApiSession.RegisterAsync(app);
        var jira = await SeedAsync(app, user.User!.Id, PlatformProvider.Jira, jiraToken);
        var linear = await SeedAsync(app, user.User!.Id, PlatformProvider.Linear, linearKey);

        Assert.Equal(HttpStatusCode.OK, (await user.Client.PostAsJsonAsync("/api/me/trackers/jira/search", new { connectionId = jira, containerId = "ENG" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.PostAsJsonAsync("/api/me/trackers/linear/search", new { connectionId = linear, containerId = "team:ENG" }, Ct)).StatusCode);
        var rejected = await user.Client.PostAsJsonAsync("/api/me/trackers/linear/search", new { connectionId = linear, containerId = "team:ENG" }, Ct);
        await Problems.AssertAsync(rejected, HttpStatusCode.Conflict, ErrorCodes.TrackerNotConnected);
        Assert.DoesNotContain(linearKey, await rejected.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // The secrets really were sent, in the headers the spec names.
        Assert.Contains(stub.Requests, r => IsJiraSearch(r) && r.Authorization == $"Basic {basic}");
        Assert.Contains(stub.Requests, r => r.Url.Host == "api.linear.app" && r.Authorization == linearKey);

        // The tracker clients' request logs were captured, and nothing logged holds a secret.
        Assert.Contains(factory.Logs.Entries, e => e.Category.StartsWith($"System.Net.Http.HttpClient.{TrackerHosts.JiraGatewayClient}", StringComparison.Ordinal));
        Assert.Contains(factory.Logs.Entries, e => e.Category.StartsWith($"System.Net.Http.HttpClient.{TrackerHosts.LinearClient}", StringComparison.Ordinal));
        foreach (var secret in new[] { jiraToken, basic, linearKey })
        {
            Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(secret, StringComparison.Ordinal));
        }
    }

    // ---------------------------------------------------------------- FR-167

    private static long NewGitHubId() => RandomNumberGenerator.GetInt32(1, int.MaxValue) + 30_000_000_000L;

    [Fact]
    public async Task FR167_A_PAT_mode_project_imports_GitHub_Issues_only_through_the_users_GitHub_connection_and_never_with_the_PAT()
    {
        const string pat = "ghp_neverForImports0123456789";
        const long installation = 7_167;
        await using var harness = GitHubAppHarness.Create(factory, configure: b => b.UseSetting("Trackers:Provider", "Real"));
        using var owner = await ApiSession.RegisterAsync(harness.App);
        var project = await ProjectApi.CreateAsync(owner.Client, git: new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = pat });
        var sources = $"/api/projects/{project.Id}/sources";
        var import = new { provider = "github", selection = new { keys = new[] { "acme/todo#1" } }, options = new { includeChildren = false } };

        // No GitHub connection: the project's PAT doesn't stand in for one.
        await Problems.AssertAsync(await owner.Client.PostAsJsonAsync(sources, import, Ct), HttpStatusCode.Conflict, ErrorCodes.TrackerNotConnected);
        await Problems.AssertAsync(
            await owner.Client.PostAsJsonAsync("/api/me/trackers/github/search", new { containerId = "acme/todo" }, Ct),
            HttpStatusCode.Conflict,
            ErrorCodes.TrackerNotConnected);

        // Connected: the user's token finds the repo and installation, and the installation's token reads the issues.
        await harness.SeedConnectionAsync(owner.User!.Id, NewGitHubId(), $"octo-{Guid.NewGuid():N}"[..20]);
        harness.Api.On(HttpMethod.Get, "/repos/acme/todo", r => r.Authorization == $"Bearer {GitHubAppHarness.UserToken}"
            ? (HttpStatusCode.OK, GitHubAppHarness.RepoJson(71670, "acme", "todo"))
            : (HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        harness.OnInstallations((installation, "acme", "Organization", "all"));
        harness.OnRepoInstallation("acme", "todo", installation);
        harness.Api.On(HttpMethod.Post, $"/app/installations/{installation}/access_tokens", HttpStatusCode.Created, JsonSerializer.Serialize(new
        {
            token = GitHubAppHarness.InstallationToken,
            expires_at = DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            permissions = new { issues = "read", metadata = "read" },
            repository_selection = "selected",
        }));
        const string issue = """{"id":1,"number":1,"title":"Bug one","state":"open","html_url":"https://github.com/acme/todo/issues/1","updated_at":"2026-10-01T09:00:00Z","body":"First","labels":[]}""";
        harness.Api.On(HttpMethod.Get, "/repos/acme/todo/issues/1", r => r.Authorization == $"Bearer {GitHubAppHarness.InstallationToken}"
            ? (HttpStatusCode.OK, issue)
            : (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}"""));

        var queued = await ProjectApi.ReadAsync<SourceImportDto>(await owner.Client.PostAsJsonAsync(sources, import, Ct), HttpStatusCode.Accepted);
        var job = (await LifecycleSeed.JobsAsync(harness.App, project.Id)).Single(j => j.Type == JobTypes.TrackerImport && j.Status == JobStatus.Queued);
        Assert.False(await AgentJobs.RunCoreAsync(harness.App, job));

        var done = (await ProjectApi.ReadAsync<ProjectSourcesDto>(await owner.Client.GetAsync(U(sources), Ct))).Imports.Single(i => i.Id == queued.Id);
        Assert.Equal((TrackerImportStatus.Completed, 1), (done.Status, done.ItemCount));
        Assert.Contains(harness.Api.Requests, r => r.Path == "/repos/acme/todo/issues/1" && r.Authorization == $"Bearer {GitHubAppHarness.InstallationToken}");
        Assert.DoesNotContain(harness.Api.Requests, r => r.Authorization?.Contains(pat, StringComparison.Ordinal) == true);
    }
}

/// <summary>A request a tracker stub received.</summary>
public sealed record TrackerApiRequest(HttpMethod Method, Uri Url, string? Authorization, string Body);

/// <summary>Answers every tracker host in memory and records each request (with its raw <c>Authorization</c> value).</summary>
public sealed class TrackerApiStub : HttpMessageHandler
{
    private readonly ConcurrentQueue<TrackerApiRequest> _requests = new();

    public IReadOnlyList<TrackerApiRequest> Requests => [.. _requests];

    public Func<TrackerApiRequest, CancellationToken, Task<HttpResponseMessage>> Respond { get; init; } = (_, _) => Task.FromResult(Json("{}"));

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Never answers: waits until the caller gives up.</summary>
    public static async Task<HttpResponseMessage> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable.");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var authorization = request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : null;
        var received = new TrackerApiRequest(request.Method, request.RequestUri!, authorization, body);
        _requests.Enqueue(received);
        var response = await Respond(received, cancellationToken);
        response.RequestMessage = request;
        return response;
    }
}
