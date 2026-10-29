using System.Net;
using System.Net.Http.Json;
using System.Text;
using Aide.Api.Features.Bitbucket;
using Aide.Domain.Common;
using Aide.IntegrationTests.Support;

namespace Aide.IntegrationTests.Bitbucket;

/// <summary>
/// FR-135 against a stub Bitbucket: the repo picker across workspaces (role=contributor, the name filter, cursor), the
/// workspace and project owners, and repository creation with the initial README pushed to <c>main</c> over a local bare
/// repository standing in for bitbucket.org (the token only in the push's per-command extraHeader), plus the create error
/// map.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class BitbucketRepoTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR135_The_picker_lists_contributor_repos_workspace_by_workspace_with_the_name_filter()
    {
        var bitbucket = new FakeGitHubApi()
            .On(HttpMethod.Get, "/2.0/user/workspaces", HttpStatusCode.OK, BitbucketHarness.WorkspacesJson("acme", "beta"))
            .On(HttpMethod.Get, "/2.0/repositories/acme", HttpStatusCode.OK, BitbucketHarness.ReposJson(next: false, ("acme", "todo-app")))
            .On(HttpMethod.Get, "/2.0/repositories/beta", HttpStatusCode.OK, BitbucketHarness.ReposJson(next: true, ("beta", "todo-api")));
        var app = BitbucketHarness.App(factory, bitbucket);
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, user.User!.Id);

        var first = await ProjectApi.ReadAsync<BitbucketRepoPageDto>(await user.Client.GetAsync(new Uri($"/api/me/git/bitbucket/repos?connectionId={connection}&search=todo", UriKind.Relative), Ct));
        var second = await ProjectApi.ReadAsync<BitbucketRepoPageDto>(await user.Client.GetAsync(new Uri($"/api/me/git/bitbucket/repos?connectionId={connection}&search=todo&cursor={first.NextCursor}", UriKind.Relative), Ct));

        Assert.Equal(("acme/todo-app", "beta:1"), (Assert.Single(first.Items).FullName, first.NextCursor));
        Assert.Equal(("beta/todo-api", "beta:2"), (Assert.Single(second.Items).FullName, second.NextCursor));
        // The workspace list is read once and reused for the next page.
        Assert.Single(bitbucket.Requests, r => r.Path == "/2.0/user/workspaces");
        var acme = Assert.Single(bitbucket.Requests, r => r.Path == "/2.0/repositories/acme");
        Assert.Contains("role=contributor", acme.Query, StringComparison.Ordinal);
        Assert.Contains("sort=-updated_on", acme.Query, StringComparison.Ordinal);
        Assert.Contains("q=name~\"todo\"", acme.Query, StringComparison.Ordinal);
        Assert.All(bitbucket.Requests, r => Assert.Equal("Bearer bb-seeded-token", r.Authorization));

        var bad = await user.Client.GetAsync(new Uri($"/api/me/git/bitbucket/repos?connectionId={connection}&cursor=nope", UriKind.Relative), Ct);
        var problem = await Problems.AssertAsync(bad, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(problem.GetProperty("errors").TryGetProperty("cursor", out _));
    }

    [Fact]
    public async Task FR135_Owners_are_the_workspaces_with_their_projects()
    {
        var bitbucket = new FakeGitHubApi()
            .On(HttpMethod.Get, "/2.0/user/workspaces", HttpStatusCode.OK, BitbucketHarness.WorkspacesJson("acme", "locked"))
            .On(HttpMethod.Get, "/2.0/workspaces/acme/projects", HttpStatusCode.OK, """{"values":[{"key":"TODO","name":"Todo","is_private":true},{"key":"WEB","name":"Web","is_private":false}]}""")
            .On(HttpMethod.Get, "/2.0/workspaces/locked/projects", HttpStatusCode.Forbidden, """{"type":"error","error":{"message":"Forbidden"}}""");
        var app = BitbucketHarness.App(factory, bitbucket);
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, user.User!.Id);

        var owners = await ProjectApi.ReadAsync<BitbucketOwnersDto>(await user.Client.GetAsync(new Uri($"/api/me/git/bitbucket/owners?connectionId={connection}", UriKind.Relative), Ct));

        Assert.False(owners.Truncated);
        Assert.Equal(["acme", "locked"], owners.Workspaces.Select(o => o.Slug));
        Assert.Equal(["TODO", "WEB"], owners.Workspaces[0].Projects.Select(p => p.Key));
        Assert.False(owners.Workspaces[0].ProjectsTruncated);
        Assert.Empty(owners.Workspaces[1].Projects);
        Assert.Contains("pagelen=50", Assert.Single(bitbucket.Requests, r => r.Path == "/2.0/user/workspaces").Query, StringComparison.Ordinal);
        Assert.Single(bitbucket.Requests, r => r.Path == "/2.0/workspaces/acme/projects");
    }

    [Fact]
    public async Task FR135_Owners_list_at_most_50_workspaces_and_one_page_of_projects_each()
    {
        var bitbucket = new FakeGitHubApi()
            .On(HttpMethod.Get, "/2.0/user/workspaces", HttpStatusCode.OK,
                BitbucketHarness.WorkspacesJson("acme").Replace("]}", "],\"next\":\"https://api.bitbucket.org/2.0/user/workspaces?page=2\"}", StringComparison.Ordinal))
            .On(HttpMethod.Get, "/2.0/workspaces/acme/projects", HttpStatusCode.OK,
                """{"values":[{"key":"TODO","name":"Todo"}],"next":"https://api.bitbucket.org/2.0/workspaces/acme/projects?page=2"}""");
        var app = BitbucketHarness.App(factory, bitbucket);
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, user.User!.Id);

        var owners = await ProjectApi.ReadAsync<BitbucketOwnersDto>(await user.Client.GetAsync(new Uri($"/api/me/git/bitbucket/owners?connectionId={connection}", UriKind.Relative), Ct));

        Assert.True(owners.Truncated);
        Assert.True(Assert.Single(owners.Workspaces).ProjectsTruncated);
        Assert.Single(bitbucket.Requests, r => r.Path == "/2.0/user/workspaces");
        Assert.Single(bitbucket.Requests, r => r.Path == "/2.0/workspaces/acme/projects");
    }

    [Fact]
    public async Task FR135_Given_a_user_creates_acme_todo_app_in_project_TODO_then_main_holds_a_README()
    {
        var root = Path.Combine(Path.GetTempPath(), "aide-tests-bitbucket", Guid.NewGuid().ToString("N"));
        var bare = Path.Combine(root, "acme", "todo-app.git");
        Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
        await TestGit.RunAsync(Path.GetDirectoryName(bare)!, Ct, "init", "-q", "--bare", bare);
        try
        {
            var bitbucket = new FakeGitHubApi()
                .On(HttpMethod.Post, "/2.0/repositories/acme/todo-app", HttpStatusCode.OK, BitbucketHarness.RepoJson("acme", "todo-app", mainBranch: null));
            var app = BitbucketHarness.AppWithGit(factory, bitbucket, root, out var git);
            using var user = await ApiSession.RegisterAsync(app);
            var connection = await BitbucketHarness.SeedConnectionAsync(app, user.User!.Id, "bb-oauth-secret-token");

            var response = await user.Client.PostAsJsonAsync("/api/me/git/bitbucket/repos",
                new { connectionId = connection, owner = "acme", project = "TODO", name = "todo-app", description = "A todo app", @private = true }, Ct);

            var repo = await ProjectApi.ReadAsync<BitbucketRepoDto>(response, HttpStatusCode.Created);
            Assert.Equal(("acme/todo-app", "main", "TODO"), (repo.FullName, repo.MainBranch, repo.ProjectKey));
            var create = HttpJson(Assert.Single(bitbucket.Requests, r => r.Path == "/2.0/repositories/acme/todo-app").Body);
            Assert.Equal(("git", true, "TODO", "A todo app"),
                (create.GetProperty("scm").GetString(), create.GetProperty("is_private").GetBoolean(), create.GetProperty("project").GetProperty("key").GetString(), create.GetProperty("description").GetString()));

            // main holds the README in the "Bitbucket" repository.
            Assert.Equal("Initial commit", await TestGit.RunAsync(root, Ct, "--git-dir", bare, "log", "-1", "--format=%s", "main"));
            Assert.Equal("# todo-app\n\nA todo app", await TestGit.RunAsync(root, Ct, "--git-dir", bare, "show", "main:README.md"));

            // FR-136: the token only travelled in the push's per-command extraHeader (Basic x-token-auth:<token>).
            var push = Assert.Single(git.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "push");
            var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-token-auth:bb-oauth-secret-token"));
            Assert.Contains(push.Config!.Values, v => v == $"Authorization: Basic {header}");
            Assert.All(git.Calls, c => Assert.DoesNotContain(c.Arguments, a => a.Contains("bb-oauth-secret-token", StringComparison.Ordinal) || a.Contains(header, StringComparison.Ordinal)));
            Assert.All(git.Calls.Where(c => c.Arguments[0] != "push"), c => Assert.Null(c.Config));
            Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains("bb-oauth-secret-token", StringComparison.Ordinal));
        }
        finally
        {
            TestGit.DeleteTree(root);
        }
    }

    [Fact]
    public async Task FR135_Create_errors_REPO_NAME_TAKEN_FORBIDDEN_and_UPSTREAM_RATE_LIMITED()
    {
        var bitbucket = new FakeGitHubApi()
            .On(HttpMethod.Post, "/2.0/repositories/acme/taken", HttpStatusCode.BadRequest, """{"type":"error","error":{"message":"Repository with this Slug and Owner already exists."}}""")
            .On(HttpMethod.Post, "/2.0/repositories/acme/denied", HttpStatusCode.Forbidden, """{"type":"error","error":{"message":"You do not have permission to create repositories in this workspace."}}""")
            .On(HttpMethod.Post, "/2.0/repositories/acme/busy", HttpStatusCode.TooManyRequests, """{"type":"error","error":{"message":"Rate limit exceeded"}}""", new Dictionary<string, string> { ["Retry-After"] = "120" });
        var app = BitbucketHarness.App(factory, bitbucket);
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, user.User!.Id);

        Task<HttpResponseMessage> Create(string name, string project = "TODO") =>
            user.Client.PostAsJsonAsync("/api/me/git/bitbucket/repos", new { connectionId = connection, owner = "acme", project, name }, Ct);

        await Problems.AssertAsync(await Create("taken"), HttpStatusCode.Conflict, ErrorCodes.RepoNameTaken);
        await Problems.AssertAsync(await Create("denied"), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        var limited = await Create("busy");
        await Problems.AssertAsync(limited, HttpStatusCode.TooManyRequests, ErrorCodes.UpstreamRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(120), limited.Headers.RetryAfter?.Delta);
        var noProject = await Problems.AssertAsync(await Create("ok", project: " "), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(noProject.GetProperty("errors").TryGetProperty("project", out _));

        // §4.19: after Bitbucket's 429 the connection is held back until Retry-After, without calling Bitbucket.
        var before = bitbucket.Requests.Count;
        await Problems.AssertAsync(await Create("taken"), HttpStatusCode.TooManyRequests, ErrorCodes.UpstreamRateLimited);
        Assert.Equal(before, bitbucket.Requests.Count);
    }

    private static System.Text.Json.JsonElement HttpJson(string body) => System.Text.Json.JsonDocument.Parse(body).RootElement.Clone();
}
