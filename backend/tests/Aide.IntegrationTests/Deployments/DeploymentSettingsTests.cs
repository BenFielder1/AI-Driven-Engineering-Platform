using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Deployments;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Deployments;

/// <summary>
/// FR-144 (replacing FR-88's single Vercel setting): Settings → Deployment's target cards, set up through
/// <c>PUT …/targets/:component</c> and the <c>deployment_setup</c> job against a stub Vercel API (never api.vercel.com),
/// the platform refusals reported on the target, binding rules, and the wizard's Fake deployment step.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class DeploymentSettingsTests(AideApiFactory factory)
{
    private static readonly object PatGit = new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_deploySettingsToken" };

    private static readonly string[] Both = ["preview", "production"];

    private static readonly string[] BackendOnly = ["backend"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Settings(Guid projectId) => DeploymentHarness.SettingsPath(projectId);

    private static string Frontend(DeploymentSettingsDto settings) => settings.Targets.Single(t => t.Component == DeploymentComponent.Frontend).Provider.ToWire();

    [Fact]
    public async Task FR144_Members_see_one_target_card_per_hosted_component_with_the_derived_build_config_and_readiness()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: PatGit);
        await DeploymentHarness.SetStackAsync(fixture.Owner.Client, fixture.Project.Id, DeploymentHarness.VercelStack);

        var asViewer = await DeploymentHarness.SettingsAsync(fixture.Viewer.Client, fixture.Project.Id);

        Assert.Equal((false, false, "vercel"), (asViewer.CanManage, asViewer.FakeMode, Frontend(asViewer)));
        var target = Assert.Single(asViewer.Targets);
        Assert.Equal(("deploy-vercel", true, true, false), (target.HostOption, target.InComposition, target.NeedsConnection, target.CanBind));
        Assert.Equal((DeploymentTargetStatus.NotConfigured, (string?)null, (DeploymentSetupDto?)null, (DeploymentTargetBindingDto?)null), (target.Status, target.ResourceId, target.Setup, target.Binding));
        Assert.Equal((".", "nextjs", "npm run build"), (target.RootDirectory, target.BuildConfig.Framework, target.BuildConfig.BuildCommand));
        Assert.Equal((true, true, true), (target.Capabilities.Previews, target.Capabilities.Rollback, target.ImplementedForReal));
        Assert.Equal(
            [ReadinessRules.Connection, ReadinessRules.GitAccess, ReadinessRules.RequiredEnv, ReadinessRules.BranchControl, ReadinessRules.PreviewEnvironment, ReadinessRules.PlanNotes],
            target.Readiness.Select(i => i.Id).ToArray());
        Assert.Equal((ReadinessState.Error, "/account/connections"), (target.Readiness[0].State, target.Readiness[0].FixUrl));
        Assert.Equal(ReadinessState.Warning, target.Readiness[1].State);
        Assert.Empty(asViewer.Databases);
        Assert.Equal(OriginModels.ToPackVocabulary(asViewer.OriginModel.Model), asViewer.OriginModel.PackModel);

        Assert.True((await DeploymentHarness.SettingsAsync(fixture.Owner.Client, fixture.Project.Id)).Targets.Single().CanBind);
    }

    [Fact]
    public async Task FR144_A_project_without_a_stack_has_no_targets_databases_or_origin_model()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);

        var settings = await DeploymentHarness.SettingsAsync(owner.Client, project.Id);

        Assert.Equal((0, 0, true), (settings.Targets.Count, settings.Databases.Count, settings.CanManage));
        Assert.Equal(((string?)null, (string?)null), (settings.OriginModel.Model, settings.OriginModel.PackModel));
        Assert.Empty(settings.Env.Variables);
    }

    [Fact]
    public async Task FR144_Linking_an_existing_Vercel_project_queues_the_setup_job_and_binds_the_Owners_connection()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v9/projects/prj_todo", HttpStatusCode.OK, """{"id":"prj_todo","name":"todo","link":{"type":"github","org":"acme","repo":"todo","repoId":42}}""");
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var fixture = await ProjectApi.FixtureAsync(app, git: PatGit);
        var id = fixture.Project.Id;
        await DeploymentHarness.SetStackAsync(fixture.Owner.Client, id, DeploymentHarness.VercelStack);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id, teamId: "team_acme");

        var queued = await ProjectApi.ReadAsync<DeploymentTargetDto>(
            await DeploymentHarness.PutTargetAsync(fixture.Owner.Client, id, "frontend", new { provider = "vercel", connectionId = connection, link = new { resourceId = "prj_todo" } }),
            HttpStatusCode.Accepted);

        // 202: the platform work happens in the deployment_setup job.
        Assert.Equal((DeploymentTargetStatus.Configuring, "queued", "queued"), (queued.Status, queued.Setup!.State, queued.Setup.Step));
        Assert.Empty(vercel.Requests);
        var job = await DeploymentHarness.SetupJobAsync(app, id);
        await DeploymentHarness.SettleAsync(app, job.Id);

        var asViewer = await DeploymentHarness.SettingsAsync(fixture.Viewer.Client, id);
        var target = Assert.Single(asViewer.Targets);
        Assert.Equal((DeploymentTargetStatus.Ready, "prj_todo", "todo"), (target.Status, target.ResourceId, target.Name));
        Assert.Equal(("succeeded", (Guid?)job.Id), (target.Setup!.State, target.Setup.JobId));
        Assert.Equal((connection, fixture.Owner.User!.Id, "acme-team"), (target.Binding!.ConnectionId, target.Binding.BoundBy.Id, target.Binding.AccountName));
        Assert.Contains(vercel.Requests, r => r.Path == "/v9/projects/prj_todo" && r.Query.Contains("teamId=team_acme", StringComparison.Ordinal));
        Assert.True((await ProjectApi.ReadAsync<ProjectDto>(await fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{id}", UriKind.Relative), Ct))).DeploymentsEnabled);
        var events = await LifecycleSeed.EventsAsync(app, id, EventTypes.SettingsUpdated);
        Assert.Contains(events, e => e.Payload.Contains("\"link\"", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Payload.Contains("prj_todo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR144_Creating_a_Vercel_project_links_it_to_the_repo_with_the_build_config_derived_from_the_composition()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Post, "/v11/projects", HttpStatusCode.OK, """{"id":"prj_new","name":"todo-web","link":{"type":"github","org":"acme","repo":"todo"}}""");
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        await DeploymentHarness.SetStackAsync(owner.Client, project.Id, DeploymentHarness.VercelStack);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id);

        var job = await DeploymentHarness.SetUpTargetAsync(app, owner.Client, project.Id, "frontend", new { provider = "vercel", connectionId = connection, create = new { name = "todo-web" } });

        Assert.Equal(JobStatus.Succeeded, job.Status);
        var target = Assert.Single((await DeploymentHarness.SettingsAsync(owner.Client, project.Id)).Targets);
        Assert.Equal((DeploymentTargetStatus.Ready, "prj_new", "nextjs"), (target.Status, target.ResourceId, target.BuildConfig.Framework));
        var body = JsonDocument.Parse(Assert.Single(vercel.Requests, r => r.Method == "POST").Body).RootElement;
        Assert.Equal(("todo-web", "nextjs", "github", "acme/todo"), (
            body.GetProperty("name").GetString(),
            body.GetProperty("framework").GetString(),
            body.GetProperty("gitRepository").GetProperty("type").GetString(),
            body.GetProperty("gitRepository").GetProperty("repo").GetString()));
    }

    [Fact]
    public async Task FR144_Vercels_Git_integration_without_access_fails_the_setup_with_PLATFORM_GIT_NOT_CONNECTED_and_the_install_link()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Post, "/v11/projects", HttpStatusCode.BadRequest,
                """{"error":{"code":"bad_request","message":"To link a GitHub repository, you need to install the GitHub integration first.","action":"Install GitHub App","link":"https://github.com/apps/vercel/installations/select_target"}}""");
        var target = await FailedSetupAsync(vercel, new { name = "todo" });

        Assert.Equal((DeploymentTargetStatus.Error, ErrorCodes.PlatformGitNotConnected), (target.Status, target.StatusReason));
        Assert.Equal(("failed", ErrorCodes.PlatformGitNotConnected), (target.Setup!.State, target.Setup.ErrorCode));
        Assert.False(string.IsNullOrEmpty(target.Setup.FixUrl));
        Assert.Null(target.ResourceId);
    }

    [Fact]
    public async Task FR144_A_Hobby_plan_with_an_organisation_repo_fails_the_setup_with_PLATFORM_PLAN_LIMIT()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Post, "/v11/projects", HttpStatusCode.Forbidden,
                """{"error":{"code":"forbidden","message":"Hobby teams can't connect repositories owned by a GitHub organization. Upgrade to Pro."}}""");

        var target = await FailedSetupAsync(vercel, new { name = "todo" });

        Assert.Equal(("failed", ErrorCodes.PlatformPlanLimit), (target.Setup!.State, target.Setup.ErrorCode));
    }

    [Fact]
    public async Task FR144_Linking_a_Vercel_project_of_another_repo_fails_with_VALIDATION_and_an_unknown_one_with_NOT_FOUND()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v9/projects/prj_other", HttpStatusCode.OK, """{"id":"prj_other","name":"other","link":{"type":"github","org":"acme","repo":"other"}}""");

        var other = await FailedSetupAsync(vercel, link: "prj_other");
        Assert.Equal(ErrorCodes.Validation, other.Setup!.ErrorCode);
        Assert.Contains("acme/other", other.Setup.Message, StringComparison.Ordinal);

        var missing = await FailedSetupAsync(new FakeGitHubApi(), link: "prj_missing");
        Assert.Equal(ErrorCodes.NotFound, missing.Setup!.ErrorCode);
    }

    [Fact]
    public async Task FR144_A_rejected_token_during_setup_marks_the_connection_invalid()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v9/projects/prj_todo", HttpStatusCode.Unauthorized, """{"error":{"code":"forbidden","message":"Not authorized","invalidToken":true}}""");
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        await DeploymentHarness.SetStackAsync(owner.Client, project.Id, DeploymentHarness.VercelStack);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id);

        var job = await DeploymentHarness.SetUpTargetAsync(app, owner.Client, project.Id, "frontend", new { provider = "vercel", connectionId = connection, link = new { resourceId = "prj_todo" } });

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.StartsWith(ErrorCodes.CredentialInvalid, job.LastError, StringComparison.Ordinal);
        Assert.Equal(PlatformConnectionStatus.Invalid, (await ProjectApi.WithDbAsync(app, db => db.PlatformConnections.AsNoTracking().SingleAsync(c => c.Id == connection, Ct))).Status);
    }

    [Fact]
    public async Task FR144_A_provider_the_composition_doesnt_host_is_STACK_INCOMPATIBLE_with_violations()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        var connection = await DeploymentHarness.SeedConnectionAsync(factory, owner.User!.Id);
        var body = new { provider = "vercel", connectionId = connection, create = new { name = "todo" } };

        // No stack yet: no frontend host.
        await Problems.AssertAsync(await DeploymentHarness.PutTargetAsync(owner.Client, project.Id, "frontend", body), HttpStatusCode.BadRequest, ErrorCodes.StackIncompatible);

        await DeploymentHarness.SetStackAsync(owner.Client, project.Id, DeploymentHarness.VercelStack);
        var problem = await Problems.AssertAsync(
            await DeploymentHarness.PutTargetAsync(owner.Client, project.Id, "frontend", new { provider = "netlify", connectionId = connection, create = new { name = "todo" } }),
            HttpStatusCode.BadRequest,
            ErrorCodes.StackIncompatible);
        Assert.Equal("frontendHost", problem.GetProperty("violations")[0].GetProperty("layer").GetString());
        await Problems.AssertAsync(await DeploymentHarness.PutTargetAsync(owner.Client, project.Id, "backend", body), HttpStatusCode.BadRequest, ErrorCodes.StackIncompatible);
        Assert.False(await ProjectApi.WithDbAsync(factory, db => db.DeploymentTargets.AnyAsync(t => t.ProjectId == project.Id, Ct)));
    }

    [Fact]
    public async Task FR144_Setting_up_a_target_without_a_connection_or_with_a_bad_component_is_VALIDATION()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        await DeploymentHarness.SetStackAsync(owner.Client, project.Id, DeploymentHarness.VercelStack);

        var problem = await Problems.AssertAsync(
            await DeploymentHarness.PutTargetAsync(owner.Client, project.Id, "frontend", new { provider = "vercel", link = new { resourceId = "prj_todo" } }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(problem.GetProperty("errors").TryGetProperty("connectionId", out _), problem.ToString());

        var component = await Problems.AssertAsync(
            await DeploymentHarness.PutTargetAsync(owner.Client, project.Id, "database", new { provider = "vercel", link = new { resourceId = "prj_todo" } }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(component.GetProperty("errors").TryGetProperty("component", out _), component.ToString());
    }

    [Fact]
    public async Task FR143_Selecting_GitHub_Pages_without_the_Apps_Pages_permission_is_PLATFORM_PERMISSION_MISSING_with_the_App_link()
    {
        const long installation = 9_143_001;
        await using var harness = GitHubAppHarness.Create(factory);
        harness.OnInstallationToken(installation);
        var app = harness.App;
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        await DeploymentHarness.SetStackAsync(owner.Client, project.Id, DeploymentHarness.PagesAndRailwayStack);
        await ProjectApi.WithDbAsync(app, async db =>
        {
            var git = await db.ProjectGitSettings.SingleAsync(s => s.ProjectId == project.Id, Ct);
            git.Provider = GitProvider.GithubApp;
            git.InstallationId = installation;
            git.RepoId = 42;
            git.RepoFullName = "acme/todo";
            git.TokenEncrypted = null;
            await db.SaveChangesAsync(Ct);
        });

        var problem = await Problems.AssertAsync(
            await DeploymentHarness.PutTargetAsync(owner.Client, project.Id, "frontend", new { provider = "github_pages" }), HttpStatusCode.Conflict, ErrorCodes.PlatformPermissionMissing);

        Assert.Equal("github_pages", problem.GetProperty("platform").GetString());
        Assert.Contains(GitHubAppHarness.Slug, problem.GetProperty("installUrl").GetString(), StringComparison.Ordinal);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.DeploymentTargets.AnyAsync(t => t.ProjectId == project.Id, Ct)));
    }

    [Fact]
    public async Task FR144_FR146_Fake_targets_for_GitHub_Pages_and_Railway_set_up_and_derive_the_cross_origin_variables()
    {
        var app = DeploymentHarness.Fake(factory);
        using var owner = await ApiSession.RegisterAsync(app);
        var id = (await ProjectApi.CreateAsync(owner.Client, git: PatGit)).Id;
        await DeploymentHarness.SetStackAsync(owner.Client, id, DeploymentHarness.PagesAndRailwayStack);
        var railway = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "railway_seededToken0123", provider: PlatformProvider.Railway);

        var before = await DeploymentHarness.SettingsAsync(owner.Client, id);
        Assert.Equal(["frontend", "backend"], before.Targets.Select(t => t.Component.ToWire()).ToArray());
        Assert.Equal((OriginModels.CrossOrigin, OriginModels.CrossOrigin, true), (before.OriginModel.Model, before.OriginModel.PackModel, before.FakeMode));
        Assert.Equal("VITE_", before.Env.PublicPrefix);
        Assert.False(before.Env.Variables.Single(v => v.Key == "VITE_API_BASE_URL").HasValue);

        Assert.Equal(JobStatus.Succeeded, (await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "frontend", new { provider = "github_pages" })).Status);
        Assert.Equal(JobStatus.Succeeded, (await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "backend", new { provider = "railway", connectionId = railway, create = new { name = "todo-api" } })).Status);

        var settings = await DeploymentHarness.SettingsAsync(owner.Client, id);
        var frontend = settings.Targets.Single(t => t.Component == DeploymentComponent.Frontend);
        var backend = settings.Targets.Single(t => t.Component == DeploymentComponent.Backend);
        Assert.Equal((DeploymentTargetStatus.Ready, (DeploymentTargetBindingDto?)null, false, false), (frontend.Status, frontend.Binding, frontend.NeedsConnection, frontend.Capabilities.Previews));
        Assert.Equal(("frontend", "dist"), (frontend.RootDirectory, frontend.BuildConfig.OutputDirectory));
        Assert.Equal((DeploymentTargetStatus.Ready, "fake_railway_todo-api", "https://todo-api-backend.railway.test"), (backend.Status, backend.ResourceId, backend.ProductionUrl));
        Assert.Equal(("backend/Dockerfile", "/api/health/ready"), (backend.BuildConfig.DockerfilePath, backend.BuildConfig.HealthCheckPath));
        Assert.Equal(railway, backend.Binding!.ConnectionId);

        // FR-146: the frontend calls the Railway URL; the backend allows the Pages origin.
        var apiBase = settings.Env.Variables.Single(v => v.Key == "VITE_API_BASE_URL");
        Assert.Equal((DeploymentEnvSource.Derived, true, true), (apiBase.Source, apiBase.Public, apiBase.HasValue));
        Assert.Equal([DeploymentComponent.Frontend], apiBase.AppliesTo);
        Assert.Contains("Railway", apiBase.DerivedFrom, StringComparison.Ordinal);
        var cors = settings.Env.Variables.Single(v => v.Key == "CORS_ALLOWED_ORIGINS");
        Assert.Equal((DeploymentEnvSource.Derived, true, false), (cors.Source, cors.HasValue, cors.Public));
        Assert.Equal([DeploymentComponent.Backend], cors.AppliesTo);
        var stored = await ProjectApi.WithDbAsync(app, db => db.DeploymentEnvVars.AsNoTracking().Where(v => v.ProjectId == id && v.Source == DeploymentEnvSource.Derived).ToListAsync(Ct));
        Assert.Equal(["CORS_ALLOWED_ORIGINS", "VITE_API_BASE_URL"], stored.Select(v => v.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.All(stored, v => Assert.NotNull(v.SyncedAt));

        // FR-145: derived keys can't be set by hand, and a public value can't look secret.
        await Problems.AssertAsync(
            await owner.Client.PutAsJsonAsync($"{Settings(id)}/env/VITE_API_BASE_URL", new { value = "https://evil.example", targets = Both }, Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var secret = await Problems.AssertAsync(
            await owner.Client.PutAsJsonAsync($"{Settings(id)}/env/VITE_STRIPE_SECRET", new { value = "sk_live_123", targets = Both }, Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.Contains(EnvInventoryRules.PublicSecretMessage, secret.GetProperty("title").GetString(), StringComparison.Ordinal);
        var backendSecret = await ProjectApi.ReadAsync<DeploymentEnvVarDto>(await owner.Client.PutAsJsonAsync(
            $"{Settings(id)}/env/STRIPE_SECRET", new { value = "sk_live_123", targets = Both, sensitive = true, appliesTo = BackendOnly }, Ct));
        Assert.Equal((DeploymentEnvSource.User, false, true), (backendSecret.Source, backendSecret.Public, backendSecret.HasValue));
        Assert.Equal([DeploymentComponent.Backend], backendSecret.AppliesTo);
        Assert.NotNull(backendSecret.SyncedAt);
    }

    [Fact]
    public async Task FR144_Verify_returns_the_checked_readiness_checklist_and_flags_missing_derived_values()
    {
        var app = DeploymentHarness.Fake(factory);
        using var fixture = await ProjectApi.FixtureAsync(app, git: PatGit);
        var id = fixture.Project.Id;
        await DeploymentHarness.SetStackAsync(fixture.Owner.Client, id, DeploymentHarness.PagesAndRailwayStack);
        await DeploymentHarness.SetUpTargetAsync(app, fixture.Owner.Client, id, "frontend", new { provider = "github_pages" });
        var verify = DeploymentHarness.TargetPath(id, "frontend") + "/verify";

        var result = await ProjectApi.ReadAsync<DeploymentVerifyDto>(await fixture.Owner.Client.PostAsync(new Uri(verify, UriKind.Relative), null, Ct));

        Assert.Equal(result.Target.Readiness.Select(i => i.Id), result.Items.Select(i => i.Id));
        Assert.Equal(ReadinessState.Ok, result.Items.Single(i => i.Id == ReadinessRules.Connection).State);
        Assert.Equal(ReadinessState.Ok, result.Items.Single(i => i.Id == ReadinessRules.GitAccess).State);
        Assert.Equal(ReadinessState.Na, result.Items.Single(i => i.Id == ReadinessRules.BranchControl).State);
        Assert.Equal(ReadinessState.Na, result.Items.Single(i => i.Id == ReadinessRules.PreviewEnvironment).State);

        // The backend has no URL yet, so the derived VITE_API_BASE_URL is missing: the ready target needs attention.
        var env = result.Items.Single(i => i.Id == ReadinessRules.RequiredEnv);
        Assert.Equal(ReadinessState.Error, env.State);
        Assert.Contains("VITE_API_BASE_URL", env.Message, StringComparison.Ordinal);
        Assert.Equal((DeploymentTargetStatus.Attention, ReadinessRules.RequiredEnv), (result.Target.Status, result.Target.StatusReason));

        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(new Uri(verify, UriKind.Relative), null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Outsider.Client.PostAsync(new Uri(verify, UriKind.Relative), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsync(new Uri(DeploymentHarness.TargetPath(id, "backend") + "/verify", UriKind.Relative), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR144_Deleting_a_target_clears_it_and_its_derived_values_and_keeps_history()
    {
        var app = DeploymentHarness.Fake(factory);
        using var owner = await ApiSession.RegisterAsync(app);
        var id = (await ProjectApi.CreateAsync(owner.Client, git: PatGit)).Id;
        await DeploymentHarness.SetStackAsync(owner.Client, id, DeploymentHarness.PagesAndRailwayStack);
        var railway = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "railway_seededToken0123", provider: PlatformProvider.Railway);
        await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "backend", new { provider = "railway", connectionId = railway, create = new { name = "todo-api" } });

        var deleted = await owner.Client.DeleteAsync(new Uri(DeploymentHarness.TargetPath(id, "backend"), UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var settings = await DeploymentHarness.SettingsAsync(owner.Client, id);
        var backend = settings.Targets.Single(t => t.Component == DeploymentComponent.Backend);
        Assert.Equal((DeploymentTargetStatus.NotConfigured, (string?)null, (DeploymentTargetBindingDto?)null), (backend.Status, backend.ResourceId, backend.Binding));
        Assert.False(settings.Env.Variables.Single(v => v.Key == "VITE_API_BASE_URL").HasValue);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.DeploymentEnvVars.AnyAsync(v => v.ProjectId == id && v.Source == DeploymentEnvSource.Derived, Ct)));
        Assert.Contains(await LifecycleSeed.EventsAsync(app, id, EventTypes.SettingsUpdated), e => e.Payload.Contains("\"cleared\":true", StringComparison.Ordinal));
        await Problems.AssertAsync(await owner.Client.DeleteAsync(new Uri(DeploymentHarness.TargetPath(id, "backend"), UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR144_A_PUT_without_create_or_link_rebinds_the_existing_resource_with_the_new_connection()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v9/projects/prj_todo", HttpStatusCode.OK, """{"id":"prj_todo","name":"todo","link":{"type":"github","org":"acme","repo":"todo","repoId":42}}""");
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var owner = await ApiSession.RegisterAsync(app);
        var id = (await ProjectApi.CreateAsync(owner.Client, git: PatGit)).Id;
        await DeploymentHarness.SetStackAsync(owner.Client, id, DeploymentHarness.VercelStack);
        var first = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "vcp_firstToken0123456");
        await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "frontend", new { provider = "vercel", connectionId = first, link = new { resourceId = "prj_todo" } });
        var second = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "vcp_secondToken012345");

        var job = await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "frontend", new { provider = "vercel", connectionId = second });

        Assert.Equal(JobStatus.Succeeded, job.Status);
        var payload = DeploymentTargetService.TryPayload(job.Payload)!;
        Assert.Equal((false, "prj_todo", (string?)null), (payload.Create, payload.ResourceId, payload.Name));
        var target = Assert.Single((await DeploymentHarness.SettingsAsync(owner.Client, id)).Targets);
        Assert.Equal((DeploymentTargetStatus.Ready, "prj_todo", second), (target.Status, target.ResourceId, target.Binding!.ConnectionId));
        Assert.Contains(vercel.Requests, r => r.Path == "/v9/projects/prj_todo" && r.Authorization == "Bearer vcp_secondToken012345");
        Assert.DoesNotContain(vercel.Requests, r => r.Method == "POST");
        Assert.Contains(await LifecycleSeed.EventsAsync(app, id, EventTypes.SettingsUpdated), e => e.Payload.Contains("\"rebind\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR144_A_stale_setup_job_ends_without_changing_the_target_and_the_newest_one_applies()
    {
        var app = DeploymentHarness.Fake(factory);
        using var owner = await ApiSession.RegisterAsync(app);
        var id = (await ProjectApi.CreateAsync(owner.Client, git: PatGit)).Id;
        await DeploymentHarness.SetStackAsync(owner.Client, id, DeploymentHarness.PagesAndRailwayStack);
        var railway = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "railway_seededToken0123", provider: PlatformProvider.Railway);
        await ProjectApi.ReadAsync<DeploymentTargetDto>(
            await DeploymentHarness.PutTargetAsync(owner.Client, id, "backend", new { provider = "railway", connectionId = railway, create = new { name = "one" } }), HttpStatusCode.Accepted);
        var stale = await DeploymentHarness.SetupJobAsync(app, id);
        await ProjectApi.ReadAsync<DeploymentTargetDto>(
            await DeploymentHarness.PutTargetAsync(owner.Client, id, "backend", new { provider = "railway", connectionId = railway, create = new { name = "two" } }), HttpStatusCode.Accepted);
        var newest = await DeploymentHarness.SetupJobAsync(app, id);
        Assert.NotEqual(stale.Id, newest.Id);

        // As if the first job had already been picked up when the second PUT cancelled the queued ones.
        await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Id == stale.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, JobStatus.Queued).SetProperty(j => j.CompletedAt, (DateTimeOffset?)null), Ct));
        await DeploymentHarness.SettleAsync(app, stale.Id);
        var during = await ProjectApi.WithDbAsync(app, db => db.DeploymentTargets.AsNoTracking().SingleAsync(t => t.ProjectId == id && t.Component == DeploymentComponent.Backend, Ct));
        Assert.Equal((DeploymentTargetStatus.Configuring, (string?)null), (during.Status, during.ResourceId));

        await DeploymentHarness.SettleAsync(app, newest.Id);
        var after = await ProjectApi.WithDbAsync(app, db => db.DeploymentTargets.AsNoTracking().SingleAsync(t => t.ProjectId == id && t.Component == DeploymentComponent.Backend, Ct));
        Assert.Equal((DeploymentTargetStatus.Ready, "fake_railway_two"), (after.Status, after.ResourceId));
    }

    [Fact]
    public async Task FR145_S4_22_A_user_value_on_a_key_that_becomes_derived_is_replaced_with_an_event()
    {
        var app = DeploymentHarness.Fake(factory);
        using var owner = await ApiSession.RegisterAsync(app);
        var id = (await ProjectApi.CreateAsync(owner.Client, git: PatGit)).Id;
        await ProjectApi.ReadAsync<DeploymentEnvVarDto>(await owner.Client.PutAsJsonAsync($"{Settings(id)}/env/VITE_API_BASE_URL", new { value = "https://mine.example", targets = Both }, Ct));
        await DeploymentHarness.SetStackAsync(owner.Client, id, DeploymentHarness.PagesAndRailwayStack);
        var railway = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "railway_seededToken0123", provider: PlatformProvider.Railway);

        await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "backend", new { provider = "railway", connectionId = railway, create = new { name = "todo-api" } });

        var row = await ProjectApi.WithDbAsync(app, db => db.DeploymentEnvVars.AsNoTracking().SingleAsync(v => v.ProjectId == id && v.Key == "VITE_API_BASE_URL", Ct));
        Assert.Equal(DeploymentEnvSource.Derived, row.Source);
        Assert.Contains(await LifecycleSeed.EventsAsync(app, id, EventTypes.SettingsUpdated), e => e.Payload.Contains("replacedUserValue", StringComparison.Ordinal));
        Assert.Contains(factory.Logs.Entries, e => e.AllText.Contains("value was replaced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR144_S4_20_A_manual_preview_on_GitHub_Pages_is_DEPLOYMENT_ACTION_UNSUPPORTED()
    {
        var app = DeploymentHarness.Fake(factory);
        using var owner = await ApiSession.RegisterAsync(app);
        var id = (await ProjectApi.CreateAsync(owner.Client, git: PatGit)).Id;
        await DeploymentHarness.SetStackAsync(owner.Client, id, DeploymentHarness.PagesAndRailwayStack);
        await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "frontend", new { provider = "github_pages" });

        var problem = await Problems.AssertAsync(
            await owner.Client.PostAsync(new Uri($"/api/projects/{id}/deployments/preview", UriKind.Relative), null, Ct), HttpStatusCode.Conflict, ErrorCodes.DeploymentActionUnsupported);

        Assert.Equal("github_pages", problem.GetProperty("platform").GetString());
    }

    [Fact]
    public async Task FR145_S4_22_A_Next_js_Supabase_BaaS_stack_lists_its_required_vars_and_database_cards()
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

        var settings = await DeploymentHarness.SettingsAsync(owner.Client, project.Id);

        Assert.Equal("vercel", Frontend(settings));
        Assert.Equal(("NEXT_PUBLIC_", 2), (settings.Env.PublicPrefix, settings.Databases.Count));
        Assert.All(settings.Databases, d => Assert.Equal(("supabase", "not_configured"), (d.Provider, d.Status)));

        // The server-side SUPABASE_URL arrives with M30's env derivation (PROGRESS.md Known issues).
        var vars = settings.Env.Variables.ToDictionary(v => v.Key);
        Assert.Equal(
            ["NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY", "NEXT_PUBLIC_SUPABASE_URL", "SUPABASE_SECRET_KEY"],
            vars.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal((DeploymentEnvSource.User, true, true), (vars["NEXT_PUBLIC_SUPABASE_URL"].Source, vars["NEXT_PUBLIC_SUPABASE_URL"].HasValue, vars["NEXT_PUBLIC_SUPABASE_URL"].Public));
        Assert.Equal((DeploymentEnvSource.Required, false, true), (vars["SUPABASE_SECRET_KEY"].Source, vars["SUPABASE_SECRET_KEY"].HasValue, vars["SUPABASE_SECRET_KEY"].Sensitive));
        Assert.False(vars["SUPABASE_SECRET_KEY"].Public);
        Assert.Equal(DeploymentEnvSource.Required, vars["NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY"].Source);

        // Readiness flags the missing required variables on the frontend card.
        var required = settings.Targets.Single().Readiness.Single(i => i.Id == ReadinessRules.RequiredEnv);
        Assert.Equal(ReadinessState.Error, required.State);
        Assert.Contains("NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY", required.Message, StringComparison.Ordinal);
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
        var settings = await DeploymentHarness.FrontendTargetAsync(app, project.Id);
        Assert.Equal((DeploymentProvider.Fake, (Guid?)connection, (Guid?)owner.User!.Id, "prj_fake_todo-e2e"), (settings.Provider, settings.ConnectionId, settings.BoundByUserId, settings.ResourceId));
        Assert.Equal((DeploymentTargetStatus.Ready, "nextjs"), (settings.Status, settings.BuildConfig.Framework));
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

        var settings = await DeploymentHarness.FrontendTargetAsync(factory, id);
        Assert.Equal(((Guid?)null, (Guid?)null), (settings.ConnectionId, settings.BoundByUserId));
        Assert.Equal((DeploymentTargetStatus.Attention, "connection_missing"), (settings.Status, settings.StatusReason));
        Assert.Contains(await LifecycleSeed.EventsAsync(factory, id, EventTypes.SettingsUpdated), e => e.Payload.Contains("owner_role_lost", StringComparison.Ordinal));
    }

    /// <summary>A Vercel target set up with <paramref name="create"/> or <paramref name="link"/> whose setup job failed; the card after it.</summary>
    private async Task<DeploymentTargetDto> FailedSetupAsync(FakeGitHubApi vercel, object? create = null, string? link = null)
    {
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        await DeploymentHarness.SetStackAsync(owner.Client, project.Id, DeploymentHarness.VercelStack);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id);
        object body = link is null
            ? new { provider = "vercel", connectionId = connection, create }
            : new { provider = "vercel", connectionId = connection, link = new { resourceId = link } };

        var job = await DeploymentHarness.SetUpTargetAsync(app, owner.Client, project.Id, "frontend", body);

        Assert.Equal(JobStatus.Failed, job.Status);
        return Assert.Single((await DeploymentHarness.SettingsAsync(owner.Client, project.Id)).Targets);
    }
}
