using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Deployments;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Deployments;

/// <summary>
/// FR-88: Settings → Deployment and the wizard's Deployment step against a stub Vercel API (never api.vercel.com) or the
/// Fake provider: linking, creating, Vercel's refusals, required env vars, binding rules.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class DeploymentSettingsTests(AideApiFactory factory)
{
    private static readonly object PatGit = new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_deploySettingsToken" };

    private static readonly string[] PreviewOnly = ["preview"];

    private static readonly string[] ProductionOnly = ["production"];
    private static readonly string[] Both = ["preview", "production"];
    private static readonly string[] BothReversed = ["production", "preview"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Settings(Guid projectId) => $"/api/projects/{projectId}/settings/deployment";

    [Fact]
    public async Task FR88_Linking_an_existing_Vercel_project_binds_the_Owners_connection_members_see_connected_by()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v9/projects/prj_todo", HttpStatusCode.OK, """{"id":"prj_todo","name":"todo","link":{"type":"github","org":"acme","repo":"todo","repoId":42}}""");
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var fixture = await ProjectApi.FixtureAsync(app, git: PatGit);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id, teamId: "team_acme");

        var saved = await ProjectApi.ReadAsync<DeploymentSettingsDto>(
            await ProjectApi.PatchAsync(fixture.Owner.Client, Settings(fixture.Project.Id), new { provider = "vercel", connectionId = connection, vercelProjectId = "prj_todo" }));

        Assert.Equal((DeploymentProvider?)DeploymentProvider.Vercel, saved.Provider);
        Assert.Equal(("prj_todo", "todo", "https://vercel.com/acme-team/todo"), (saved.VercelProject!.Id, saved.VercelProject.Name, saved.VercelProject.Url));
        Assert.Equal((connection, fixture.Owner.User!.Id, true), (saved.Binding!.ConnectionId, saved.Binding.BoundBy.Id, saved.CanBind));
        Assert.Contains(vercel.Requests, r => r.Path == "/v9/projects/prj_todo" && r.Query.Contains("teamId=team_acme", StringComparison.Ordinal));

        var asViewer = await ProjectApi.ReadAsync<DeploymentSettingsDto>(await fixture.Viewer.Client.GetAsync(new Uri(Settings(fixture.Project.Id), UriKind.Relative), Ct));
        Assert.Equal((fixture.Owner.User!.Id, false, true), (asViewer.Binding!.BoundBy.Id, asViewer.CanBind, asViewer.Supported));
        Assert.True((await ProjectApi.ReadAsync<ProjectDto>(await fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{fixture.Project.Id}", UriKind.Relative), Ct))).DeploymentsEnabled);
        Assert.Contains(await LifecycleSeed.EventsAsync(app, fixture.Project.Id, EventTypes.SettingsUpdated), e => e.Payload.Contains("vercelProject", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR88_Creating_a_Vercel_project_links_it_to_the_GitHub_repo_with_framework_nextjs_and_the_root_directory()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Post, "/v11/projects", HttpStatusCode.OK, """{"id":"prj_new","name":"todo-web","link":{"type":"github","org":"acme","repo":"todo"}}""");
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id);

        var saved = await ProjectApi.ReadAsync<DeploymentSettingsDto>(await ProjectApi.PatchAsync(
            owner.Client, Settings(project.Id), new { provider = "vercel", connectionId = connection, create = new { name = "todo-web", rootDirectory = "apps/web" } }));

        Assert.Equal(("prj_new", "apps/web", "nextjs"), (saved.VercelProject!.Id, saved.RootDirectory, saved.Framework));
        var body = JsonDocument.Parse(Assert.Single(vercel.Requests, r => r.Method == "POST").Body).RootElement;
        Assert.Equal(("todo-web", "nextjs", "github", "acme/todo", "apps/web"), (
            body.GetProperty("name").GetString(),
            body.GetProperty("framework").GetString(),
            body.GetProperty("gitRepository").GetProperty("type").GetString(),
            body.GetProperty("gitRepository").GetProperty("repo").GetString(),
            body.GetProperty("rootDirectory").GetString()));
    }

    [Fact]
    public async Task FR88_Vercels_GitHub_App_without_access_is_VERCEL_GIT_NOT_CONNECTED_with_the_install_link_and_nothing_saved()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Post, "/v11/projects", HttpStatusCode.BadRequest,
                """{"error":{"code":"bad_request","message":"To link a GitHub repository, you need to install the GitHub integration first.","action":"Install GitHub App","link":"https://github.com/apps/vercel/installations/select_target"}}""");
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id);

        var response = await ProjectApi.PatchAsync(owner.Client, Settings(project.Id), new { provider = "vercel", connectionId = connection, create = new { name = "todo" } });

        var problem = await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.VercelGitNotConnected);
        Assert.Equal("https://github.com/apps/vercel/installations/select_target", problem.GetProperty("installUrl").GetString());
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.ProjectDeploymentSettings.AnyAsync(s => s.ProjectId == project.Id, Ct)));
    }

    [Fact]
    public async Task FR88_A_Hobby_plan_with_an_organisation_repo_is_VERCEL_PLAN_LIMIT()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Post, "/v11/projects", HttpStatusCode.Forbidden,
                """{"error":{"code":"forbidden","message":"Hobby teams can't connect repositories owned by a GitHub organization. Upgrade to Pro."}}""");
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id);

        await Problems.AssertAsync(
            await ProjectApi.PatchAsync(owner.Client, Settings(project.Id), new { provider = "vercel", connectionId = connection, create = new { name = "todo" } }),
            HttpStatusCode.Conflict,
            ErrorCodes.VercelPlanLimit);
    }

    [Fact]
    public async Task FR88_A_Vercel_project_linked_to_another_repo_is_VALIDATION_and_an_unknown_one_NOT_FOUND()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v9/projects/prj_other", HttpStatusCode.OK, """{"id":"prj_other","name":"other","link":{"type":"github","org":"acme","repo":"other"}}""");
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id);

        var problem = await Problems.AssertAsync(
            await ProjectApi.PatchAsync(owner.Client, Settings(project.Id), new { provider = "vercel", connectionId = connection, vercelProjectId = "prj_other" }),
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation);
        Assert.True(problem.GetProperty("errors").TryGetProperty("vercelProjectId", out _), problem.ToString());
        await Problems.AssertAsync(
            await ProjectApi.PatchAsync(owner.Client, Settings(project.Id), new { provider = "vercel", connectionId = connection, vercelProjectId = "prj_missing" }),
            HttpStatusCode.NotFound,
            ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR88_A_rejected_token_while_linking_marks_the_connection_invalid()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v9/projects/prj_todo", HttpStatusCode.Unauthorized, """{"error":{"code":"forbidden","message":"Not authorized","invalidToken":true}}""");
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id);

        await Problems.AssertAsync(
            await ProjectApi.PatchAsync(owner.Client, Settings(project.Id), new { provider = "vercel", connectionId = connection, vercelProjectId = "prj_todo" }),
            HttpStatusCode.BadRequest,
            ErrorCodes.CredentialInvalid);
        Assert.Equal(VercelConnectionStatus.Invalid, (await ProjectApi.WithDbAsync(app, db => db.VercelConnections.AsNoTracking().SingleAsync(c => c.Id == connection, Ct))).Status);
    }

    [Fact]
    public async Task FR88_S4_22_A_Next_js_Supabase_BaaS_stack_on_Vercel_lists_its_derived_vars_as_required_and_flags_missing_ones()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var created = await owner.Client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                name = ProjectApi.UniqueName("supa"),
                idea = "A recipe app.",
                git = PatGit,
                stack = new
                {
                    frontend = new { pack = "fe-nextjs", path = "." },
                    backend = new { pack = "none" },
                    data = new { pack = "data-supabase" },
                    deploy = new { frontendHost = "deploy-vercel", databaseHost = "supabase", local = "none" },
                },
            },
            Ct);
        var project = await ProjectApi.ReadAsync<ProjectDto>(created, HttpStatusCode.Created);
        await ProjectApi.ReadAsync<DeploymentEnvVarDto>(await owner.Client.PutAsJsonAsync(
            $"{Settings(project.Id)}/env/NEXT_PUBLIC_SUPABASE_URL", new { value = "https://abc.supabase.co", targets = Both }, Ct));

        var settings = await ProjectApi.ReadAsync<DeploymentSettingsDto>(await owner.Client.GetAsync(new Uri(Settings(project.Id), UriKind.Relative), Ct));

        // FR-129, §4.22: the stack's resolved env replaces the v2 preset's requiredEnv; Vercel as the frontend host makes deployment supported.
        Assert.True(settings.Supported);
        Assert.Null(settings.Provider);
        // The order follows the resolved packs, so compare as a set. §4.22: the frontend gets the prefixed URL and publishable key; the server-side SUPABASE_URL arrives with M30's env derivation (PROGRESS.md Known issues).
        Assert.Equal(
            ["NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY", "NEXT_PUBLIC_SUPABASE_URL", "SUPABASE_SECRET_KEY"],
            settings.RequiredEnv.Select(e => e.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["NEXT_PUBLIC_SUPABASE_URL"], settings.RequiredEnv.Where(e => e.Present).Select(e => e.Key).ToArray());
        Assert.True(settings.RequiredEnv.Single(e => e.Key == "SUPABASE_SECRET_KEY").Sensitive);
    }

    [Fact]
    public async Task FR88_A_project_without_a_Vercel_host_is_not_supported_until_an_Owner_opts_in()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);

        var settings = await ProjectApi.ReadAsync<DeploymentSettingsDto>(await owner.Client.GetAsync(new Uri(Settings(project.Id), UriKind.Relative), Ct));

        Assert.Equal((false, (DeploymentProvider?)null, 0, (bool?)null), (settings.Supported, settings.Provider, settings.RequiredEnv.Count, settings.GitConnected));
    }

    [Fact]
    public async Task FR88_S9_The_wizard_sets_up_deployment_with_the_Fake_provider_even_on_Local_Git()
    {
        var app = DeploymentHarness.Fake(factory);
        using var owner = await ApiSession.RegisterAsync(app);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id);

        var response = await owner.Client.PostAsJsonAsync(
            "/api/projects",
            new { name = ProjectApi.UniqueName("wiz"), idea = "A todo app.", git = new { mode = "local" }, deployment = new { provider = "fake", connectionId = connection, create = new { name = "todo-e2e" } } },
            Ct);

        var project = await ProjectApi.ReadAsync<ProjectDto>(response, HttpStatusCode.Created);
        Assert.True(project.DeploymentsEnabled);
        var settings = await ProjectApi.WithDbAsync(app, db => db.ProjectDeploymentSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct));
        Assert.Equal((DeploymentProvider.Fake, (Guid?)connection, (Guid?)owner.User!.Id, "prj_fake_todo-e2e"), (settings.Provider, settings.ConnectionId, settings.BoundByUserId, settings.VercelProjectId));
    }

    [Fact]
    public async Task FR88_Demoting_or_removing_the_binding_Owner_clears_the_binding()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: PatGit);
        var id = fixture.Project.Id;
        await ProjectApi.AddMemberInDbAsync(factory, id, fixture.Outsider.User!.Id, ProjectRole.Owner);
        var connection = await DeploymentHarness.SeedConnectionAsync(factory, fixture.Outsider.User!.Id);
        await DeploymentHarness.BindAsync(factory, id, connection, fixture.Outsider.User!.Id);

        var demoted = await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/members/{fixture.Outsider.User!.Id}", new { role = "contributor" });
        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);

        var settings = await ProjectApi.WithDbAsync(factory, db => db.ProjectDeploymentSettings.AsNoTracking().SingleAsync(s => s.ProjectId == id, Ct));
        Assert.Equal(((Guid?)null, (Guid?)null), (settings.ConnectionId, settings.BoundByUserId));
        Assert.Contains(await LifecycleSeed.EventsAsync(factory, id, EventTypes.SettingsUpdated), e => e.Payload.Contains("owner_role_lost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR88_Turning_deployment_off_removes_the_settings_and_keeps_history()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        await DeploymentHarness.BindAsync(factory, project.Id, await DeploymentHarness.SeedConnectionAsync(factory, owner.User!.Id), owner.User!.Id);

        var off = await ProjectApi.ReadAsync<DeploymentSettingsDto>(await ProjectApi.PatchAsync(owner.Client, Settings(project.Id), new { provider = (string?)null }));

        Assert.Equal(((DeploymentProvider?)null, (DeploymentBindingDto?)null), (off.Provider, off.Binding));
        Assert.False((await ProjectApi.ReadAsync<ProjectDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}", UriKind.Relative), Ct))).DeploymentsEnabled);
    }

    [Fact]
    public async Task FR88_Binding_without_a_connection_is_VALIDATION()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);

        var problem = await Problems.AssertAsync(
            await ProjectApi.PatchAsync(owner.Client, Settings(project.Id), new { provider = "vercel", vercelProjectId = "prj_todo" }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(problem.GetProperty("errors").TryGetProperty("connectionId", out _), problem.ToString());
    }
}
