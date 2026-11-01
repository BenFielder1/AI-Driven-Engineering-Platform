using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Deployments;
using Aide.Api.Features.Stack;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Deployments;

/// <summary>FR-187, FR-189, FR-190, FR-191, FR-192, §4.28: deployment groups through the API and the job, on the Fake providers.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class DeploymentGroupTests(AideApiFactory factory)
{
    private static readonly object PatGit = new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_groupTestsToken" };

    private static readonly object RailwayX3 = new
    {
        frontend = new { pack = "fe-react-vite", path = "frontend" },
        backend = new { pack = "be-fastapi", path = "backend" },
        data = new { pack = "data-sqlalchemy" },
        deploy = new { frontendHost = "deploy-railway", backendHost = "deploy-railway", databaseHost = "railway-postgres" },
    };

    private static readonly object RailwayDatabaseSeparate = new
    {
        frontend = new { pack = "fe-react-vite", path = "frontend" },
        backend = new { pack = "be-fastapi", path = "backend" },
        data = new { pack = "data-sqlalchemy" },
        deploy = new { frontendHost = "deploy-railway", backendHost = "deploy-railway", databaseHost = "railway-postgres", separateParts = new[] { "database" } },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string GroupPath(Guid projectId, string provider = "railway") => $"{DeploymentHarness.SettingsPath(projectId)}/groups/{provider}";

    private static Uri U(string path) => new(path, UriKind.Relative);

    private async Task<(WebApplicationFactory<Program> App, ProjectFixture Fixture, Guid Connection)> ArrangeAsync(object stack, string token = "railway_seededToken0123")
    {
        var app = DeploymentHarness.Fake(factory);
        var fixture = await ProjectApi.FixtureAsync(app, git: PatGit);
        await DeploymentHarness.SetStackAsync(fixture.Owner.Client, fixture.Project.Id, stack);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id, token: token, provider: PlatformProvider.Railway);
        return (app, fixture, connection);
    }

    /// <summary>Runs the group's job, then the databases' jobs it queued, to the end.</summary>
    private static async Task SettleGroupAsync(WebApplicationFactory<Program> app, Guid projectId)
    {
        var job = await DeploymentHarness.SetupJobAsync(app, projectId);
        await DeploymentHarness.SettleAsync(app, job.Id);
        await DeploymentHarness.SettleDatabasesAsync(app, projectId);
    }

    private static Task<DeploymentGroup> GroupAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.DeploymentGroups.AsNoTracking().SingleAsync(g => g.ProjectId == projectId, Ct));

    [Fact]
    public async Task FR187_Options_and_validate_return_the_groups()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var body = new
        {
            frontend = new { pack = "fe-react-vite" },
            backend = new { pack = "be-fastapi" },
            data = new { pack = "data-sqlalchemy" },
            vcs = new { pack = "vcs-github" },
            deploy = new { frontendHost = "deploy-netlify", backendHost = "deploy-render", databaseHost = "render-postgres" },
        };

        var options = await ProjectApi.ReadAsync<StackOptionsDto>(await owner.Client.PostAsJsonAsync("/api/stack/options", body, Ct));
        var composition = await ProjectApi.ReadAsync<StackCompositionDto>(await owner.Client.PostAsJsonAsync("/api/stack/validate", body, Ct));

        var group = Assert.Single(options.Groups);
        Assert.Equal((DeploymentProvider.Render, SharedProjectCapability.Project), (group.Provider, group.Capability));
        Assert.Equal([DeploymentPart.Backend, DeploymentPart.Database], group.Parts);
        Assert.Equal(DeploymentProvider.Render, Assert.Single(composition.Groups).Provider);
        Assert.Equal(2, options.Windows.Count);
        Assert.Empty(composition.Deploy.SeparateParts);
    }

    [Fact]
    public async Task FR189_Validate_stores_separateParts_and_reports_a_removed_part()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var body = new
        {
            frontend = new { pack = "fe-react-vite" },
            backend = new { pack = "be-fastapi" },
            vcs = new { pack = "vcs-github" },
            deploy = new { frontendHost = "deploy-netlify", backendHost = "deploy-render", separateParts = new[] { "backend" } },
        };

        var composition = await ProjectApi.ReadAsync<StackCompositionDto>(await owner.Client.PostAsJsonAsync("/api/stack/validate", body, Ct));

        Assert.Empty(composition.Deploy.SeparateParts);
        Assert.Contains(composition.Caveats, c => c is { Rule: "R13", Option: "backend" });
    }

    [Fact]
    public async Task FR190_One_job_sets_up_the_project_backend_frontend_and_database_and_the_group_becomes_ready()
    {
        var (app, fixture, connection) = await ArrangeAsync(RailwayX3);
        using var _ = fixture;
        var id = fixture.Project.Id;

        var accepted = await ProjectApi.ReadAsync<DeploymentGroupDto>(
            await fixture.Owner.Client.PutAsJsonAsync(GroupPath(id), new { connectionId = connection, create = new { name = "todo" } }, Ct), HttpStatusCode.Accepted);
        Assert.Equal(("configuring", "todo"), (accepted.Status, accepted.Name));
        var setupJobs = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().CountAsync(j => j.ProjectId == id && j.Type == JobTypes.DeploymentSetup, Ct));
        Assert.Equal(1, setupJobs);

        await SettleGroupAsync(app, id);

        var group = await GroupAsync(app, id);
        Assert.Equal((DeploymentGroupStatus.Ready, "fake_railway_project_todo"), (group.Status, group.SharedResourceId));
        var settings = await DeploymentHarness.SettingsAsync(fixture.Owner.Client, id);
        Assert.Equal("ready", Assert.Single(settings.Groups).Status);
        Assert.All(settings.Targets, t => Assert.Equal((DeploymentTargetStatus.Ready, (Guid?)group.Id), (t.Status, t.GroupId)));
        Assert.Contains(settings.Targets, t => t.Name == "todo-web");
        Assert.Contains(settings.Targets, t => t.Name == "todo-api");
        Assert.All(settings.Databases, d => Assert.Equal(("available", (Guid?)group.Id), (d.Status, d.GroupId)));
        var events = await ProjectApi.WithDbAsync(app, db => db.Events.AsNoTracking().Where(e => e.ProjectId == id).Select(e => e.Type).ToListAsync(Ct));
        Assert.Contains(EventTypes.DeploymentGroupConfigured, events);
    }

    [Fact]
    public async Task FR190_A_failed_frontend_leaves_the_others_ready_and_retry_creates_only_the_frontend()
    {
        var (app, fixture, failing) = await ArrangeAsync(RailwayX3, token: "fake_fail_web_token0123");
        using var _ = fixture;
        var id = fixture.Project.Id;
        var client = fixture.Owner.Client;
        await ProjectApi.ReadAsync<DeploymentGroupDto>(await client.PutAsJsonAsync(GroupPath(id), new { connectionId = failing, create = new { name = "todo" } }, Ct), HttpStatusCode.Accepted);

        await SettleGroupAsync(app, id);

        var settings = await DeploymentHarness.SettingsAsync(client, id);
        var card = Assert.Single(settings.Groups);
        Assert.Equal(("error", true), (card.Status, card.CanRetry));
        var frontend = settings.Targets.Single(t => t.Component == DeploymentComponent.Frontend);
        var backend = settings.Targets.Single(t => t.Component == DeploymentComponent.Backend);
        Assert.Equal((DeploymentTargetStatus.Error, DeploymentTargetStatus.Ready), (frontend.Status, backend.Status));
        Assert.All(settings.Databases, d => Assert.Equal("available", d.Status));
        var events = await ProjectApi.WithDbAsync(app, db => db.Events.AsNoTracking().Where(e => e.ProjectId == id).Select(e => e.Type).ToListAsync(Ct));
        Assert.Contains(EventTypes.DeploymentGroupFailed, events);

        // A working token, then Retry: only the frontend is set up; the rest keep their resources.
        var working = await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id, token: "railway_workingToken0123", provider: PlatformProvider.Railway);
        await ProjectApi.ReadAsync<DeploymentGroupDto>(await client.PutAsJsonAsync(GroupPath(id), new { connectionId = working }, Ct), HttpStatusCode.Accepted);
        await ProjectApi.ReadAsync<DeploymentGroupDto>(await client.PostAsync(U($"{GroupPath(id)}/retry"), null, Ct), HttpStatusCode.Accepted);
        await SettleGroupAsync(app, id);

        var after = await DeploymentHarness.SettingsAsync(client, id);
        Assert.Equal(DeploymentTargetStatus.Ready, after.Targets.Single(t => t.Component == DeploymentComponent.Frontend).Status);
        Assert.Equal(backend.ResourceId, after.Targets.Single(t => t.Component == DeploymentComponent.Backend).ResourceId);
        Assert.Equal("ready", Assert.Single(after.Groups).Status);
    }

    [Fact]
    public async Task FR190_An_existing_project_name_is_PLATFORM_RESOURCE_EXISTS()
    {
        var (app, fixture, connection) = await ArrangeAsync(RailwayX3);
        using var _ = fixture;

        var response = await fixture.Owner.Client.PutAsJsonAsync(GroupPath(fixture.Project.Id), new { connectionId = connection, create = new { name = "fake-exists" } }, Ct);

        var problem = await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.PlatformResourceExists);
        Assert.Equal("railway", problem.GetProperty("platform").GetString());
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.DeploymentGroups.AnyAsync(g => g.ProjectId == fixture.Project.Id, Ct)));
    }

    [Fact]
    public async Task FR190_Validation_authorisation_and_group_errors()
    {
        var (app, fixture, connection) = await ArrangeAsync(RailwayX3);
        using var _ = fixture;
        var id = fixture.Project.Id;
        var body = new { connectionId = connection, create = new { name = "todo" } };

        await Problems.AssertAsync(await fixture.Viewer.Client.PutAsJsonAsync(GroupPath(id), body, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Contributor.Client.PutAsJsonAsync(GroupPath(id), body, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Outsider.Client.PutAsJsonAsync(GroupPath(id), body, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(GroupPath(id), body, Ct)).StatusCode);
        await Problems.AssertAsync(await fixture.Owner.Client.PutAsJsonAsync(GroupPath(id, "render"), body, Ct), HttpStatusCode.BadRequest, ErrorCodes.StackIncompatible);
        await Problems.AssertAsync(await fixture.Owner.Client.PutAsJsonAsync(GroupPath(id), new { connectionId = connection }, Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(U($"{GroupPath(id)}/retry"), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);

        var others = await DeploymentHarness.SeedConnectionAsync(app, fixture.Contributor.User!.Id, token: "railway_othersToken0123", provider: PlatformProvider.Railway);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PutAsJsonAsync(GroupPath(id), new { connectionId = others, create = new { name = "todo" } }, Ct), HttpStatusCode.Forbidden, ErrorCodes.CredentialNotOwner);
    }

    [Fact]
    public async Task FR187_One_part_on_the_platform_is_DEPLOYMENT_GROUP_INVALID()
    {
        var (_, fixture, connection) = await ArrangeAsync(new { frontend = new { pack = "fe-react-vite", path = "frontend" }, deploy = new { frontendHost = "deploy-railway" } });
        using var _f = fixture;

        await Problems.AssertAsync(
            await fixture.Owner.Client.PutAsJsonAsync(GroupPath(fixture.Project.Id), new { connectionId = connection, create = new { name = "todo" } }, Ct),
            HttpStatusCode.BadRequest,
            ErrorCodes.DeploymentGroupInvalid);
    }

    [Fact]
    public async Task FR191_A_rebind_to_a_token_that_cant_see_the_project_is_PLATFORM_ERROR_and_the_old_binding_stays()
    {
        var (app, fixture, connection) = await ArrangeAsync(RailwayX3);
        using var _ = fixture;
        var id = fixture.Project.Id;
        var client = fixture.Owner.Client;
        await ProjectApi.ReadAsync<DeploymentGroupDto>(await client.PutAsJsonAsync(GroupPath(id), new { connectionId = connection, create = new { name = "todo" } }, Ct), HttpStatusCode.Accepted);
        await SettleGroupAsync(app, id);
        var blind = await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id, token: "fake_no_access_token", provider: PlatformProvider.Railway);

        await Problems.AssertAsync(await client.PutAsJsonAsync(GroupPath(id), new { connectionId = blind }, Ct), HttpStatusCode.BadGateway, ErrorCodes.PlatformError);

        Assert.Equal(connection, (await GroupAsync(app, id)).ConnectionId);
        var seeing = await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id, token: "railway_seeingToken0123", provider: PlatformProvider.Railway);
        await ProjectApi.ReadAsync<DeploymentGroupDto>(await client.PutAsJsonAsync(GroupPath(id), new { connectionId = seeing }, Ct), HttpStatusCode.Accepted);
        var settings = await DeploymentHarness.SettingsAsync(client, id);
        Assert.All(settings.Targets, t => Assert.Equal(seeing, t.Binding!.ConnectionId));
        Assert.Equal(DeploymentGroupStatus.Ready, (await GroupAsync(app, id)).Status);
    }

    [Fact]
    public async Task FR191_Delete_clears_the_group_and_its_parts_and_a_repeat_or_a_retry_with_nothing_to_do_is_refused()
    {
        var (app, fixture, connection) = await ArrangeAsync(RailwayX3);
        using var _ = fixture;
        var id = fixture.Project.Id;
        var client = fixture.Owner.Client;
        await ProjectApi.ReadAsync<DeploymentGroupDto>(await client.PutAsJsonAsync(GroupPath(id), new { connectionId = connection, create = new { name = "todo" } }, Ct), HttpStatusCode.Accepted);
        await SettleGroupAsync(app, id);
        await Problems.AssertAsync(await client.PostAsync(U($"{GroupPath(id)}/retry"), null, Ct), HttpStatusCode.Conflict, ErrorCodes.InvalidState);
        await Problems.AssertAsync(await client.PutAsJsonAsync(GroupPath(id), new { connectionId = connection, create = new { name = "other" } }, Ct), HttpStatusCode.Conflict, ErrorCodes.Conflict);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(U(GroupPath(id)), Ct)).StatusCode);

        Assert.False(await ProjectApi.WithDbAsync(app, db => db.DeploymentGroups.AnyAsync(g => g.ProjectId == id, Ct)));
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.DeploymentTargets.AnyAsync(t => t.ProjectId == id, Ct)));
        Assert.Equal("not_configured", Assert.Single((await DeploymentHarness.SettingsAsync(client, id)).Groups).Status);
        await Problems.AssertAsync(await client.DeleteAsync(U(GroupPath(id)), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR191_Parts_leave_and_join_a_group_explicitly()
    {
        var (app, fixture, connection) = await ArrangeAsync(RailwayX3);
        using var _ = fixture;
        var id = fixture.Project.Id;
        var client = fixture.Owner.Client;
        await ProjectApi.ReadAsync<DeploymentGroupDto>(await client.PutAsJsonAsync(GroupPath(id), new { connectionId = connection, create = new { name = "todo" } }, Ct), HttpStatusCode.Accepted);
        await SettleGroupAsync(app, id);
        var group = await GroupAsync(app, id);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(U(DeploymentHarness.TargetPath(id, "frontend")), Ct)).StatusCode);
        var job = await DeploymentHarness.SetUpTargetAsync(app, client, id, "frontend", new { provider = "railway", connectionId = connection, create = new { name = "todo-web" }, groupId = group.Id });

        Assert.Equal(JobStatus.Succeeded, job.Status);
        var target = (await DeploymentHarness.SettingsAsync(client, id)).Targets.Single(t => t.Component == DeploymentComponent.Frontend);
        Assert.Equal((DeploymentTargetStatus.Ready, (Guid?)group.Id), (target.Status, target.GroupId));
        await Problems.AssertAsync(
            await DeploymentHarness.PutTargetAsync(client, id, "backend", new { provider = "railway", connectionId = connection, create = new { name = "x" }, groupId = Guid.CreateVersion7() }),
            HttpStatusCode.BadRequest,
            ErrorCodes.DeploymentGroupInvalid);
    }

    [Fact]
    public async Task FR191_A_v4_target_keeps_no_group_and_a_group_wont_take_it()
    {
        var (app, fixture, connection) = await ArrangeAsync(RailwayX3);
        using var _ = fixture;
        var id = fixture.Project.Id;
        var client = fixture.Owner.Client;

        var job = await DeploymentHarness.SetUpTargetAsync(app, client, id, "backend", new { provider = "railway", connectionId = connection, create = new { name = "todo-api" } });

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Null((await DeploymentHarness.SettingsAsync(client, id)).Targets.Single(t => t.Component == DeploymentComponent.Backend).GroupId);
        await Problems.AssertAsync(
            await client.PutAsJsonAsync(GroupPath(id), new { connectionId = connection, create = new { name = "todo" } }, Ct), HttpStatusCode.BadRequest, ErrorCodes.DeploymentGroupInvalid);
    }

    [Fact]
    public async Task FR189_A_separate_Railway_database_stands_alone_with_a_public_url_and_its_recorded_origin()
    {
        var (app, fixture, connection) = await ArrangeAsync(RailwayDatabaseSeparate);
        using var _ = fixture;
        var id = fixture.Project.Id;
        var client = fixture.Owner.Client;

        Assert.Equal([DeploymentPart.Frontend, DeploymentPart.Backend], Assert.Single((await DeploymentHarness.SettingsAsync(client, id)).Groups).Parts);
        var response = await client.PutAsJsonAsync($"{DeploymentHarness.SettingsPath(id)}/databases/production", new { provider = "railway", connectionId = connection, create = new { name = "todo-db" } }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().Where(j => j.ProjectId == id && j.Type == JobTypes.DatabaseProvisioning).OrderByDescending(j => j.CreatedAt).FirstAsync(Ct));
        await DeploymentHarness.SettleAsync(app, job.Id);

        var settings = await DeploymentHarness.SettingsAsync(client, id);
        var production = settings.Databases.Single(d => d.Environment == DeploymentEnvironment.Production);
        Assert.Equal("available", production.Status);
        Assert.Contains("proxy.rlwy.test", production.ConnectionSummary, StringComparison.Ordinal);
        Assert.Equal(DerivedEnvRules.SeparateRailwayOrigin, Assert.Single(settings.Env.Variables, v => v.Key == DerivedEnvRules.DatabaseUrl).DerivedFrom);
    }

    [Fact]
    public async Task FR191_A_group_card_shows_up_before_setup_for_any_member()
    {
        var (_, fixture, _) = await ArrangeAsync(RailwayX3);
        using var _f = fixture;

        var card = Assert.Single((await DeploymentHarness.SettingsAsync(fixture.Viewer.Client, fixture.Project.Id)).Groups);

        Assert.Equal(("not_configured", false, SharedProjectCapability.Project, DeploymentProvider.Railway), (card.Status, card.CanBind, card.Capability, card.Provider));
        Assert.Equal(4, card.PartStatuses.Count);
        Assert.True(JsonSerializer.Serialize(card).Length > 0);
    }
}
