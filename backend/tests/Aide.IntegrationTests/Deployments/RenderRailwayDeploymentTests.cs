using System.Net;
using Aide.Api.Features.Deployments;
using Aide.Api.Features.Projects;
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
/// FR-144, FR-147, FR-150, §4.20: Render and Railway for real (not Fake mode) against stubbed platform APIs: setup creates
/// the production resource and the preview (Render <c>-preview</c> service, Railway <c>aide-preview</c> environment), and
/// rollback goes through each platform's call and is recorded with trigger <c>rollback</c>.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RenderRailwayDeploymentTests(AideApiFactory factory)
{
    private static readonly object PatGit = new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_renderRailwayToken" };

    /// <summary>A React SPA on GitHub Pages with a FastAPI backend on Render.</summary>
    private static readonly object PagesAndRenderStack = new
    {
        frontend = new { pack = "fe-react-vite", path = "frontend" },
        backend = new { pack = "be-fastapi", path = "backend" },
        deploy = new { frontendHost = "deploy-github-pages", backendHost = "deploy-render" },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri U(string path) => new(path, UriKind.Relative);

    private static WebApplicationFactory<Program> App(AideApiFactory factory, string clientName, FakeGitHubApi api) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => api)));

    [Fact]
    public async Task FR144_FR147_Render_setup_creates_the_production_service_and_its_dash_preview_service()
    {
        var render = new FakeGitHubApi
        {
            // Env sync after setup reads and replaces the services' variables.
            Fallback = _ => (HttpStatusCode.OK, "[]"),
        };
        render.On(HttpMethod.Post, "/v1/services", request => request.Body.Contains("\"todo-api-preview\"", StringComparison.Ordinal)
            ? (HttpStatusCode.Created, """{"service":{"id":"srv-v","name":"todo-api-preview","serviceDetails":{"url":"https://todo-api-preview.onrender.com"}}}""")
            : (HttpStatusCode.Created, """{"service":{"id":"srv-p","name":"todo-api","serviceDetails":{"url":"https://todo-api.onrender.com"}}}"""));
        var app = App(factory, PlatformHosts.RenderClient, render);
        using var owner = await ApiSession.RegisterAsync(app);
        var id = (await ProjectApi.CreateAsync(owner.Client, git: PatGit)).Id;
        await DeploymentHarness.SetStackAsync(owner.Client, id, PagesAndRenderStack);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "rnd_seededKey0123", provider: PlatformProvider.Render);

        var job = await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "backend", new { provider = "render", connectionId = connection, create = new { name = "todo-api", region = "frankfurt", plan = "starter" } });

        Assert.Equal(JobStatus.Succeeded, job.Status);
        var target = await ProjectApi.WithDbAsync(app, db => db.DeploymentTargets.AsNoTracking().SingleAsync(t => t.ProjectId == id && t.Component == DeploymentComponent.Backend, Ct));
        Assert.Equal(
            ("srv-p", "srv-v", "https://todo-api.onrender.com", "https://todo-api-preview.onrender.com"),
            (target.ResourceId, target.PreviewResourceId, target.ProductionUrl, target.PreviewUrl));
        var creates = render.Requests.Where(r => r.Method == "POST" && r.Path == "/v1/services").ToList();
        Assert.Equal(2, creates.Count);
        Assert.Contains("\"type\":\"web_service\"", creates[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"autoDeployTrigger\":\"commit\"", creates[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"ownerId\":\"usr_seed\"", creates[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"rootDir\":\"backend\"", creates[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"healthCheckPath\":", creates[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"todo-api-preview\"", creates[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"autoDeployTrigger\":\"off\"", creates[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR144_FR147_Railway_setup_creates_the_service_and_the_aide_preview_environment()
    {
        var railway = new FakeGitHubApi();
        railway.On(HttpMethod.Post, "/graphql/v2", request => (HttpStatusCode.OK, RailwayAnswer(request.Body)));
        var app = App(factory, PlatformHosts.RailwayClient, railway);
        using var owner = await ApiSession.RegisterAsync(app);
        var id = (await ProjectApi.CreateAsync(owner.Client, git: PatGit)).Id;
        await DeploymentHarness.SetStackAsync(owner.Client, id, DeploymentHarness.PagesAndRailwayStack);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "railway_seededToken0123", provider: PlatformProvider.Railway);

        var job = await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "backend", new { provider = "railway", connectionId = connection, create = new { name = "todo-api" } });

        Assert.Equal(JobStatus.Succeeded, job.Status);
        var target = await ProjectApi.WithDbAsync(app, db => db.DeploymentTargets.AsNoTracking().SingleAsync(t => t.ProjectId == id && t.Component == DeploymentComponent.Backend, Ct));
        Assert.Equal(("p1:s1:e-prod", "e-prev", "https://todo-api.up.railway.app"), (target.ResourceId, target.PreviewResourceId, target.ProductionUrl));
        var bodies = railway.Requests.Select(r => r.Body).ToList();
        var preview = Assert.Single(bodies, b => b.Contains("environmentCreate", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"aide-preview\",\"sourceEnvironmentId\":\"e-prod\",\"skipInitialDeploys\":true", preview, StringComparison.Ordinal);
        Assert.Contains("\"prDeploys\":false", Assert.Single(bodies, b => b.Contains("projectCreate", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("\"workspaceId\":\"usr_seed\"", Assert.Single(bodies, b => b.Contains("projectCreate", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("\"branch\":\"main\"", Assert.Single(bodies, b => b.Contains("serviceCreate", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR150_Render_rollback_posts_the_older_deploy_and_records_a_rollback_row_shown_as_current()
    {
        var render = new FakeGitHubApi().On(HttpMethod.Post, "/v1/services/srv-p/rollback", HttpStatusCode.Created, """{"id":"dep-r","trigger":"rollback","status":"created"}""");
        var app = App(factory, PlatformHosts.RenderClient, render);
        using var fixture = await ProjectApi.FixtureAsync(app, git: PatGit);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id, "rnd_seededKey0123", provider: PlatformProvider.Render);
        await DeploymentHarness.BindAsync(app, fixture.Project.Id, connection, fixture.Owner.User!.Id, "srv-p", DeploymentProvider.Render);
        var old = await SeedAsync(app, fixture.Project, DeploymentProvider.Render, "srv-p|dep-old", -10);
        await SeedAsync(app, fixture.Project, DeploymentProvider.Render, "srv-p|dep-new", -5);

        var rolledBack = await ProjectApi.ReadAsync<DeploymentDto>(
            await fixture.Owner.Client.PostAsync(U($"{fixture.Path}/deployments/{old}/rollback"), null, Ct), HttpStatusCode.Accepted);

        var call = Assert.Single(render.Requests, r => r.Method == "POST" && r.Path == "/v1/services/srv-p/rollback");
        Assert.Equal("""{"deployId":"dep-old"}""", call.Body);
        Assert.Equal((DeploymentTrigger.Rollback, DeploymentKind.Rollback, old, true), (rolledBack.Trigger, rolledBack.Kind, rolledBack.PromotedFromDeploymentId, rolledBack.Current));
    }

    [Fact]
    public async Task FR150_Railway_rollback_checks_canRollback_then_calls_deploymentRollback()
    {
        var railway = new FakeGitHubApi();
        railway.On(HttpMethod.Post, "/graphql/v2", request => (HttpStatusCode.OK, request.Body.Contains("deploymentRollback", StringComparison.Ordinal)
            ? """{"data":{"deploymentRollback":true}}"""
            : """{"data":{"deployment":{"id":"dep-old","canRollback":true}}}"""));
        var app = App(factory, PlatformHosts.RailwayClient, railway);
        using var fixture = await ProjectApi.FixtureAsync(app, git: PatGit);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id, "railway_seededToken0123", provider: PlatformProvider.Railway);
        await DeploymentHarness.BindAsync(app, fixture.Project.Id, connection, fixture.Owner.User!.Id, "p1:s1:e-prod", DeploymentProvider.Railway);
        var old = await SeedAsync(app, fixture.Project, DeploymentProvider.Railway, "dep-old", -10);
        await SeedAsync(app, fixture.Project, DeploymentProvider.Railway, "dep-new", -5);

        var rolledBack = await ProjectApi.ReadAsync<DeploymentDto>(
            await fixture.Owner.Client.PostAsync(U($"{fixture.Path}/deployments/{old}/rollback"), null, Ct), HttpStatusCode.Accepted);

        Assert.Single(railway.Requests, r => r.Body.Contains("deploymentRollback", StringComparison.Ordinal) && r.Body.Contains("dep-old", StringComparison.Ordinal));
        Assert.Equal((DeploymentTrigger.Rollback, true), (rolledBack.Trigger, rolledBack.Current));
    }

    /// <summary>A Railway workspace with no projects yet, answering each GraphQL operation the setup sends.</summary>
    private static string RailwayAnswer(string body) =>
        body.Contains("projects(workspaceId", StringComparison.Ordinal) ? """{"data":{"projects":{"edges":[]}}}"""
        : body.Contains("projectCreate", StringComparison.Ordinal) ? """{"data":{"projectCreate":{"id":"p1","name":"todo"}}}"""
        : body.Contains("services { edges", StringComparison.Ordinal) ? """{"data":{"project":{"id":"p1","services":{"edges":[]}}}}"""
        : body.Contains("environments { edges", StringComparison.Ordinal) ? """{"data":{"project":{"id":"p1","environments":{"edges":[{"node":{"id":"e-prod","name":"production"}}]}}}}"""
        : body.Contains("serviceCreate", StringComparison.Ordinal) ? """{"data":{"serviceCreate":{"id":"s1","name":"todo-api"}}}"""
        : body.Contains("environmentCreate", StringComparison.Ordinal) ? """{"data":{"environmentCreate":{"id":"e-prev","name":"aide-preview"}}}"""
        : body.Contains("domains(", StringComparison.Ordinal) ? """{"data":{"domains":{"serviceDomains":[]}}}"""
        : body.Contains("serviceDomainCreate", StringComparison.Ordinal) ? """{"data":{"serviceDomainCreate":{"id":"d1","domain":"todo-api.up.railway.app"}}}"""
        : """{"data":{"serviceInstanceUpdate":true,"variableCollectionUpsert":true,"variableDelete":true}}""";

    private static async Task<Guid> SeedAsync(WebApplicationFactory<Program> app, ProjectDto project, DeploymentProvider provider, string providerId, int minutesAgo)
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(minutesAgo);
        var deployment = new Deployment
        {
            ProjectId = project.Id,
            ReleaseVersionId = project.ReleaseVersionId!.Value,
            Provider = provider,
            Component = DeploymentComponent.Frontend,
            Environment = DeploymentEnvironment.Production,
            Status = DeploymentStatus.Ready,
            Trigger = DeploymentTrigger.Auto,
            ProviderDeploymentId = providerId,
            GitRef = "main",
            GitSha = new string('b', 40),
            CreatedAt = at,
            ReadyAt = at,
            UpdatedAt = at,
        };
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync(Ct);
        });
        return deployment.Id;
    }
}
