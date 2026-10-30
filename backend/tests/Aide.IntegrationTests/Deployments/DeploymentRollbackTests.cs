using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Deployments;
using Aide.Api.Features.Projects;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Platforms;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Deployments;

/// <summary>
/// FR-150, FR-151: rollback per provider (Netlify restore against a stubbed Netlify API, GitHub Pages unsupported, the v2
/// <c>/promote</c> alias), the Deployments list filters and the overview's production URLs.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class DeploymentRollbackTests(AideApiFactory factory)
{
    private static readonly object PatGit = new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_rollbackTestToken" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri U(string path) => new(path, UriKind.Relative);

    [Fact]
    public async Task FR150_Netlify_rollback_restores_the_deploy_and_records_a_rollback_row_shown_as_current()
    {
        var netlify = new FakeGitHubApi().On(HttpMethod.Post, "/api/v1/sites/site_1/deploys/dpl_old/restore", HttpStatusCode.OK, """{"id":"dpl_old","state":"ready"}""");
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddHttpClient(PlatformHosts.NetlifyClient).ConfigurePrimaryHttpMessageHandler(() => netlify)));
        using var fixture = await ProjectApi.FixtureAsync(app, git: PatGit);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id, "nfp_seededToken0123", provider: PlatformProvider.Netlify);
        await DeploymentHarness.BindAsync(app, fixture.Project.Id, connection, fixture.Owner.User!.Id, "site_1", DeploymentProvider.Netlify);
        var old = await SeedAsync(app, fixture.Project, DeploymentProvider.Netlify, DeploymentEnvironment.Production, "dpl_old", -10);
        await SeedAsync(app, fixture.Project, DeploymentProvider.Netlify, DeploymentEnvironment.Production, "dpl_new", -5);

        var response = await fixture.Owner.Client.PostAsync(U($"{fixture.Path}/deployments/{old}/rollback"), null, Ct);
        var rolledBack = await ProjectApi.ReadAsync<DeploymentDto>(response, HttpStatusCode.Accepted);

        Assert.Single(netlify.Requests, r => r.Method == "POST" && r.Path == "/api/v1/sites/site_1/deploys/dpl_old/restore");
        Assert.Equal(DeploymentTrigger.Rollback, rolledBack.Trigger);
        Assert.Equal(DeploymentKind.Rollback, rolledBack.Kind);
        Assert.Equal(old, rolledBack.PromotedFromDeploymentId);
        var list = await fixture.Viewer.Client.GetFromJsonAsync<List<DeploymentDto>>(U($"{fixture.Path}/deployments?environment=production"), ApiSession.Json, Ct);
        var current = Assert.Single(list!, d => d.Current);
        Assert.Equal(rolledBack.Id, current.Id);
        Assert.False(current.CanRollback);
        Assert.Equal(DeploymentRollbackRules.ReasonCurrent, current.RollbackBlockedReason);
        Assert.Equal(DeploymentProvider.Netlify, current.Platform);
        Assert.Contains(list!, d => d.Id != rolledBack.Id && d.CanRollback);
        Assert.All(list!, d => Assert.Null(d.OlderThanLatestMigration));
        Assert.True(rolledBack.Current);
        Assert.False(rolledBack.CanRollback);
    }

    [Fact]
    public async Task FR150_FR151_after_a_rollback_the_original_deployment_is_blocked_as_current()
    {
        var netlify = new FakeGitHubApi().On(HttpMethod.Post, "/api/v1/sites/site_1/deploys/dpl_a/restore", HttpStatusCode.OK, """{"id":"dpl_a","state":"ready"}""");
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddHttpClient(PlatformHosts.NetlifyClient).ConfigurePrimaryHttpMessageHandler(() => netlify)));
        using var fixture = await ProjectApi.FixtureAsync(app, git: PatGit);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id, "nfp_seededToken0123", provider: PlatformProvider.Netlify);
        await DeploymentHarness.BindAsync(app, fixture.Project.Id, connection, fixture.Owner.User!.Id, "site_1", DeploymentProvider.Netlify);
        var a = await SeedAsync(app, fixture.Project, DeploymentProvider.Netlify, DeploymentEnvironment.Production, "dpl_a", -10);
        var b = await SeedAsync(app, fixture.Project, DeploymentProvider.Netlify, DeploymentEnvironment.Production, "dpl_b", -5);
        await ProjectApi.ReadAsync<DeploymentDto>(await fixture.Owner.Client.PostAsync(U($"{fixture.Path}/deployments/{a}/rollback"), null, Ct), HttpStatusCode.Accepted);

        var list = await fixture.Viewer.Client.GetFromJsonAsync<List<DeploymentDto>>(U($"{fixture.Path}/deployments?environment=production"), ApiSession.Json, Ct);
        var original = Assert.Single(list!, d => d.Id == a);
        Assert.False(original.Current);
        Assert.False(original.CanRollback);
        Assert.Equal(DeploymentRollbackRules.ReasonCurrent, original.RollbackBlockedReason);
        Assert.True(Assert.Single(list!, d => d.Id == b).CanRollback);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsync(U($"{fixture.Path}/deployments/{a}/rollback"), null, Ct), HttpStatusCode.Conflict, ErrorCodes.InvalidDeployment);

        var state = await fixture.Viewer.Client.GetFromJsonAsync<ProjectStateDto>(U($"{fixture.Path}/state"), ApiSession.Json, Ct);
        Assert.True(state!.LatestDeployment!.Current);
        Assert.False(state.LatestDeployment.CanRollback);
    }

    [Fact]
    public async Task FR150_GitHub_Pages_rollback_is_DEPLOYMENT_ACTION_UNSUPPORTED_409()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: PatGit);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            db.DeploymentTargets.Add(DeploymentHarness.Target(fixture.Project.Id, null, null, "acme/todo", DeploymentProvider.GithubPages));
            await db.SaveChangesAsync(Ct);
        });
        await SeedAsync(factory, fixture.Project, DeploymentProvider.GithubPages, DeploymentEnvironment.Production, "pages:new", -1);
        var old = await SeedAsync(factory, fixture.Project, DeploymentProvider.GithubPages, DeploymentEnvironment.Production, "pages:old", -10);

        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsync(U($"{fixture.Path}/deployments/{old}/rollback"), null, Ct), HttpStatusCode.Conflict, ErrorCodes.DeploymentActionUnsupported);
        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsync(U($"{fixture.Path}/deployments/{old}/rollback"), null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        var list = await fixture.Viewer.Client.GetFromJsonAsync<List<DeploymentDto>>(U($"{fixture.Path}/deployments"), ApiSession.Json, Ct);
        Assert.All(list!, d => Assert.Equal(DeploymentRollbackRules.ReasonUnsupported, d.RollbackBlockedReason));
    }

    [Fact]
    public async Task FR150_promote_is_kept_as_an_alias_of_rollback_for_Vercel()
    {
        var app = DeploymentHarness.Fake(factory);
        using var fixture = await ProjectApi.FixtureAsync(app, git: PatGit);
        var connection = await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id);
        await DeploymentHarness.BindAsync(app, fixture.Project.Id, connection, fixture.Owner.User!.Id);
        var old = await SeedAsync(app, fixture.Project, DeploymentProvider.Fake, DeploymentEnvironment.Production, "dpl_a", -10);
        await SeedAsync(app, fixture.Project, DeploymentProvider.Fake, DeploymentEnvironment.Production, "dpl_b", -5);

        var promoted = await ProjectApi.ReadAsync<DeploymentDto>(
            await fixture.Owner.Client.PostAsync(U($"{fixture.Path}/deployments/{old}/promote"), null, Ct), HttpStatusCode.Accepted);
        var rolledBack = await ProjectApi.ReadAsync<DeploymentDto>(
            await fixture.Owner.Client.PostAsync(U($"{fixture.Path}/deployments/{old}/rollback"), null, Ct), HttpStatusCode.Accepted);

        Assert.Equal(DeploymentTrigger.Promote, promoted.Trigger);
        Assert.Equal(DeploymentKind.Promote, promoted.Kind);
        Assert.Equal(DeploymentTrigger.Promote, rolledBack.Trigger);
        Assert.Equal(old, rolledBack.PromotedFromDeploymentId);
    }

    [Fact]
    public async Task FR151_Deployments_filter_by_component_and_environment_and_the_overview_shows_production_URLs()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: PatGit);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var frontend = DeploymentHarness.Target(fixture.Project.Id, null, null, "prj_x", DeploymentProvider.Vercel);
            frontend.ProductionUrl = "https://todo.vercel.app";
            db.DeploymentTargets.Add(frontend);
            await db.SaveChangesAsync(Ct);
        });
        await SeedAsync(factory, fixture.Project, DeploymentProvider.Vercel, DeploymentEnvironment.Production, "dpl_f", -3);
        await SeedAsync(factory, fixture.Project, DeploymentProvider.Render, DeploymentEnvironment.Production, "dep_b", -2, DeploymentComponent.Backend);
        await SeedAsync(factory, fixture.Project, DeploymentProvider.Vercel, DeploymentEnvironment.Preview, "dpl_p", -1);
        async Task<List<DeploymentDto>> ListAsync(string query) =>
            (await fixture.Viewer.Client.GetFromJsonAsync<List<DeploymentDto>>(U($"{fixture.Path}/deployments?{query}"), ApiSession.Json, Ct))!;

        Assert.Equal(DeploymentComponent.Backend, Assert.Single(await ListAsync("component=backend")).Component);
        Assert.Equal(DeploymentEnvironment.Preview, Assert.Single(await ListAsync("environment=preview")).Target);
        Assert.Equal(DeploymentComponent.Frontend, Assert.Single(await ListAsync("component=frontend&environment=production")).Component);
        Assert.Equal(3, (await ListAsync(string.Empty)).Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Viewer.Client.GetAsync(U($"{fixture.Path}/deployments?component=database"), Ct)).StatusCode);

        var state = await fixture.Viewer.Client.GetFromJsonAsync<ProjectStateDto>(U($"{fixture.Path}/state"), ApiSession.Json, Ct);
        var url = Assert.Single(state!.ProductionUrls);
        Assert.Equal(DeploymentComponent.Frontend, url.Component);
        Assert.Equal("https://todo.vercel.app", url.Url);
        Assert.NotNull(url.DeploymentId);
    }

    private static async Task<Guid> SeedAsync(
        WebApplicationFactory<Program> app,
        ProjectDto project,
        DeploymentProvider provider,
        DeploymentEnvironment environment,
        string providerId,
        int minutesAgo,
        DeploymentComponent component = DeploymentComponent.Frontend)
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(minutesAgo);
        var deployment = new Deployment
        {
            ProjectId = project.Id,
            ReleaseVersionId = project.ReleaseVersionId!.Value,
            Provider = provider,
            Component = component,
            Environment = environment,
            Status = DeploymentStatus.Ready,
            Trigger = DeploymentTrigger.Auto,
            ProviderDeploymentId = providerId,
            GitRef = environment == DeploymentEnvironment.Production ? "main" : "aide/0.1.0",
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
