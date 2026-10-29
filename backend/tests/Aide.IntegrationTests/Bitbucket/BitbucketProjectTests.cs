using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aide.Api.Features.Bitbucket;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Stack;
using Aide.Domain.Access;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Git;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Bitbucket;

/// <summary>
/// FR-135 (project binding), FR-136, FR-137, FR-141 against a stub Bitbucket API (never a real Bitbucket) and, for git, a
/// local bare repository standing in for bitbucket.org (<c>Bitbucket:GitBaseUrlOverride</c>, tests only): create and import
/// with a Bitbucket repo bound to the creator's connection, the binding checks, the push and draft pull request with the
/// binding connection's token in the per-command header, the lost binding, re-binding, the import refs, stack detection,
/// the README resume and the Bitbucket-only merge strategies.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class BitbucketProjectTests(AideApiFactory factory)
{
    private const string RepoUuid = "{r-acme-widgets}";
    private const string FullName = "acme/widgets";
    private const string Repo = "/2.0/repositories/acme/widgets";
    private const string AnaToken = "bb-ana-0123456789";
    private const string BenToken = "bb-ben-0123456789";
    private const string HeadSha = "1111111111111111111111111111111111111111";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A stub Bitbucket where <c>acme/widgets</c> exists; each token's permission comes from <paramref name="permission"/>.</summary>
    private static FakeGitHubApi BitbucketApi(Func<string?, string?>? permission = null, bool hasCode = false)
    {
        var levels = permission ?? (_ => "write");
        var repoJson = BitbucketHarness.RepoJson("acme", "widgets");
        return new FakeGitHubApi()
            .On(HttpMethod.Get, "/2.0/repositories/acme/" + Uri.EscapeDataString(RepoUuid), HttpStatusCode.OK, repoJson)
            .On(HttpMethod.Get, "/2.0/repositories/" + Uri.EscapeDataString("{}") + "/" + Uri.EscapeDataString(RepoUuid), HttpStatusCode.OK, repoJson)
            .On(HttpMethod.Get, Repo, HttpStatusCode.OK, repoJson)
            .On(HttpMethod.Get, "/2.0/user/permissions/repositories", r => (HttpStatusCode.OK, levels(r.Authorization) is { } level
                ? $$$"""{"values":[{"permission":"{{{level}}}","repository":{"uuid":"{{{RepoUuid}}}"}}]}"""
                : """{"values":[]}"""))
            .On(HttpMethod.Get, Repo + "/refs/branches/main", HttpStatusCode.OK, $$$"""{"name":"main","target":{"hash":"{{{HeadSha}}}"}}""")
            .On(HttpMethod.Get, $"{Repo}/src/{HeadSha}/", HttpStatusCode.OK,
                hasCode
                    ? """{"values":[{"path":"README.md","type":"commit_file","size":10},{"path":"src","type":"commit_directory"}]}"""
                    : """{"values":[{"path":"README.md","type":"commit_file","size":10}]}""");
    }

    private static object BitbucketGit(Guid connectionId, string repoId = RepoUuid) =>
        new { mode = "bitbucket", connectionId, repoId, repoFullName = FullName };

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, object git, object? stack = null, string source = "scratch", object? import = null) =>
        client.PostAsJsonAsync(
            "/api/projects",
            new { name = ProjectApi.UniqueName("bb"), idea = "Build a todo API.", git, stack, source, import },
            Ct);

    private WebApplicationFactory<Program> App(FakeGitHubApi bitbucket, BareRemote? remote = null, RecordingGitCli[]? recorder = null)
    {
        var settings = new Dictionary<string, string>();
        if (remote is not null)
        {
            settings["Bitbucket:GitBaseUrlOverride"] = remote.BaseUrl;
            settings["Bitbucket:AllowInsecureGitBaseUrl"] = "true";
        }

        var app = BitbucketHarness.App(factory, bitbucket, settings: settings);
        if (recorder is null)
        {
            return app;
        }

        var recorded = app.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var real = services.Last(d => d.ServiceType == typeof(IGitCli));
            services.Remove(real);
            services.AddSingleton<IGitCli>(sp => recorder[0] = new RecordingGitCli(
                real.ImplementationInstance as IGitCli ?? (IGitCli)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!)));
        }));
        _ = recorded.Services.GetRequiredService<IGitCli>();
        return recorded;
    }

    [Fact]
    public async Task FR135_FR136_Creating_a_project_with_a_Bitbucket_repo_binds_the_creators_connection_and_shows_the_binding()
    {
        var app = App(BitbucketApi());
        using var ana = await ApiSession.RegisterAsync(app, name: "Ana");
        var connection = await BitbucketHarness.SeedConnectionAsync(app, ana.User!.Id, AnaToken, login: "ana");
        var instance = await BitbucketHarness.InstanceIdAsync(app);

        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(ana.Client, BitbucketGit(connection)), HttpStatusCode.Created);

        var stored = await ProjectApi.WithDbAsync(app, db => db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct));
        Assert.Equal(
            (GitProvider.Bitbucket, (Guid?)instance, (Guid?)connection, (Guid?)ana.User.Id, RepoUuid, FullName, "https://bitbucket.org/acme/widgets", (string?)null),
            (stored.Provider, stored.InstanceId, stored.GitConnectionId, stored.BoundByUserId, stored.ExternalRepoId, stored.RepoFullName, stored.RepoUrl, stored.TokenEncrypted));

        var settings = await ProjectApi.ReadAsync<ProjectSettingsDto>(await ana.Client.GetAsync(new Uri($"/api/projects/{project.Id}/settings", UriKind.Relative), Ct));
        Assert.Equal((instance, "Bitbucket Cloud", "https://bitbucket.org"), (settings.Git.Instance!.Id, settings.Git.Instance.Name, settings.Git.Instance.BaseUrl));
        Assert.Equal((connection, "ana", "Ana", true), (settings.Git.Binding!.ConnectionId, settings.Git.Binding.Login, settings.Git.Binding.BoundBy.Name, settings.Git.CanRebind));
    }

    [Fact]
    public async Task FR117_R5_FR136_A_Bitbucket_project_needs_vcs_bitbucket_and_create_errors_carry_field_keys()
    {
        var app = App(BitbucketApi(auth => auth == $"Bearer {BenToken}" ? "read" : "write"));
        using var ana = await ApiSession.RegisterAsync(app);
        using var ben = await ApiSession.RegisterAsync(app);
        var anas = await BitbucketHarness.SeedConnectionAsync(app, ana.User!.Id, AnaToken);
        var bens = await BitbucketHarness.SeedConnectionAsync(app, ben.User!.Id, BenToken, login: "ben");

        await Problems.AssertAsync(
            await CreateAsync(ana.Client, BitbucketGit(anas), stack: new { frontend = new { pack = "fe-vue" }, backend = new { pack = "be-express" }, vcs = new { pack = "vcs-github" } }),
            HttpStatusCode.BadRequest,
            ErrorCodes.VcsMismatch);

        AssertField(await Problems.AssertAsync(await CreateAsync(ana.Client, BitbucketGit(bens)), HttpStatusCode.Forbidden, ErrorCodes.CredentialNotOwner), "git.connectionId");
        AssertField(await Problems.AssertAsync(await CreateAsync(ana.Client, BitbucketGit(Guid.CreateVersion7())), HttpStatusCode.NotFound, ErrorCodes.NotFound), "git.connectionId");
        AssertField(await Problems.AssertAsync(await CreateAsync(ben.Client, BitbucketGit(bens)), HttpStatusCode.Forbidden, ErrorCodes.Forbidden), "git.repoId");
        AssertField(await Problems.AssertAsync(await CreateAsync(ana.Client, BitbucketGit(anas, "{r-missing}")), HttpStatusCode.NotFound, ErrorCodes.NotFound), "git.repoId");

        await ProjectApi.WithDbAsync(app, db => db.GitConnections.Where(c => c.Id == anas).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, GitConnectionStatus.Invalid), Ct));
        AssertField(await Problems.AssertAsync(await CreateAsync(ana.Client, BitbucketGit(anas)), HttpStatusCode.Conflict, ErrorCodes.GitNotConnected), "git.connectionId");

        var missing = await Problems.AssertAsync(await CreateAsync(ana.Client, new { mode = "bitbucket" }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(missing.GetProperty("errors").TryGetProperty("git.repoId", out _), missing.ToString());
        Assert.True(missing.GetProperty("errors").TryGetProperty("git.repoFullName", out _), missing.ToString());
    }

    [Fact]
    public async Task FR136_FR137_Given_a_Bitbucket_project_when_execution_runs_then_the_branch_is_pushed_and_a_draft_PR_from_aide_slug_v0_1_0_to_main_is_linked()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var recorder = new RecordingGitCli[1];
        var api = BitbucketApi()
            .On(HttpMethod.Get, Repo + "/pullrequests", HttpStatusCode.OK, """{"values":[]}""")
            .On(HttpMethod.Post, Repo + "/pullrequests", HttpStatusCode.Created,
                """{"id":3,"title":"x","state":"OPEN","draft":true,"links":{"html":{"href":"https://bitbucket.org/acme/widgets/pull-requests/3"}}}""");
        var app = App(api, remote, recorder);
        using var ana = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, ana.User!.Id, AnaToken);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(ana.Client, BitbucketGit(connection)), HttpStatusCode.Created);
        var id = project.Id;

        var tested = await ProjectApi.ReadAsync<GitTestResultDto>(await ana.Client.PostAsync(new Uri($"/api/projects/{id}/settings/git/test", UriKind.Relative), null, Ct));
        Assert.Equal(new GitTestResultDto(true, true, true, true, null), tested);

        await SeedExecutionAsync(app, id);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await ana.Client.PostAsJsonAsync($"/api/projects/{id}/execution/start", new { }, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(app, id);

        var branch = Aide.Domain.Releases.ReleaseVersionRules.BranchName(project.Slug, id, Aide.Domain.Releases.SemVer.Parse(project.DevelopmentVersion!));
        Assert.StartsWith($"aide/{project.Slug}/v0.1.0", branch, StringComparison.Ordinal);
        var workingCopy = Path.Combine(factory.WorkspacesRoot, id.ToString("D"));
        Assert.Equal(await TestGit.RunAsync(workingCopy, Ct, "rev-parse", branch), await TestGit.RunAsync(remote.BarePath, Ct, "rev-parse", $"refs/heads/{branch}"));

        // FR-136: the OAuth token travels only in the per-command Basic x-token-auth header.
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-token-auth:{AnaToken}"));
        var push = recorder[0].Calls.First(c => c.Arguments.Contains("push"));
        Assert.Contains($"Authorization: Basic {basic}", push.Config!.Values);
        Assert.All(recorder[0].Calls, c => Assert.DoesNotContain(c.Arguments, a => a.Contains(AnaToken, StringComparison.Ordinal) || a.Contains(basic, StringComparison.Ordinal)));
        Assert.DoesNotContain(AnaToken, await File.ReadAllTextAsync(Path.Combine(workingCopy, ".git", "config"), Ct), StringComparison.Ordinal);

        // FR-137: a draft PR from the branch to main, linking back to AIDE; the Release page links it.
        var created = Assert.Single(api.Requests, r => r.Method == "POST" && r.Path == Repo + "/pullrequests");
        Assert.Equal($"Bearer {AnaToken}", created.Authorization);
        var body = GitLabHarness.Body(created);
        Assert.Equal(
            (branch, "main", true, false),
            (body.GetProperty("source").GetProperty("branch").GetProperty("name").GetString(), body.GetProperty("destination").GetProperty("branch").GetProperty("name").GetString(),
                body.GetProperty("draft").GetBoolean(), body.GetProperty("close_source_branch").GetBoolean()));
        Assert.Contains($"/projects/{id}", body.GetProperty("description").GetString(), StringComparison.Ordinal);
        var version = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == id, Ct));
        Assert.Equal((3, "https://bitbucket.org/acme/widgets/pull-requests/3"), (version.PullRequestNumber, version.PullRequestUrl));
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(AnaToken, StringComparison.Ordinal) || e.AllText.Contains(basic, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR136_An_API_token_connection_pushes_with_x_bitbucket_api_token_auth()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var recorder = new RecordingGitCli[1];
        var api = BitbucketApi()
            .On(HttpMethod.Get, Repo + "/pullrequests", HttpStatusCode.OK, """{"values":[]}""")
            .On(HttpMethod.Post, Repo + "/pullrequests", HttpStatusCode.Created, """{"id":3,"title":"x","state":"OPEN","draft":true}""");
        var app = App(api, remote, recorder);
        using var ana = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, ana.User!.Id, AnaToken, authKind: GitConnectionAuthKind.Token, email: "ana@example.com");
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(ana.Client, BitbucketGit(connection)), HttpStatusCode.Created);
        await SeedExecutionAsync(app, project.Id);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await ana.Client.PostAsJsonAsync($"/api/projects/{project.Id}/execution/start", new { }, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(app, project.Id);

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-bitbucket-api-token-auth:{AnaToken}"));
        Assert.Contains($"Authorization: Basic {basic}", recorder[0].Calls.First(c => c.Arguments.Contains("push")).Config!.Values);

        // REST calls with an API token use Basic email:token; the PR link falls back to bitbucket.org's page.
        var restBasic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"ana@example.com:{AnaToken}"));
        Assert.Equal(restBasic, Assert.Single(api.Requests, r => r.Method == "POST" && r.Path == Repo + "/pullrequests").Authorization);
        var version = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == project.Id, Ct));
        Assert.Equal("https://bitbucket.org/acme/widgets/pull-requests/3", version.PullRequestUrl);
    }

    [Fact]
    public async Task FR136_Given_the_binding_Owner_leaves_when_a_push_is_due_then_the_job_fails_GIT_NOT_CONNECTED_and_Owners_are_notified()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var app = App(BitbucketApi(), remote);
        using var ana = await ApiSession.RegisterAsync(app, name: "Ana");
        using var ben = await ApiSession.RegisterAsync(app, name: "Ben");
        var connection = await BitbucketHarness.SeedConnectionAsync(app, ana.User!.Id, AnaToken);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(ana.Client, BitbucketGit(connection)), HttpStatusCode.Created);
        var id = project.Id;
        await ProjectApi.AddMemberInDbAsync(app, id, ben.User!.Id, ProjectRole.Owner);
        await SeedExecutionAsync(app, id);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await ana.Client.PostAsJsonAsync($"/api/projects/{id}/execution/start", new { }, Ct), HttpStatusCode.Accepted);

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync(new Uri($"/api/projects/{id}/members/{ana.User.Id}", UriKind.Relative), Ct)).StatusCode);

        var execution = Assert.Single(await AgentJobs.RunQueuedAsync(app, id), j => j.Type == JobTypes.Execution);
        Assert.Equal(JobStatus.Failed, execution.Status);
        Assert.StartsWith(ErrorCodes.GitNotConnected, execution.LastError, StringComparison.Ordinal);
        var cleared = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.GitBindingCleared)).Payload);
        Assert.Equal(("member_removed", "bitbucket"), (cleared.GetProperty("reason").GetString(), cleared.GetProperty("provider").GetString()));
        Assert.Contains(await LifecycleSeed.NotificationJobsAsync(app, id), j => j.Payload.Contains("git_binding_cleared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR136_Another_Owner_rebinds_their_own_Bitbucket_connection_after_the_write_access_check()
    {
        var app = App(BitbucketApi(auth => auth == "Bearer bb-cara-0123456789" ? "read" : "write"));
        using var ana = await ApiSession.RegisterAsync(app);
        using var ben = await ApiSession.RegisterAsync(app, name: "Ben");
        using var cara = await ApiSession.RegisterAsync(app);
        var anas = await BitbucketHarness.SeedConnectionAsync(app, ana.User!.Id, AnaToken);
        var bens = await BitbucketHarness.SeedConnectionAsync(app, ben.User!.Id, BenToken, login: "ben");
        var caras = await BitbucketHarness.SeedConnectionAsync(app, cara.User!.Id, "bb-cara-0123456789", login: "cara");
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(ana.Client, BitbucketGit(anas)), HttpStatusCode.Created);
        var path = $"/api/projects/{project.Id}/settings/git";
        await ProjectApi.AddMemberInDbAsync(app, project.Id, ben.User!.Id, ProjectRole.Owner);
        await ProjectApi.AddMemberInDbAsync(app, project.Id, cara.User!.Id, ProjectRole.Owner);

        // Ana deletes her connection: connection_deleted clears the binding.
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync(new Uri($"/api/me/connections/bitbucket/{anas}", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal("connection_deleted", LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.GitBindingCleared)).Payload).GetProperty("reason").GetString());

        await Problems.AssertAsync(await ProjectApi.PatchAsync(ben.Client, path, new { connectionId = caras }), HttpStatusCode.Forbidden, ErrorCodes.CredentialNotOwner);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(cara.Client, path, new { connectionId = caras }), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        var rebound = await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(ben.Client, path, new { connectionId = bens }));
        Assert.Equal((bens, ben.User.Id), (rebound.Binding!.ConnectionId, rebound.Binding.BoundBy.Id));

        // An insufficient_scope or invalid connection clears it too (the shared FR-136 path).
        await ProjectApi.WithDbAsync(app, async db =>
        {
            await Aide.Infrastructure.Git.GitBindings.ClearForConnectionAsync(db, bens, Aide.Domain.GitHosts.GitBindingClearedReason.ConnectionInvalid, "system", DateTimeOffset.UtcNow, Ct);
            await db.SaveChangesAsync(Ct);
        });
        Assert.Null((await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(ben.Client, path, new { }))).Binding);
    }

    [Fact]
    public async Task FR141_Given_Bitbucket_tags_v1_0_0_and_v1_1_0_when_imported_then_v1_1_0_is_preselected_and_1_2_0_is_the_default()
    {
        const string V100 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string V110 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var app = App(BitbucketApi(hasCode: true)
            .On(HttpMethod.Get, Repo + "/refs/tags", HttpStatusCode.OK,
                $$$"""{"values":[{"name":"v1.0.0","target":{"hash":"{{{V100}}}"}},{"name":"v1.1.0","target":{"hash":"{{{V110}}}"}},{"name":"nightly","target":{"hash":"{{{V110}}}"}}]}""")
            .On(HttpMethod.Get, Repo + "/refs/tags/v1.1.0", HttpStatusCode.OK, $$$"""{"name":"v1.1.0","target":{"hash":"{{{V110}}}"}}"""));
        using var ana = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, ana.User!.Id, AnaToken);
        var refsPath = $"/api/me/git/bitbucket/repos/{Uri.EscapeDataString(RepoUuid)}/refs?connectionId={connection}";

        var refs = await ProjectApi.ReadAsync<BitbucketRepoRefsDto>(await ana.Client.GetAsync(new Uri(refsPath, UriKind.Relative), Ct));
        Assert.Equal(("v1.1.0", "1.2.0", "main", false, FullName), (refs.PreselectedTag, refs.SuggestedDevelopmentVersion, refs.DefaultBranch, refs.Empty, refs.FullName));
        Assert.Equal(new string?[] { "1.0.0", "1.1.0", null }, refs.Tags.Select(t => t.Semver));

        var project = await ProjectApi.ReadAsync<ProjectDto>(
            await CreateAsync(ana.Client, BitbucketGit(connection), source: "import", import: new { releasedTag = refs.PreselectedTag, developmentVersion = refs.SuggestedDevelopmentVersion }),
            HttpStatusCode.Created);
        Assert.Equal(("1.1.0", "1.2.0"), (project.ReleasedVersion, project.DevelopmentVersion));
        var versions = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == project.Id).OrderBy(v => v.CreatedAt).ToListAsync(Ct));
        Assert.Equal(("v1.1.0", V110, V110), (versions[0].Tag, versions[0].MergeCommitSha, versions[1].BaseCommitSha));

        using var ben = await ApiSession.RegisterAsync(app);
        await Problems.AssertAsync(await ben.Client.GetAsync(new Uri(refsPath, UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await ana.Client.GetAsync(new Uri($"/api/me/git/bitbucket/repos/widgets/refs?connectionId={connection}", UriKind.Relative), Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(
            await CreateAsync(ana.Client, BitbucketGit(connection), source: "import", import: new { releasedTag = "v0.9.0", developmentVersion = "1.0.0" }),
            HttpStatusCode.BadRequest,
            ErrorCodes.TagNotFound);
    }

    [Fact]
    public async Task FR141_Stack_detection_reads_a_Bitbucket_repo_with_the_callers_connection()
    {
        const string PackageJson = """{"name":"web","dependencies":{"vue":"^3.5.0"},"devDependencies":{"vite":"^6.0.0"}}""";
        var api = BitbucketApi()
            .On(HttpMethod.Get, Repo + "/commit/main", HttpStatusCode.OK, $$"""{"hash":"{{HeadSha}}"}""")
            .On(HttpMethod.Get, $"{Repo}/src/{HeadSha}/package.json", HttpStatusCode.OK, PackageJson);
        api.On(HttpMethod.Get, $"{Repo}/src/{HeadSha}/", HttpStatusCode.OK,
            $$"""{"values":[{"path":"package.json","type":"commit_file","size":{{PackageJson.Length}}},{"path":"src","type":"commit_directory"},{"path":"src/main.ts","type":"commit_file","size":20}]}""");
        var app = App(api);
        using var ana = await ApiSession.RegisterAsync(app);

        await Problems.AssertAsync(
            await ana.Client.PostAsJsonAsync("/api/stack/detect", new { git = new { provider = "bitbucket", repo = FullName } }, Ct), HttpStatusCode.Conflict, ErrorCodes.GitNotConnected);

        var connection = await BitbucketHarness.SeedConnectionAsync(app, ana.User!.Id, AnaToken);
        var detected = await ProjectApi.ReadAsync<StackDetectionDto>(
            await ana.Client.PostAsJsonAsync("/api/stack/detect", new { git = new { provider = "bitbucket", connectionId = connection, repo = FullName } }, Ct));

        Assert.Equal(("main", HeadSha), (detected.Ref, detected.Commit));
        Assert.Contains(detected.Suggestions, s => s.Choice == "vcs" && s.Option == "vcs-bitbucket");
        Assert.Contains(api.Requests, r => r.Path.EndsWith("/package.json", StringComparison.Ordinal) && r.Authorization == $"Bearer {AnaToken}");
    }

    [Fact]
    public async Task FR140_fast_forward_and_squash_are_refused_outside_Bitbucket()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var problem = await Problems.AssertAsync(
            await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "fast_forward" }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(problem.GetProperty("errors").TryGetProperty("mergeStrategy", out _), problem.ToString());
        await Problems.AssertAsync(
            await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "squash" }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR140_A_project_leaving_Bitbucket_goes_back_to_aide_merges()
    {
        var app = App(BitbucketApi());
        using var ana = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, ana.User!.Id, AnaToken);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(ana.Client, BitbucketGit(connection)), HttpStatusCode.Created);
        var path = $"/api/projects/{project.Id}/settings/git";

        Assert.Equal(MergeStrategy.Squash, (await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(ana.Client, path, new { mergeStrategy = "squash" }))).MergeStrategy);
        var moved = await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(ana.Client, path, new { provider = "local" }));

        Assert.Equal((GitProvider.Local, MergeStrategy.AideMerges), (moved.Provider, moved.MergeStrategy));
        Assert.Equal(MergeStrategy.AideMerges, (await LifecycleSeed.ProjectAsync(app, project.Id)).MergeStrategy);
    }

    [Fact]
    public async Task FR135_A_retry_after_the_README_push_failed_pushes_it_to_the_empty_repository_instead_of_REPO_NAME_TAKEN()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var empty = BitbucketHarness.RepoJson("acme", "fresh", mainBranch: null);
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, "/2.0/repositories/acme/fresh", HttpStatusCode.BadRequest, """{"type":"error","error":{"message":"Repository with this Slug and Owner already exists."}}""")
            .On(HttpMethod.Get, "/2.0/repositories/acme/fresh", HttpStatusCode.OK, empty);
        var app = BitbucketHarness.AppWithGit(factory, api, remote.Root, out var git);
        await InitBareAsync(remote.Root, "acme/fresh");
        using var ana = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, ana.User!.Id, AnaToken);

        var created = await ProjectApi.ReadAsync<BitbucketRepoDto>(
            await ana.Client.PostAsJsonAsync("/api/me/git/bitbucket/repos", new { connectionId = connection, owner = "acme", project = "TODO", name = "fresh" }, Ct),
            HttpStatusCode.Created);

        Assert.Equal(("acme/fresh", "main"), (created.FullName, created.MainBranch));
        Assert.Contains(git.Calls, c => c.Arguments.Contains("push"));

        // An empty repository in another project (or with another visibility) isn't what was asked for: REPO_NAME_TAKEN.
        await Problems.AssertAsync(
            await ana.Client.PostAsJsonAsync("/api/me/git/bitbucket/repos", new { connectionId = connection, owner = "acme", project = "OTHER", name = "fresh" }, Ct),
            HttpStatusCode.Conflict,
            ErrorCodes.RepoNameTaken);
        await Problems.AssertAsync(
            await ana.Client.PostAsJsonAsync("/api/me/git/bitbucket/repos", new { connectionId = connection, owner = "acme", project = "TODO", name = "fresh", @private = false }, Ct),
            HttpStatusCode.Conflict,
            ErrorCodes.RepoNameTaken);

        // A repository that already has commits stays REPO_NAME_TAKEN.
        api.On(HttpMethod.Get, "/2.0/repositories/acme/fresh", HttpStatusCode.OK, BitbucketHarness.RepoJson("acme", "fresh"));
        await Problems.AssertAsync(
            await ana.Client.PostAsJsonAsync("/api/me/git/bitbucket/repos", new { connectionId = connection, owner = "acme", project = "TODO", name = "fresh" }, Ct),
            HttpStatusCode.Conflict,
            ErrorCodes.RepoNameTaken);
    }

    /// <summary>An empty bare repository at <c>{root}/{fullName}.git</c> (bitbucket.org's empty repo).</summary>
    private static async Task InitBareAsync(string root, string fullName)
    {
        var path = Path.Combine(root, fullName.Replace('/', Path.DirectorySeparatorChar) + ".git");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await TestGit.RunAsync(Path.GetDirectoryName(path)!, Ct, "init", "-q", "--bare", path);
    }

    private static void AssertField(JsonElement problem, string field) =>
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out var messages) && messages.GetArrayLength() == 1, problem.ToString());

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
}
