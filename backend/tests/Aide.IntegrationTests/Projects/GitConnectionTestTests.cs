using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Aide.Api.Features.Projects;
using Aide.Domain.Common;
using Aide.Infrastructure.Git;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Projects;

/// <summary>FR-10: the Owner tests the saved Git settings. GitHub is a fake <c>github</c> HttpClient handler and the
/// git CLI is faked, so no test reaches the real GitHub.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitConnectionTestTests(AideApiFactory factory)
{
    private const string Token = "ghp_connectionTestToken0123456789";
    private const string RepoPath = "/repos/acme/widgets";
    private const string BranchPath = "/repos/acme/widgets/branches/main";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR10_Bad_token_reports_GitHubs_reason_and_saves_nothing()
    {
        var github = new FakeGitHub().On(RepoPath, HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
        await using var app = App(github);
        using var fixture = await GitHubProjectAsync(app);
        var before = await SnapshotAsync(fixture.Project.Id);

        var result = await TestAsync(fixture);

        Assert.False(result.Ok);
        Assert.False(result.Reachable);
        Assert.Contains("Bad credentials", result.Reason, StringComparison.Ordinal);
        Assert.Contains("HTTP 401", result.Reason, StringComparison.Ordinal);
        Assert.Contains("invalid or expired", result.Reason, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(fixture.Project.Id));
        Assert.Equal($"Bearer {Token}", github.AuthorizationFor(RepoPath));
    }

    [Fact]
    public async Task FR10_Repository_not_found_is_explained()
    {
        var github = new FakeGitHub().On(RepoPath, HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        await using var app = App(github);
        using var fixture = await GitHubProjectAsync(app);

        var result = await TestAsync(fixture);

        Assert.Equal((false, false, false, false), (result.Ok, result.Reachable, result.Writable, result.DefaultBranchExists));
        Assert.Contains("Not Found (HTTP 404)", result.Reason, StringComparison.Ordinal);
        Assert.Contains("doesn't exist", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR10_Token_without_push_permission_is_not_writable()
    {
        var github = new FakeGitHub()
            .On(RepoPath, HttpStatusCode.OK, """{"permissions":{"pull":true,"push":false}}""")
            .On(BranchPath, HttpStatusCode.OK, """{"name":"main"}""");
        await using var app = App(github);
        using var fixture = await GitHubProjectAsync(app);

        var result = await TestAsync(fixture);

        Assert.Equal((false, true, false, true), (result.Ok, result.Reachable, result.Writable, result.DefaultBranchExists));
        Assert.Contains("can't push", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR10_Missing_default_branch_is_reported()
    {
        var github = new FakeGitHub()
            .On(RepoPath, HttpStatusCode.OK, """{"permissions":{"push":true}}""")
            .On(BranchPath, HttpStatusCode.NotFound, """{"message":"Branch not found"}""");
        await using var app = App(github);
        using var fixture = await GitHubProjectAsync(app);

        var result = await TestAsync(fixture);

        Assert.Equal((false, true, true, false), (result.Ok, result.Reachable, result.Writable, result.DefaultBranchExists));
        Assert.Contains("'main' doesn't exist in acme/widgets", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR10_Reachable_writable_repo_with_branch_is_ok_and_never_echoes_the_token()
    {
        var github = new FakeGitHub()
            .On(RepoPath, HttpStatusCode.OK, """{"permissions":{"push":true}}""")
            .On(BranchPath, HttpStatusCode.OK, """{"name":"main"}""");
        await using var app = App(github);
        using var fixture = await GitHubProjectAsync(app);

        var response = await fixture.Owner.Client.PostAsync(new Uri($"{fixture.Path}/settings/git/test", UriKind.Relative), null, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var result = await ProjectApi.ReadAsync<GitTestResultDto>(response);

        Assert.Equal(new GitTestResultDto(true, true, true, true, null), result);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.Equal($"Bearer {Token}", github.AuthorizationFor(BranchPath));
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR10_Test_is_Owner_only()
    {
        await using var app = App(new FakeGitHub());
        using var fixture = await ProjectApi.FixtureAsync(app);

        var contributor = await fixture.Contributor.Client.PostAsync(new Uri($"{fixture.Path}/settings/git/test", UriKind.Relative), null, Ct);
        var outsider = await fixture.Outsider.Client.PostAsync(new Uri($"{fixture.Path}/settings/git/test", UriKind.Relative), null, Ct);

        await Problems.AssertAsync(contributor, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(outsider, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR10_Local_provider_is_ok_when_the_workspace_is_writable_and_git_runs()
    {
        await using var app = App(new FakeGitHub(), new FakeGitCli("git version 2.47.0"));
        using var fixture = await ProjectApi.FixtureAsync(app);

        var result = await TestAsync(fixture);

        Assert.Equal(new GitTestResultDto(true, true, true, true, null), result);
    }

    [Fact]
    public async Task FR10_Local_provider_fails_without_git()
    {
        await using var app = App(new FakeGitHub(), new FakeGitCli(null));
        using var fixture = await ProjectApi.FixtureAsync(app);

        var result = await TestAsync(fixture);

        Assert.Equal((false, true, true), (result.Ok, result.Reachable, result.Writable));
        Assert.Contains("git command-line tool isn't installed", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR10_Local_provider_unwritable_workspace_reason_hides_the_server_path()
    {
        // A file where the workspace folder should be: creating the folder fails.
        var blocked = Path.Combine(Path.GetTempPath(), "aide-tests-blocked", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(blocked)!);
        await File.WriteAllTextAsync(blocked, "not a folder", Ct);
        try
        {
            await using var app = App(new FakeGitHub(), new FakeGitCli("git version 2.47.0"), workspacesRoot: blocked);
            using var fixture = await ProjectApi.FixtureAsync(app);

            var result = await TestAsync(fixture);

            Assert.Equal((false, false), (result.Ok, result.Writable));
            Assert.Contains("isn't writable", result.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(blocked, result.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("aide-tests-blocked", result.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(factory.Logs.Entries, e => e.AllText.Contains(blocked, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(blocked);
        }
    }

    private WebApplicationFactory<Program> App(FakeGitHub github, FakeGitCli? git = null, string? workspacesRoot = null) =>
        factory.WithWebHostBuilder(b =>
        {
            if (workspacesRoot is not null)
            {
                b.UseSetting("Workspaces:Root", workspacesRoot);
            }

            b.ConfigureTestServices(services =>
            {
                // "github" is the named HttpClient of the GitHub provider.
                services.AddHttpClient("github").ConfigurePrimaryHttpMessageHandler(() => github);
                if (git is not null)
                {
                    services.AddSingleton<IGitCli>(git);
                }
            });
        });

    private static Task<ProjectFixture> GitHubProjectAsync(WebApplicationFactory<Program> app) =>
        ProjectApi.FixtureAsync(app, git: new { provider = "github", repoUrl = "https://github.com/acme/widgets", defaultBranch = "main", token = Token });

    private static async Task<GitTestResultDto> TestAsync(ProjectFixture fixture) =>
        await ProjectApi.ReadAsync<GitTestResultDto>(
            await fixture.Owner.Client.PostAsync(new Uri($"{fixture.Path}/settings/git/test", UriKind.Relative), null, Ct));

    /// <summary>Everything a connection test could change: the Git settings row and the project's events.</summary>
    private Task<string> SnapshotAsync(Guid projectId) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var git = await db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == projectId, Ct);
            var events = await db.Events.CountAsync(e => e.ProjectId == projectId, Ct);
            return $"{git.Provider}|{git.RepoUrl}|{git.DefaultBranch}|{git.BranchName}|{git.TokenEncrypted}|{events}";
        });

    /// <summary>The GitHub REST API, answered in memory per path. Unknown paths are 404.</summary>
    public sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string?> _authorization = new(StringComparer.Ordinal);

        public FakeGitHub On(string path, HttpStatusCode status, string body)
        {
            _routes[path] = (status, body);
            return this;
        }

        public string? AuthorizationFor(string path) => _authorization.TryGetValue(path, out var value) ? value : null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            _authorization[path] = request.Headers.Authorization?.ToString();
            var (status, body) = _routes.TryGetValue(path, out var route) ? route : (HttpStatusCode.NotFound, """{"message":"Not Found"}""");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    public sealed class FakeGitCli(string? version) : IGitCli
    {
        public Task<string?> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult(version);

        public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            throw new GitCommandException("The fake git CLI runs no commands.");
    }
}
