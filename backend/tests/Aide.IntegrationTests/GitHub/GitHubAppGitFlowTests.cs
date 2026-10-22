using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Projects;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.GitHub;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.GitHub;

/// <summary>
/// FR-82, §4.14: a github_app project's Git work end to end — git clones from and pushes to a local bare repository
/// (<see cref="BareRemote"/>) and the REST API is the fake — authenticated with installation tokens minted from the App JWT,
/// scoped to the repository and cached for at most 55 minutes.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitHubAppGitFlowTests(AideApiFactory factory)
{
    private const long Installation = 2002;
    private const long RepoId = 777;
    private const string Pulls = "/repos/acme/widgets/pulls";

    private static readonly string Basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{GitHubAppHarness.InstallationToken}"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR82_Given_an_App_project_when_step_1_commits_then_the_push_uses_an_installation_token_the_commit_is_the_bots_and_the_PR_links_back()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var recorder = new RecordingGitCli[1];
        await using var harness = GitHubAppHarness.Create(
            factory,
            configure: b =>
            {
                b.UseSetting("GitHub:GitBaseUrl", remote.BaseUrl);
                b.UseSetting("GitHub:AllowInsecureGitBaseUrl", "true");
            },
            services: s =>
            {
                var real = s.Last(d => d.ServiceType == typeof(IGitCli));
                s.Remove(real);
                s.AddSingleton<IGitCli>(sp => recorder[0] = new RecordingGitCli((IGitCli)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!)));
            });
        var app = harness.App;
        _ = app.Services.GetRequiredService<IGitCli>();
        var git = recorder[0];
        harness.OnRepoInstallation("acme", "widgets", Installation);
        harness.Api.On(HttpMethod.Get, "/repos/acme/widgets", HttpStatusCode.OK, GitHubAppHarness.RepoJson(RepoId, "acme", "widgets"));
        harness.OnBranchRoot("acme", "widgets", "main", new string('d', 40), "README.md");
        harness.OnInstallationToken(Installation);
        harness.Api.On(HttpMethod.Get, Pulls, HttpStatusCode.OK, "[]");
        harness.Api.On(HttpMethod.Post, Pulls, HttpStatusCode.Created, """{"number":7,"html_url":"https://github.com/acme/widgets/pull/7"}""");

        using var owner = await ApiSession.RegisterAsync(app);
        await harness.SeedConnectionAsync(owner.User!.Id, RandomNumberGenerator.GetInt32(1, int.MaxValue) + 30_000_000_000L, "octo");
        var project = await ProjectApi.ReadAsync<ProjectDto>(
            await owner.Client.PostAsJsonAsync("/api/projects", new { name = ProjectApi.UniqueName("app"), idea = "Build a todo API.", git = new { mode = "github_app", installationId = Installation, repoFullName = "acme/widgets" } }, Ct),
            HttpStatusCode.Created);
        var id = project.Id;

        // FR-10 for github_app: the connection test uses the installation token.
        var tested = await ProjectApi.ReadAsync<GitTestResultDto>(await owner.Client.PostAsync(new Uri($"/api/projects/{id}/settings/git/test", UriKind.Relative), null, Ct));
        Assert.Equal(new GitTestResultDto(true, true, true, true, null), tested);

        await SeedExecutionAsync(app, id);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await owner.Client.PostAsJsonAsync($"/api/projects/{id}/execution/start", new { }, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(app, id);

        // Pushed: the remote branch is the local one, and the commit is attributed to the App bot.
        var branch = Aide.Domain.Releases.ReleaseVersionRules.BranchName(project.Slug, id, Aide.Domain.Releases.SemVer.Parse(project.DevelopmentVersion!));
        var workingCopy = Path.Combine(factory.WorkspacesRoot, id.ToString("D"));
        Assert.Equal(await TestGit.RunAsync(workingCopy, Ct, "rev-parse", branch), await TestGit.RunAsync(remote.BarePath, Ct, "rev-parse", $"refs/heads/{branch}"));
        Assert.Equal(
            $"aide-test[bot] <{GitHubAppHarness.BotUserId}+aide-test[bot]@users.noreply.github.com>",
            await TestGit.RunAsync(remote.BarePath, Ct, "log", "-1", "--format=%an <%ae>", $"refs/heads/{branch}"));
        Assert.Equal("aide-test[bot]", await TestGit.RunAsync(remote.BarePath, Ct, "log", "-1", "--format=%cn", $"refs/heads/{branch}"));

        // The installation token travels only in the per-command extraHeader environment.
        var push = git.Calls.First(c => c.Arguments.Contains("push"));
        Assert.Contains($"Authorization: Basic {Basic}", push.Config!.Values);
        Assert.All(git.Calls, c => Assert.DoesNotContain(c.Arguments, a => a.Contains(GitHubAppHarness.InstallationToken, StringComparison.Ordinal) || a.Contains(Basic, StringComparison.Ordinal)));
        Assert.Equal(remote.BaseUrl + "acme/widgets.git", await TestGit.RunAsync(workingCopy, Ct, "remote", "get-url", "origin"));

        // §8: minted once (cached), scoped to the project's repository.
        var minted = Assert.Single(harness.Api.Requests, r => r.Path == $"/app/installations/{Installation}/access_tokens");
        using (var body = JsonDocument.Parse(minted.Body))
        {
            Assert.Equal([RepoId], body.RootElement.GetProperty("repository_ids").EnumerateArray().Select(e => e.GetInt64()));
        }

        // The PR is opened by the App (installation token) and links back to the AIDE project.
        var pr = harness.Api.Requests.Single(r => r.Method == "POST" && r.Path == Pulls);
        Assert.Equal($"Bearer {GitHubAppHarness.InstallationToken}", pr.Authorization);
        using (var body = JsonDocument.Parse(pr.Body))
        {
            Assert.Contains($"Opened by [AIDE](http://localhost:8080/projects/{id})", body.RootElement.GetProperty("body").GetString(), StringComparison.Ordinal);
        }

        var version = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == id, Ct));
        Assert.Equal((7, "https://github.com/acme/widgets/pull/7"), (version.PullRequestNumber, version.PullRequestUrl));
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);

        // Never on disk, in events, job errors or logs; no user token used for background work.
        var config = await File.ReadAllTextAsync(Path.Combine(workingCopy, ".git", "config"), Ct);
        Assert.DoesNotContain(GitHubAppHarness.InstallationToken, config, StringComparison.Ordinal);
        Assert.DoesNotContain("extraheader", config, StringComparison.OrdinalIgnoreCase);
        var stored = await ProjectApi.WithDbAsync(app, async db =>
            string.Join('\n', await db.Events.AsNoTracking().Where(e => e.ProjectId == id).Select(e => e.Payload).ToListAsync(Ct))
            + string.Join('\n', await db.Jobs.AsNoTracking().Where(j => j.ProjectId == id).Select(j => j.LastError ?? string.Empty).ToListAsync(Ct)));
        Assert.DoesNotContain(GitHubAppHarness.InstallationToken, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(GitHubAppHarness.InstallationToken, StringComparison.Ordinal) || e.AllText.Contains(Basic, StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Api.Requests, r => r.Path.StartsWith(Pulls, StringComparison.Ordinal) && r.Authorization == $"Bearer {GitHubAppHarness.UserToken}");
    }

    [Fact]
    public async Task S4_14_Installation_tokens_are_scoped_to_the_repo_and_cached_for_at_most_55_minutes()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var harness = GitHubAppHarness.Create(factory, services: s => s.AddSingleton<TimeProvider>(clock));
        var minted = 0;
        harness.Api.On(HttpMethod.Post, $"/app/installations/{Installation}/access_tokens", _ =>
        {
            var n = Interlocked.Increment(ref minted);
            return (HttpStatusCode.Created, JsonSerializer.Serialize(new
            {
                token = $"ghs_token{n}",
                expires_at = clock.GetUtcNow().AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                permissions = new { contents = "write" },
            }));
        });
        await using var scope = harness.App.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IGitHubInstallationTokens>();
        var appCredentials = (await scope.ServiceProvider.GetRequiredService<IGitHubAppStore>().GetAsync(Ct))!;

        var first = await tokens.GetAsync(appCredentials, Installation, RepoId, Ct);
        clock.Advance(TimeSpan.FromMinutes(54));
        var cached = await tokens.GetAsync(appCredentials, Installation, RepoId, Ct);
        clock.Advance(TimeSpan.FromMinutes(2));
        var renewed = await tokens.GetAsync(appCredentials, Installation, RepoId, Ct);
        var otherRepo = await tokens.GetAsync(appCredentials, Installation, RepoId + 1, Ct);

        Assert.Equal(("ghs_token1", "ghs_token1", "ghs_token2", "ghs_token3"), (first.Token, cached.Token, renewed.Token, otherRepo.Token));
        var bodies = harness.Api.Requests.Where(r => r.Path == $"/app/installations/{Installation}/access_tokens")
            .Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("repository_ids")[0].GetInt64());
        Assert.Equal([RepoId, RepoId, RepoId + 1], bodies);
    }

    [Fact]
    public async Task FR82_An_App_project_whose_installation_is_gone_fails_preparation_for_good_not_transiently()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        harness.Api.On(HttpMethod.Post, $"/app/installations/{Installation}/access_tokens", HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        await using var scope = harness.App.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IGitProviderResolver>().Resolve(GitProvider.GithubApp);
        var settings = new GitConnectionSettings(Guid.CreateVersion7(), "https://github.com/acme/widgets", "main") { InstallationId = Installation, RepoId = RepoId };

        var outcome = await provider.PublishBranchAsync(settings, "aide/x/v0.1.0", null, Ct);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Transient);
        Assert.Contains("can't access the repository", outcome.Reason, StringComparison.Ordinal);
    }

    private static async Task SeedExecutionAsync(WebApplicationFactory<Program> app, Guid id)
    {
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Execution, 4, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: "# Todo API\n\n## Summary\nManage todos.");
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, content: "# Spec\n\nExpress API.");
        var plan = await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ImplementationPlan, content: AgentJobs.PlanMarkdown(1));
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.ExecutionSteps.Add(new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = plan.Id,
                StepNumber = 1,
                Title = "Step 1",
                Task = "Build part 1.",
                DeclaredFiles = [new DeclaredFile("src/part1.js", FileAction.Create)],
                Dependencies = [],
                Status = StepStatus.Pending,
            });
            await db.SaveChangesAsync(Ct);
        });
    }

    /// <summary>A clock tests move by hand; its timers are the system's.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by)
        {
            lock (_gate)
            {
                _now += by;
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }
}
