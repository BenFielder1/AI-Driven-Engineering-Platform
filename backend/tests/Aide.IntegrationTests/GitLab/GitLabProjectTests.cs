using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aide.Api.Features.Execution;
using Aide.Api.Features.GitLab;
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

namespace Aide.IntegrationTests.GitLab;

/// <summary>
/// FR-132 (project binding), FR-136, FR-137, FR-141 against a stub GitLab API (never a real GitLab) and, for git, a local
/// bare repository standing in for the instance (<c>GitLab:GitBaseUrlOverride</c>, tests only): create and import with a
/// GitLab repo bound to the creator's connection, the binding checks, the push and draft merge request with the binding
/// connection's token in the per-command header, the lost binding (<c>GIT_NOT_CONNECTED</c>, <c>git_binding_cleared</c>),
/// re-binding in Settings → Git, the import refs, and stack detection.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitLabProjectTests(AideApiFactory factory)
{
    private const string RepoId = "77";
    private const string FullPath = "acme/widgets";
    private const string AnaToken = "glpat-ana-0123456789";
    private const string BenToken = "glpat-ben-0123456789";
    private const string HeadSha = "1111111111111111111111111111111111111111";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A stub GitLab where <c>acme/widgets</c> (id 77) exists; each token's access level comes from <paramref name="access"/>.</summary>
    private static FakeGitHubApi GitLabApi(string baseUrl, Func<string?, int?>? access = null, bool hasCode = false)
    {
        var levels = access ?? (_ => 30);
        return new FakeGitHubApi()
            .On(HttpMethod.Get, $"/api/v4/projects/{RepoId}", r =>
                (HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    id = 77,
                    path_with_namespace = FullPath,
                    default_branch = "main",
                    web_url = $"{baseUrl}/{FullPath}",
                    empty_repo = false,
                    permissions = new { project_access = levels(r.Authorization) is { } level ? new { access_level = level } : null, group_access = (object?)null },
                })))
            .On(HttpMethod.Get, $"/api/v4/projects/{RepoId}/repository/branches/main", HttpStatusCode.OK, $$$"""{"name":"main","commit":{"id":"{{{HeadSha}}}"}}""")
            .On(HttpMethod.Get, $"/api/v4/projects/{RepoId}/repository/tree", HttpStatusCode.OK,
                hasCode ? """[{"name":"README.md","type":"blob","path":"README.md"},{"name":"src","type":"tree","path":"src"}]""" : """[{"name":"README.md","type":"blob","path":"README.md"}]""");
    }

    private static WebApplicationFactory<Program> App(
        WebApplicationFactory<Program> factory, FakeGitHubApi gitlab, BareRemote? remote = null, RecordingGitCli[]? recorder = null, StubGitHostDns? dns = null)
    {
        var settings = new Dictionary<string, string>();
        if (remote is not null)
        {
            settings["GitLab:GitBaseUrlOverride"] = remote.BaseUrl;
            settings["GitLab:AllowInsecureGitBaseUrl"] = "true";
        }

        var app = GitLabHarness.App(factory, gitlab, dns, settings: settings);
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

    private static object GitLabGit(Guid instanceId, Guid connectionId) =>
        new { mode = "gitlab", instanceId, connectionId, repoId = RepoId, repoFullName = FullPath };

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, object git, object? stack = null, string source = "scratch", object? import = null) =>
        client.PostAsJsonAsync(
            "/api/projects",
            new { name = ProjectApi.UniqueName("gl"), idea = "Build a todo API.", git, stack, source, import },
            Ct);

    private async Task<(WebApplicationFactory<Program> App, string BaseUrl, Guid Instance)> SetUpAsync(
        Func<string, FakeGitHubApi> gitlab, BareRemote? remote = null, RecordingGitCli[]? recorder = null, Func<string, StubGitHostDns>? dns = null)
    {
        var baseUrl = GitLabHarness.UniqueBaseUrl();
        var app = App(factory, gitlab(baseUrl), remote, recorder, dns?.Invoke(new Uri(baseUrl).Host));
        var instance = await GitLabHarness.SeedInstanceAsync(app, baseUrl, oauth: false);
        return (app, baseUrl, instance);
    }

    [Fact]
    public async Task FR132_FR136_Creating_a_project_with_a_GitLab_repo_binds_the_creators_connection_and_shows_the_binding()
    {
        var (app, baseUrl, instance) = await SetUpAsync(b => GitLabApi(b));
        using var ana = await ApiSession.RegisterAsync(app, name: "Ana");
        var connection = await GitLabHarness.SeedConnectionAsync(app, ana.User!.Id, instance, AnaToken, login: "ana");

        var project = await ProjectApi.ReadAsync<ProjectDto>(
            await CreateAsync(ana.Client, GitLabGit(instance, connection)),
            HttpStatusCode.Created);

        var stored = await ProjectApi.WithDbAsync(app, db => db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct));
        Assert.Equal(
            (GitProvider.Gitlab, (Guid?)instance, (Guid?)connection, (Guid?)ana.User.Id, RepoId, FullPath, $"{baseUrl}/{FullPath}", (string?)null),
            (stored.Provider, stored.InstanceId, stored.GitConnectionId, stored.BoundByUserId, stored.ExternalRepoId, stored.RepoFullName, stored.RepoUrl, stored.TokenEncrypted));

        var settings = await ProjectApi.ReadAsync<ProjectSettingsDto>(await ana.Client.GetAsync(new Uri($"/api/projects/{project.Id}/settings", UriKind.Relative), Ct));
        Assert.Equal((instance, "Acme GitLab", baseUrl), (settings.Git.Instance!.Id, settings.Git.Instance.Name, settings.Git.Instance.BaseUrl));
        Assert.Equal((connection, "ana", ana.User.Id, "Ana", true), (settings.Git.Binding!.ConnectionId, settings.Git.Binding.Login, settings.Git.Binding.BoundBy.Id, settings.Git.Binding.BoundBy.Name, settings.Git.CanRebind));
        Assert.Equal(($"{baseUrl}/{FullPath}", RepoId, FullPath), (settings.Git.RepoWebUrl, settings.Git.ExternalRepoId, settings.Git.RepoFullName));
    }

    [Fact]
    public async Task FR117_R5_A_GitLab_project_with_the_GitHub_VCS_is_VCS_MISMATCH()
    {
        var (app, _, instance) = await SetUpAsync(b => GitLabApi(b));
        using var ana = await ApiSession.RegisterAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, ana.User!.Id, instance, AnaToken);

        var response = await CreateAsync(ana.Client, GitLabGit(instance, connection), stack: new { frontend = new { pack = "fe-vue" }, backend = new { pack = "be-express" }, vcs = new { pack = "vcs-github" } });

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.VcsMismatch);
    }

    [Fact]
    public async Task FR132_FR136_Create_refuses_another_users_connection_an_invalid_one_and_less_than_Developer_access()
    {
        var (app, _, instance) = await SetUpAsync(b => GitLabApi(b, auth => auth == $"Bearer {BenToken}" ? 20 : 30));
        using var ana = await ApiSession.RegisterAsync(app);
        using var ben = await ApiSession.RegisterAsync(app);
        var anas = await GitLabHarness.SeedConnectionAsync(app, ana.User!.Id, instance, AnaToken);
        var bens = await GitLabHarness.SeedConnectionAsync(app, ben.User!.Id, instance, BenToken, externalUserId: "5151", login: "ben");

        // Each failure names its field in errors (the VALIDATION shape), with the code and status unchanged.
        AssertField(await Problems.AssertAsync(await CreateAsync(ana.Client, GitLabGit(instance, bens)), HttpStatusCode.Forbidden, ErrorCodes.CredentialNotOwner), "git.connectionId");
        AssertField(await Problems.AssertAsync(await CreateAsync(ana.Client, GitLabGit(instance, Guid.CreateVersion7())), HttpStatusCode.NotFound, ErrorCodes.NotFound), "git.connectionId");
        AssertField(await Problems.AssertAsync(await CreateAsync(ben.Client, GitLabGit(instance, bens)), HttpStatusCode.Forbidden, ErrorCodes.Forbidden), "git.repoId");
        AssertField(
            await Problems.AssertAsync(await CreateAsync(ana.Client, new { mode = "gitlab", instanceId = instance, connectionId = anas, repoId = "404", repoFullName = FullPath }), HttpStatusCode.NotFound, ErrorCodes.NotFound),
            "git.repoId");

        var other = await GitLabHarness.SeedInstanceAsync(app, oauth: false);
        var invalid = await Problems.AssertAsync(await CreateAsync(ana.Client, GitLabGit(other, anas)), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(invalid.GetProperty("errors").TryGetProperty("git.connectionId", out _), invalid.ToString());

        await ProjectApi.WithDbAsync(app, db => db.GitConnections.Where(c => c.Id == anas).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, GitConnectionStatus.Invalid), Ct));
        AssertField(await Problems.AssertAsync(await CreateAsync(ana.Client, GitLabGit(instance, anas)), HttpStatusCode.Conflict, ErrorCodes.GitNotConnected), "git.connectionId");

        var missing = await Problems.AssertAsync(await CreateAsync(ana.Client, new { mode = "gitlab" }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(missing.GetProperty("errors").TryGetProperty("git.repoId", out _), missing.ToString());
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.Projects.AnyAsync(p => db.ProjectMembers.Any(m => m.ProjectId == p.Id && (m.UserId == ana.User.Id || m.UserId == ben.User.Id)), Ct)));
    }

    [Fact]
    public async Task FR141_Given_tags_v1_0_0_and_v1_1_0_when_imported_then_v1_1_0_is_preselected_and_1_2_0_is_the_default()
    {
        var (app, _, instance) = await SetUpAsync(b => GitLabApi(b, hasCode: true)
            .On(HttpMethod.Get, $"/api/v4/projects/{RepoId}/releases", HttpStatusCode.OK, "[]")
            .On(HttpMethod.Get, $"/api/v4/projects/{RepoId}/repository/tags", HttpStatusCode.OK,
                """[{"name":"v1.1.0","commit":{"id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}},{"name":"v1.0.0","commit":{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}]""")
            .On(HttpMethod.Get, $"/api/v4/projects/{RepoId}/repository/tags/v1.1.0", HttpStatusCode.OK,
                """{"name":"v1.1.0","commit":{"id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}"""));
        using var ana = await ApiSession.RegisterAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, ana.User!.Id, instance, AnaToken);

        var refs = await ProjectApi.ReadAsync<GitLabRepoRefsDto>(
            await ana.Client.GetAsync(new Uri($"/api/me/git/gitlab/repos/{RepoId}/refs?connectionId={connection}", UriKind.Relative), Ct));
        Assert.Equal(("v1.1.0", "1.2.0", "main", false), (refs.PreselectedTag, refs.SuggestedDevelopmentVersion, refs.DefaultBranch, refs.Empty));
        Assert.Equal(["v1.1.0", "v1.0.0"], refs.Tags.Select(t => t.Name));
        Assert.Equal(["1.1.0", "1.0.0"], refs.Tags.Select(t => t.Semver));

        var project = await ProjectApi.ReadAsync<ProjectDto>(
            await CreateAsync(ana.Client, GitLabGit(instance, connection), source: "import", import: new { releasedTag = refs.PreselectedTag, developmentVersion = refs.SuggestedDevelopmentVersion }),
            HttpStatusCode.Created);

        Assert.Equal(("1.1.0", "1.2.0"), (project.ReleasedVersion, project.DevelopmentVersion));
        var versions = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == project.Id).OrderBy(v => v.CreatedAt).ToListAsync(Ct));
        Assert.Equal(("v1.1.0", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), (versions[0].Tag, versions[0].MergeCommitSha));
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", versions[1].BaseCommitSha);

        // Another user's connection is 404 on the refs endpoint; a tag that doesn't exist is TAG_NOT_FOUND on import.
        using var ben = await ApiSession.RegisterAsync(app);
        await Problems.AssertAsync(await ben.Client.GetAsync(new Uri($"/api/me/git/gitlab/repos/{RepoId}/refs?connectionId={connection}", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await CreateAsync(ana.Client, GitLabGit(instance, connection), source: "import", import: new { releasedTag = "v0.9.0", developmentVersion = "1.0.0" }),
            HttpStatusCode.BadRequest,
            ErrorCodes.TagNotFound);
    }

    [Fact]
    public async Task FR136_FR137_Given_a_GitLab_project_when_execution_runs_then_the_branch_is_pushed_and_a_draft_MR_is_opened()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var recorder = new RecordingGitCli[1];
        FakeGitHubApi? api = null;
        var (app, baseUrl, instance) = await SetUpAsync(
            b => api = GitLabApi(b)
                .On(HttpMethod.Get, $"/api/v4/projects/{RepoId}/merge_requests", HttpStatusCode.OK, "[]")
                .On(HttpMethod.Post, $"/api/v4/projects/{RepoId}/merge_requests", HttpStatusCode.Created,
                    $$"""{"iid":3,"title":"Draft: x","web_url":"{{b}}/{{FullPath}}/-/merge_requests/3","state":"opened","source_branch":"x","target_branch":"main"}"""),
            remote,
            recorder);
        using var ana = await ApiSession.RegisterAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, ana.User!.Id, instance, AnaToken);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(ana.Client, GitLabGit(instance, connection)), HttpStatusCode.Created);
        var id = project.Id;

        // FR-10 for gitlab: the connection test uses the binding connection.
        var tested = await ProjectApi.ReadAsync<GitTestResultDto>(await ana.Client.PostAsync(new Uri($"/api/projects/{id}/settings/git/test", UriKind.Relative), null, Ct));
        Assert.Equal(new GitTestResultDto(true, true, true, true, null), tested);

        await SeedExecutionAsync(app, id);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await ana.Client.PostAsJsonAsync($"/api/projects/{id}/execution/start", new { }, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(app, id);

        // Pushed to aide/<slug>/v<version>.
        var branch = Aide.Domain.Releases.ReleaseVersionRules.BranchName(project.Slug, id, Aide.Domain.Releases.SemVer.Parse(project.DevelopmentVersion!));
        Assert.StartsWith($"aide/{project.Slug}/v", branch, StringComparison.Ordinal);
        var workingCopy = Path.Combine(factory.WorkspacesRoot, id.ToString("D"));
        Assert.Equal(await TestGit.RunAsync(workingCopy, Ct, "rev-parse", branch), await TestGit.RunAsync(remote.BarePath, Ct, "rev-parse", $"refs/heads/{branch}"));

        // The token travels only in the per-command Basic oauth2 header.
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"oauth2:{AnaToken}"));
        var push = recorder[0].Calls.First(c => c.Arguments.Contains("push"));
        Assert.Contains($"Authorization: Basic {basic}", push.Config!.Values);
        Assert.All(recorder[0].Calls, c => Assert.DoesNotContain(c.Arguments, a => a.Contains(AnaToken, StringComparison.Ordinal) || a.Contains(basic, StringComparison.Ordinal)));
        Assert.Equal($"{remote.BaseUrl}{FullPath}.git", await TestGit.RunAsync(workingCopy, Ct, "remote", "get-url", "origin"));

        // One draft MR from the branch to main, linking back to AIDE, never removing the source branch.
        var created = Assert.Single(api!.Requests, r => r.Method == "POST" && r.Path == $"/api/v4/projects/{RepoId}/merge_requests");
        Assert.Equal($"Bearer {AnaToken}", created.Authorization);
        var body = GitLabHarness.Body(created);
        Assert.StartsWith("Draft: ", body.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Equal((branch, "main", false), (body.GetProperty("source_branch").GetString(), body.GetProperty("target_branch").GetString(), body.GetProperty("remove_source_branch").GetBoolean()));
        Assert.Contains($"/projects/{id}", body.GetProperty("description").GetString(), StringComparison.Ordinal);

        // The Release page links it through the version's PR URL.
        var version = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == id, Ct));
        Assert.Equal((3, $"{baseUrl}/{FullPath}/-/merge_requests/3"), (version.PullRequestNumber, version.PullRequestUrl));

        // Never on disk, in events, job errors or logs.
        var config = await File.ReadAllTextAsync(Path.Combine(workingCopy, ".git", "config"), Ct);
        Assert.DoesNotContain(AnaToken, config, StringComparison.Ordinal);
        var stored = await ProjectApi.WithDbAsync(app, async db =>
            string.Join('\n', await db.Events.AsNoTracking().Where(e => e.ProjectId == id).Select(e => e.Payload).ToListAsync(Ct))
            + string.Join('\n', await db.Jobs.AsNoTracking().Where(j => j.ProjectId == id).Select(j => j.LastError ?? string.Empty).ToListAsync(Ct)));
        Assert.DoesNotContain(AnaToken, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(AnaToken, StringComparison.Ordinal) || e.AllText.Contains(basic, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR136_Given_the_binding_Owner_leaves_when_a_push_is_due_then_the_job_fails_GIT_NOT_CONNECTED_and_Owners_are_notified()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var (app, _, instance) = await SetUpAsync(b => GitLabApi(b), remote);
        using var ana = await ApiSession.RegisterAsync(app, name: "Ana");
        using var ben = await ApiSession.RegisterAsync(app, name: "Ben");
        var connection = await GitLabHarness.SeedConnectionAsync(app, ana.User!.Id, instance, AnaToken);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(ana.Client, GitLabGit(instance, connection)), HttpStatusCode.Created);
        var id = project.Id;
        await ProjectApi.AddMemberInDbAsync(app, id, ben.User!.Id, ProjectRole.Owner);
        await SeedExecutionAsync(app, id);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await ana.Client.PostAsJsonAsync($"/api/projects/{id}/execution/start", new { }, Ct), HttpStatusCode.Accepted);

        // Ana (the binding Owner) leaves before the step's push.
        var left = await ana.Client.DeleteAsync(new Uri($"/api/projects/{id}/members/{ana.User.Id}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);

        var jobs = await AgentJobs.RunQueuedAsync(app, id);
        var execution = Assert.Single(jobs, j => j.Type == JobTypes.Execution);
        Assert.Equal(JobStatus.Failed, execution.Status);
        Assert.StartsWith(ErrorCodes.GitNotConnected, execution.LastError, StringComparison.Ordinal);

        var stored = await ProjectApi.WithDbAsync(app, db => db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == id, Ct));
        Assert.Equal(((Guid?)null, (Guid?)null), (stored.GitConnectionId, stored.BoundByUserId));
        var cleared = Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.GitBindingCleared));
        var payload = LifecycleSeed.Payload(cleared.Payload);
        Assert.Equal(("member_removed", connection, ana.User.Id, "gitlab"), (payload.GetProperty("reason").GetString(), payload.GetProperty("gitConnectionId").GetGuid(), payload.GetProperty("boundByUserId").GetGuid(), payload.GetProperty("provider").GetString()));
        Assert.Contains(await LifecycleSeed.NotificationJobsAsync(app, id), j => j.Payload.Contains("git_binding_cleared", StringComparison.Ordinal));

        // A new start is refused up front.
        await Problems.AssertAsync(await ben.Client.PostAsJsonAsync($"/api/projects/{id}/execution/start", new { }, Ct), HttpStatusCode.Conflict, ErrorCodes.GitNotConnected);
    }

    [Fact]
    public async Task FR136_Another_Owner_rebinds_their_own_connection_after_the_access_check()
    {
        var (app, _, instance) = await SetUpAsync(b => GitLabApi(b, auth => auth == "Bearer glpat-cara-0123456789" ? 20 : 30));
        using var ana = await ApiSession.RegisterAsync(app);
        using var ben = await ApiSession.RegisterAsync(app, name: "Ben");
        using var cara = await ApiSession.RegisterAsync(app);
        var anas = await GitLabHarness.SeedConnectionAsync(app, ana.User!.Id, instance, AnaToken);
        var bens = await GitLabHarness.SeedConnectionAsync(app, ben.User!.Id, instance, BenToken, externalUserId: "5151", login: "ben");
        var caras = await GitLabHarness.SeedConnectionAsync(app, cara.User!.Id, instance, "glpat-cara-0123456789", externalUserId: "6161", login: "cara");
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(ana.Client, GitLabGit(instance, anas)), HttpStatusCode.Created);
        var path = $"/api/projects/{project.Id}/settings/git";
        await ProjectApi.AddMemberInDbAsync(app, project.Id, ben.User!.Id, ProjectRole.Owner);
        await ProjectApi.AddMemberInDbAsync(app, project.Id, cara.User!.Id, ProjectRole.Owner);

        // Ana deletes her connection: the binding is cleared (connection_deleted).
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync(new Uri($"/api/me/connections/gitlab/{anas}", UriKind.Relative), Ct)).StatusCode);
        var unbound = await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(ben.Client, path, new { }));
        Assert.Null(unbound.Binding);
        var cleared = Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.GitBindingCleared));
        Assert.Equal("connection_deleted", LifecycleSeed.Payload(cleared.Payload).GetProperty("reason").GetString());

        // Only with your own connection, and only with Developer access.
        await Problems.AssertAsync(await ProjectApi.PatchAsync(ben.Client, path, new { connectionId = caras }), HttpStatusCode.Forbidden, ErrorCodes.CredentialNotOwner);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(cara.Client, path, new { connectionId = caras }), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        var rebound = await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(ben.Client, path, new { connectionId = bens }));
        Assert.Equal((bens, ben.User.Id, "Ben", "ben"), (rebound.Binding!.ConnectionId, rebound.Binding.BoundBy.Id, rebound.Binding.BoundBy.Name, rebound.Binding.Login));
        var stored = await ProjectApi.WithDbAsync(app, db => db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct));
        Assert.Equal(((Guid?)bens, (Guid?)ben.User.Id), (stored.GitConnectionId, stored.BoundByUserId));

        // Ben is demoted: the binding goes with his Owner role.
        Assert.True((await ProjectApi.PatchAsync(cara.Client, $"/api/projects/{project.Id}/members/{ben.User.Id}", new { role = "contributor" })).IsSuccessStatusCode);
        var events = await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.GitBindingCleared);
        Assert.Contains(events, e => LifecycleSeed.Payload(e.Payload).GetProperty("reason").GetString() == "owner_role_lost");
    }

    [Fact]
    public async Task FR136_A_connection_GitLab_rejects_is_invalid_and_its_projects_lose_the_binding()
    {
        // GitLab rejects the token on /user (a personal access token can't be refreshed).
        var (app, _, instance) = await SetUpAsync(b => GitLabApi(b)
            .On(HttpMethod.Get, "/api/v4/user", HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}"""));
        using var ana = await ApiSession.RegisterAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, ana.User!.Id, instance, AnaToken);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(ana.Client, GitLabGit(instance, connection)), HttpStatusCode.Created);

        // The connection test (a GitLab call with the token) marks it invalid.
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsync(new Uri($"/api/me/connections/gitlab/{connection}/test", UriKind.Relative), null, Ct)).StatusCode);
        Assert.Equal(GitConnectionStatus.Invalid, await ProjectApi.WithDbAsync(app, db => db.GitConnections.Where(c => c.Id == connection).Select(c => c.Status).SingleAsync(Ct)));

        var stored = await ProjectApi.WithDbAsync(app, db => db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct));
        Assert.Null(stored.GitConnectionId);
        var cleared = Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.GitBindingCleared));
        Assert.Equal("connection_invalid", LifecycleSeed.Payload(cleared.Payload).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task FR136_S8_Git_never_reaches_an_instance_whose_host_now_resolves_to_a_refused_address()
    {
        // The API is the stub (its handler bypasses the network); git goes through the SSRF guard, whose DNS now answers the
        // metadata address for the instance (DNS rebinding after the instance was added).
        var (app, _, instance) = await SetUpAsync(b => GitLabApi(b), dns: host => new StubGitHostDns((host, "169.254.169.254")));
        using var ana = await ApiSession.RegisterAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, ana.User!.Id, instance, AnaToken);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(ana.Client, GitLabGit(instance, connection)), HttpStatusCode.Created);
        await SeedExecutionAsync(app, project.Id);

        var refused = await ana.Client.PostAsJsonAsync($"/api/projects/{project.Id}/execution/start", new { }, Ct);

        await Problems.AssertAsync(refused, HttpStatusCode.BadRequest, ErrorCodes.GitHostNotAllowed);
        Assert.False(Directory.Exists(Path.Combine(factory.WorkspacesRoot, project.Id.ToString("D"), ".git")));
    }

    [Theory]
    [InlineData("""{"id":9,"revoked":true,"active":false,"scopes":["api","write_repository"],"expires_at":null}""", "invalid")]
    [InlineData("""{"id":9,"revoked":false,"active":true,"scopes":["api","write_repository"],"expires_at":"2020-01-01"}""", "invalid")]
    [InlineData("""{"id":9,"revoked":false,"active":true,"scopes":["api"],"expires_at":null}""", "insufficient_scope")]
    public async Task FR136_A_token_found_revoked_expired_or_missing_a_scope_clears_the_binding_and_notifies_Owners(string tokenSelf, string status)
    {
        var (app, _, instance) = await SetUpAsync(b => GitLabApi(b)
            .On(HttpMethod.Get, "/api/v4/user", HttpStatusCode.OK, """{"id":4242,"username":"ana","name":"Ana","state":"active"}""")
            .On(HttpMethod.Get, "/api/v4/personal_access_tokens/self", HttpStatusCode.OK, tokenSelf));
        using var ana = await ApiSession.RegisterAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, ana.User!.Id, instance, AnaToken);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(ana.Client, GitLabGit(instance, connection)), HttpStatusCode.Created);

        var tested = await ProjectApi.ReadAsync<GitLabConnectionDto>(await ana.Client.PostAsync(new Uri($"/api/me/connections/gitlab/{connection}/test", UriKind.Relative), null, Ct));

        Assert.Equal(status, tested.Status.ToWire());
        var stored = await ProjectApi.WithDbAsync(app, db => db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct));
        Assert.Equal(((Guid?)null, (Guid?)null), (stored.GitConnectionId, stored.BoundByUserId));
        var cleared = Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.GitBindingCleared));
        Assert.Equal("connection_invalid", LifecycleSeed.Payload(cleared.Payload).GetProperty("reason").GetString());
        Assert.Contains(await LifecycleSeed.NotificationJobsAsync(app, project.Id), j => j.Payload.Contains("git_binding_cleared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR136_Rebinding_a_project_that_is_not_on_GitLab_or_moving_onto_GitLab_is_VALIDATION()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var path = $"{fixture.Path}/settings/git";

        var rebind = await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Owner.Client, path, new { connectionId = Guid.CreateVersion7() }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(rebind.GetProperty("errors").TryGetProperty("connectionId", out _), rebind.ToString());
        var move = await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Owner.Client, path, new { provider = "gitlab" }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(move.GetProperty("errors").TryGetProperty("provider", out _), move.ToString());
        await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Contributor.Client, path, new { connectionId = Guid.CreateVersion7() }), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task FR141_Stack_detection_reads_a_GitLab_repo_with_the_callers_connection()
    {
        const string Commit = "2222222222222222222222222222222222222222";
        const string PackageJson = """{"name":"web","dependencies":{"vue":"^3.5.0"},"devDependencies":{"vite":"^6.0.0"}}""";
        FakeGitHubApi? api = null;
        var (app, _, instance) = await SetUpAsync(b => api = GitLabApi(b)
            .On(HttpMethod.Get, $"/api/v4/projects/{RepoId}/repository/commits/main", HttpStatusCode.OK, $$"""{"id":"{{Commit}}"}""")
            .On(HttpMethod.Head, $"/api/v4/projects/{RepoId}/repository/files/package.json", HttpStatusCode.OK, string.Empty,
                new Dictionary<string, string> { ["X-Gitlab-Size"] = PackageJson.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            .On(HttpMethod.Get, $"/api/v4/projects/{RepoId}/repository/files/package.json/raw", HttpStatusCode.OK, PackageJson));
        api!.On(HttpMethod.Get, $"/api/v4/projects/{RepoId}/repository/tree", HttpStatusCode.OK,
            """[{"name":"package.json","type":"blob","path":"package.json"},{"name":"src","type":"tree","path":"src"},{"name":"main.ts","type":"blob","path":"src/main.ts"}]""");
        using var ana = await ApiSession.RegisterAsync(app);

        var notConnected = await ana.Client.PostAsJsonAsync("/api/stack/detect", new { git = new { provider = "gitlab", instanceId = instance, repo = RepoId } }, Ct);
        await Problems.AssertAsync(notConnected, HttpStatusCode.Conflict, ErrorCodes.GitNotConnected);

        await GitLabHarness.SeedConnectionAsync(app, ana.User!.Id, instance, AnaToken);
        var detected = await ProjectApi.ReadAsync<StackDetectionDto>(
            await ana.Client.PostAsJsonAsync("/api/stack/detect", new { git = new { provider = "gitlab", instanceId = instance, repo = RepoId } }, Ct));

        Assert.Equal(("main", Commit), (detected.Ref, detected.Commit));
        Assert.Contains(detected.Suggestions, s => s.Choice == "vcs" && s.Option == "vcs-gitlab");
        Assert.Contains(api.Requests, r => r.Path.EndsWith("/package.json/raw", StringComparison.Ordinal) && r.Authorization == $"Bearer {AnaToken}");

        var wrongProvider = await Problems.AssertAsync(
            await ana.Client.PostAsJsonAsync("/api/stack/detect", new { git = new { provider = "local", connectionId = Guid.CreateVersion7(), repo = "x" } }, Ct),
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation);
        Assert.True(wrongProvider.GetProperty("errors").TryGetProperty("git.connectionId", out _), wrongProvider.ToString());
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
