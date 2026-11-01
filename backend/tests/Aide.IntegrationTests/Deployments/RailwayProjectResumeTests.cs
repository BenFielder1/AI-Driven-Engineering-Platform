using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Deployments;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Platforms;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Deployments;

/// <summary>
/// FR-190, FR-192: a created Railway project's id is stored at once and resumed from; an existing name with no stored id is
/// always refused. FR-190: a group linking a Postgres provisions its preview instance. Real providers on a stubbed Railway API.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RailwayProjectResumeTests(AideApiFactory factory)
{
    private static readonly object PatGit = new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_resumeTestsToken" };

    private static readonly object RailwayX3 = new
    {
        frontend = new { pack = "fe-react-vite", path = "frontend" },
        backend = new { pack = "be-fastapi", path = "backend" },
        data = new { pack = "data-sqlalchemy" },
        deploy = new { frontendHost = "deploy-railway", backendHost = "deploy-railway", databaseHost = "railway-postgres" },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A Railway workspace whose <c>projectCreate</c> makes the project visible afterwards; <c>Special</c> answers first.</summary>
    private sealed class Workspace
    {
        public string? Project { get; set; }

        public string ProjectId { get; set; } = "p1";

        private string Node => "{\"id\":\"" + ProjectId + "\",\"name\":\"" + Project + "\"}";

        public Func<string, (HttpStatusCode, string)?> Special { get; set; } = _ => null;

        public (HttpStatusCode Status, string Body) Answer(string body)
        {
            if (Special(body) is { } special)
            {
                return special;
            }

            if (body.Contains("projects(workspaceId", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, Project is null ? """{"data":{"projects":{"edges":[]}}}""" : "{\"data\":{\"projects\":{\"edges\":[{\"node\":" + Node + "}]}}}");
            }

            if (body.Contains("projectCreate", StringComparison.Ordinal))
            {
                Project = System.Text.RegularExpressions.Regex.Match(body, "\"name\":\"([^\"]+)\"").Groups[1].Value;
                return (HttpStatusCode.OK, "{\"data\":{\"projectCreate\":" + Node + "}}");
            }

            var text =
                body.Contains("project(id: $id) { id name }", StringComparison.Ordinal) ? "{\"data\":{\"project\":" + Node + "}}"
                : body.Contains("services { edges", StringComparison.Ordinal) ? """{"data":{"project":{"id":"p1","services":{"edges":[{"node":{"id":"svc-pg","name":"Postgres"}}]}}}}"""
                : body.Contains("environments { edges", StringComparison.Ordinal) ? """{"data":{"project":{"id":"p1","environments":{"edges":[{"node":{"id":"e-prod","name":"production"}},{"node":{"id":"e-prev","name":"aide-preview"}}]}}}}"""
                : body.Contains("volumes", StringComparison.Ordinal) ? """{"data":{"project":{"volumes":{"edges":[]}}}}"""
                : body.Contains("serviceCreate", StringComparison.Ordinal) ? """{"data":{"serviceCreate":{"id":"s1","name":"todo-api"}}}"""
                : body.Contains("domains(", StringComparison.Ordinal) ? """{"data":{"domains":{"serviceDomains":[{"domain":"todo.up.railway.app"}]}}}"""
                : body.Contains("deployments(", StringComparison.Ordinal) ? """{"data":{"deployments":{"edges":[{"node":{"id":"d1","status":"SUCCESS"}}]}}}"""
                : """{"data":{"serviceInstanceUpdate":true,"variableCollectionUpsert":true,"volumeCreate":{"id":"v1"},"serviceInstanceDeployV2":"dep"}}""";
            return (HttpStatusCode.OK, text);
        }
    }

    private async Task<(WebApplicationFactory<Program> App, ApiSession Owner, Guid Project, Guid Connection, FakeGitHubApi Api)> ArrangeAsync(Workspace workspace, object stack)
    {
        var api = new FakeGitHubApi();
        api.On(HttpMethod.Post, "/graphql/v2", request => workspace.Answer(request.Body));
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddHttpClient(PlatformHosts.RailwayClient).ConfigurePrimaryHttpMessageHandler(() => api)));
        var owner = await ApiSession.RegisterAsync(app);
        var id = (await ProjectApi.CreateAsync(owner.Client, git: PatGit)).Id;
        await DeploymentHarness.SetStackAsync(owner.Client, id, stack);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "railway_seededToken0123", provider: PlatformProvider.Railway);
        return (app, owner, id, connection, api);
    }

    private static Task<DeploymentTarget> BackendAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.DeploymentTargets.AsNoTracking().SingleAsync(t => t.ProjectId == id && t.Component == DeploymentComponent.Backend, Ct));

    private static int Creates(FakeGitHubApi api) => api.Requests.Count(r => r.Body.Contains("projectCreate", StringComparison.Ordinal));

    [Fact]
    public async Task FR192_A_rate_limit_after_the_create_resumes_in_the_project_instead_of_refusing_it()
    {
        var workspace = new Workspace();
        var limited = true;
        workspace.Special = body =>
        {
            if (limited && body.Contains("serviceCreate", StringComparison.Ordinal))
            {
                limited = false;
                return (HttpStatusCode.TooManyRequests, "{}");
            }

            return null;
        };
        var (app, owner, id, connection, api) = await ArrangeAsync(workspace, DeploymentHarness.PagesAndRailwayStack);
        using var _ = owner;

        var job = await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "backend", new { provider = "railway", connectionId = connection, create = new { name = "todo" } });

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(1, Creates(api));
        Assert.StartsWith("p1:", (await BackendAsync(app, id)).ResourceId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR192_A_refused_service_then_the_same_create_again_reuses_the_project()
    {
        var workspace = new Workspace();
        var refuse = true;
        workspace.Special = body => refuse && body.Contains("serviceCreate", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"errors":[{"message":"GitHub repo acme/todo not found: is the Railway app installed with access to it?"}]}""")
            : null;
        var (app, owner, id, connection, api) = await ArrangeAsync(workspace, DeploymentHarness.PagesAndRailwayStack);
        using var _ = owner;
        var body = new { provider = "railway", connectionId = connection, create = new { name = "todo" } };

        await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "backend", body);
        var failed = await BackendAsync(app, id);
        Assert.Equal((DeploymentTargetStatus.Error, ErrorCodes.PlatformGitNotConnected, "p1"), (failed.Status, failed.StatusReason, failed.PlatformProjectId));

        refuse = false;
        var job = await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "backend", body);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(1, Creates(api));
    }

    [Fact]
    public async Task FR192_An_existing_name_with_no_stored_id_is_refused_on_every_attempt()
    {
        // The first attempt's 5xx leaves no id; the name that then exists may be anyone's, so the retry doesn't adopt it.
        var workspace = new Workspace();
        var first = true;
        workspace.Special = body =>
        {
            if (first && body.Contains("projectCreate", StringComparison.Ordinal))
            {
                first = false;
                workspace.Project = "todo";
                workspace.ProjectId = "unrelated";
                return (HttpStatusCode.BadGateway, "{}");
            }

            return null;
        };
        var (app, owner, id, connection, api) = await ArrangeAsync(workspace, DeploymentHarness.PagesAndRailwayStack);
        using var _ = owner;

        await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "backend", new { provider = "railway", connectionId = connection, create = new { name = "todo" } });
        var job = await DeploymentHarness.SetupJobAsync(app, id);
        await ProjectApi.WithDbAsync(app, db => db.DeploymentTargets.Where(t => t.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, DeploymentTargetStatus.Configuring), Ct));
        await AgentJobs.RunAsync(app, job);

        var target = await BackendAsync(app, id);
        Assert.Equal((DeploymentTargetStatus.Error, ErrorCodes.PlatformResourceExists, (string?)null), (target.Status, target.StatusReason, target.PlatformProjectId));
        Assert.Equal(1, Creates(api));
    }

    [Fact]
    public async Task FR190_A_rate_limit_after_a_groups_create_keeps_its_project()
    {
        var workspace = new Workspace();
        var limited = true;
        workspace.Special = body =>
        {
            if (limited && body.Contains("serviceCreate", StringComparison.Ordinal))
            {
                limited = false;
                return (HttpStatusCode.TooManyRequests, "{}");
            }

            return null;
        };
        var (app, owner, id, connection, api) = await ArrangeAsync(workspace, RailwayX3);
        using var _ = owner;

        await ProjectApi.ReadAsync<DeploymentGroupDto>(
            await owner.Client.PutAsJsonAsync($"{DeploymentHarness.SettingsPath(id)}/groups/railway", new { connectionId = connection, create = new { name = "todo" } }, Ct), HttpStatusCode.Accepted);
        await DeploymentHarness.SettleAsync(app, (await DeploymentHarness.SetupJobAsync(app, id)).Id);

        Assert.Equal(1, Creates(api));
        Assert.Equal("p1", await ProjectApi.WithDbAsync(app, db => db.DeploymentGroups.AsNoTracking().Where(g => g.ProjectId == id).Select(g => g.SharedResourceId).SingleAsync(Ct)));
    }

    [Fact]
    public async Task FR190_A_group_linking_a_Postgres_provisions_the_preview_instance_and_becomes_available()
    {
        var workspace = new Workspace { Project = "todo" };
        var (app, owner, id, connection, api) = await ArrangeAsync(workspace, RailwayX3);
        using var _ = owner;

        await ProjectApi.ReadAsync<DeploymentGroupDto>(
            await owner.Client.PutAsJsonAsync(
                $"{DeploymentHarness.SettingsPath(id)}/groups/railway",
                new { connectionId = connection, link = new { resourceId = "p1", parts = new { database = "svc-pg" } } },
                Ct),
            HttpStatusCode.Accepted);
        await DeploymentHarness.SettleAsync(app, (await DeploymentHarness.SetupJobAsync(app, id)).Id);
        await DeploymentHarness.SettleDatabasesAsync(app, id);

        var rows = await ProjectApi.WithDbAsync(app, db => db.ManagedDatabases.AsNoTracking().Where(d => d.ProjectId == id).ToListAsync(Ct));
        Assert.All(rows, r => Assert.Equal(ManagedDatabaseStatus.Available, r.Status));
        var preview = rows.Single(r => r.Environment == DeploymentEnvironment.Preview);
        Assert.Equal("p1:svc-pg:e-prev", preview.ResourceId);
        Assert.NotNull(preview.DbPasswordEncrypted);
        Assert.Single(api.Requests, r => r.Body.Contains("volumeCreate", StringComparison.Ordinal));
    }
}
