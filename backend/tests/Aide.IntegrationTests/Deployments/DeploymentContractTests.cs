using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Tokens;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Deployments;

/// <summary>
/// FR-87, FR-88, FR-90, FR-91: the Vercel connection, deployment settings, env var and deployment routes with their auth,
/// roles, ownership and request validation. Vercel is unreachable here (no stub), so calls that need it answer 502.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class DeploymentContractTests(AideApiFactory factory)
{
    private const string GitHubPat = "github_pat";

    private static readonly object PatGit = new { mode = GitHubPat, repoUrl = "https://github.com/acme/todo", token = "ghp_deployContractToken" };

    private static readonly string[] ProductionOnly = ["production"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri U(string path) => new(path, UriKind.Relative);

    // ---------------------------------------------------------------- FR-87 Account → Connections → Vercel

    [Theory]
    [InlineData("GET", "/api/me/connections/vercel")]
    [InlineData("POST", "/api/me/connections/vercel")]
    [InlineData("PATCH", "/api/me/connections/vercel/0192a6a4-0000-7000-8000-000000000001")]
    [InlineData("DELETE", "/api/me/connections/vercel/0192a6a4-0000-7000-8000-000000000001")]
    [InlineData("POST", "/api/me/connections/vercel/0192a6a4-0000-7000-8000-000000000001/test")]
    public async Task FR87_Vercel_connection_routes_need_sign_in(string method, string path)
    {
        using var anonymous = new ApiSession(factory);

        await Problems.AssertAsync(
            await anonymous.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }

    [Fact]
    public async Task FR87_Changing_Vercel_connections_needs_a_browser_session_API_tokens_get_FORBIDDEN()
    {
        using var user = await ApiSession.RegisterAsync(factory);
        var own = await SeedConnectionAsync(user.User!.Id);
        using var cli = await BearerAsync(user);

        await Problems.AssertAsync(await cli.PostAsJsonAsync("/api/me/connections/vercel", new { label = "Team", token = "vcp_abc" }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(cli, $"/api/me/connections/vercel/{own}", new { label = "x" }), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await cli.DeleteAsync(U($"/api/me/connections/vercel/{own}"), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await cli.PostAsync(U($"/api/me/connections/vercel/{own}/test"), null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        // Listing is allowed with a token (e.g. for the CLI).
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync(U("/api/me/connections/vercel"), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("""{"label":" ","token":"vcp_abc"}""", "label")]
    [InlineData("""{"label":"Team","token":"vcp abc"}""", "token")]
    [InlineData("""{"label":"Team"}""", null)]
    public async Task FR87_An_invalid_connection_is_VALIDATION_400(string body, string? field)
    {
        using var user = await ApiSession.RegisterAsync(factory);

        var response = await user.Client.PostAsync(U("/api/me/connections/vercel"), Json(body), Ct);

        // A missing required field fails JSON binding (400 VALIDATION without per-field errors).
        var problem = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(field is null || problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
    }

    [Fact]
    public async Task FR87_Another_users_or_an_unknown_connection_is_NOT_FOUND_and_an_own_one_passes_the_ownership_check()
    {
        using var ana = await ApiSession.RegisterAsync(factory);
        using var ben = await ApiSession.RegisterAsync(factory);
        var anas = await SeedConnectionAsync(ana.User!.Id);

        foreach (var id in new[] { anas, Guid.CreateVersion7() })
        {
            await Problems.AssertAsync(await ProjectApi.PatchAsync(ben.Client, $"/api/me/connections/vercel/{id}", new { label = "Mine" }), HttpStatusCode.NotFound, ErrorCodes.NotFound);
            await Problems.AssertAsync(await ben.Client.DeleteAsync(U($"/api/me/connections/vercel/{id}"), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
            await Problems.AssertAsync(await ben.Client.PostAsync(U($"/api/me/connections/vercel/{id}/test"), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }

        Assert.Equal(HttpStatusCode.OK, (await ProjectApi.PatchAsync(ana.Client, $"/api/me/connections/vercel/{anas}", new { label = "Renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProjectApi.PatchAsync(ana.Client, $"/api/me/connections/vercel/{anas}", new { label = "" })).StatusCode);
        await Problems.AssertAsync(await ana.Client.PostAsync(U($"/api/me/connections/vercel/{anas}/test"), null, Ct), HttpStatusCode.BadGateway, ErrorCodes.UpstreamError);
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync(U($"/api/me/connections/vercel/{anas}"), Ct)).StatusCode);
    }

    // ---------------------------------------------------------------- FR-88 Settings → Deployment

    [Fact]
    public async Task FR88_Deployment_settings_are_readable_by_members_and_editable_by_Owners()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: PatGit);
        var path = $"{fixture.Path}/settings/deployment";
        var own = await SeedConnectionAsync(fixture.Owner.User!.Id);
        var body = new { provider = "vercel", connectionId = own, vercelProjectId = "prj_abc" };

        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(U(path), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Viewer.Client.GetAsync(U(path), Ct)).StatusCode);

        await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Outsider.Client, path, body), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Viewer.Client, path, body), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Contributor.Client, path, body), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Owner.Client, path, body), HttpStatusCode.BadGateway, ErrorCodes.UpstreamError);
    }

    [Fact]
    public async Task FR88_Binding_another_users_connection_is_CREDENTIAL_NOT_OWNER_and_an_unknown_one_NOT_FOUND()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: PatGit);
        var path = $"{fixture.Path}/settings/deployment";
        var contributors = await SeedConnectionAsync(fixture.Contributor.User!.Id);

        await Problems.AssertAsync(
            await ProjectApi.PatchAsync(fixture.Owner.Client, path, new { provider = "vercel", connectionId = contributors }), HttpStatusCode.Forbidden, ErrorCodes.CredentialNotOwner);
        await Problems.AssertAsync(
            await ProjectApi.PatchAsync(fixture.Owner.Client, path, new { provider = "vercel", connectionId = Guid.CreateVersion7() }), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Theory]
    [InlineData("""{"provider":"vercel","vercelProjectId":"prj_a","create":{"name":"todo"}}""", "create")]
    [InlineData("""{"provider":"vercel","create":{"name":"Todo App"}}""", "create.name")]
    [InlineData("""{"provider":"vercel","create":{"name":"todo","rootDirectory":"../x"}}""", "create.rootDirectory")]
    [InlineData("""{"provider":"fake"}""", "provider")]
    [InlineData("""{"provider":null,"vercelProjectId":"prj_a"}""", "provider")]
    public async Task FR88_Invalid_deployment_settings_are_VALIDATION_400(string body, string field)
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);

        var response = await owner.Client.PatchAsync(U($"/api/projects/{project.Id}/settings/deployment"), Json(body), Ct);

        var problem = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
    }

    [Fact]
    public async Task FR88_A_Local_Git_project_cannot_deploy_VALIDATION()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client);

        var response = await ProjectApi.PatchAsync(owner.Client, $"/api/projects/{project.Id}/settings/deployment", new { provider = "vercel", vercelProjectId = "prj_a" });

        var problem = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(problem.GetProperty("errors").TryGetProperty("provider", out _), problem.ToString());
    }

    [Fact]
    public async Task FR88_Env_vars_are_Owner_only()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: PatGit);
        var env = $"{fixture.Path}/settings/deployment/env";
        var put = new { value = "https://x.supabase.co", targets = new[] { "preview", "production" }, sensitive = false };

        foreach (var member in new[] { fixture.Contributor, fixture.Viewer })
        {
            await Problems.AssertAsync(await member.Client.GetAsync(U(env), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
            await Problems.AssertAsync(await member.Client.PutAsJsonAsync($"{env}/NEXT_PUBLIC_SUPABASE_URL", put, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
            await Problems.AssertAsync(await member.Client.DeleteAsync(U($"{env}/NEXT_PUBLIC_SUPABASE_URL"), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        }

        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(U(env), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Owner.Client.GetAsync(U(env), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Owner.Client.PutAsJsonAsync($"{env}/NEXT_PUBLIC_SUPABASE_URL", put, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Owner.Client.DeleteAsync(U($"{env}/NEXT_PUBLIC_SUPABASE_URL"), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("1BAD", """{"value":"v","targets":["preview"]}""", "key")]
    [InlineData("MY-KEY", """{"value":"v","targets":["preview"]}""", "key")]
    [InlineData("GOOD", """{"value":"v","targets":[]}""", "targets")]
    [InlineData("GOOD", """{"value":"v","targets":["preview","preview"]}""", "targets")]
    [InlineData("GOOD", """{"targets":["preview"]}""", null)]
    public async Task FR88_An_invalid_env_var_is_VALIDATION_400(string key, string body, string? field)
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);

        var response = await owner.Client.PutAsync(U($"/api/projects/{project.Id}/settings/deployment/env/{key}"), Json(body), Ct);

        // A missing required field fails JSON binding (400 VALIDATION without per-field errors).
        var problem = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(field is null || problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
    }

    [Fact]
    public async Task FR88_An_env_value_over_64_KB_and_deleting_an_invalid_key_are_VALIDATION_400()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        var env = $"/api/projects/{project.Id}/settings/deployment/env";

        var big = await owner.Client.PutAsJsonAsync($"{env}/BIG", new { value = new string('v', (64 * 1024) + 1), targets = ProductionOnly }, Ct);
        await Problems.AssertAsync(big, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await owner.Client.DeleteAsync(U($"{env}/1BAD"), Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR88_The_wizard_Deployment_step_is_validated_ownership_checked_and_asks_Vercel_before_saving_anything()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        using var other = await ApiSession.RegisterAsync(factory);
        var own = await SeedConnectionAsync(owner.User!.Id);
        var others = await SeedConnectionAsync(other.User!.Id);
        var name = ProjectApi.UniqueName("deploy");

        var local = await owner.Client.PostAsJsonAsync("/api/projects", NewProject(name, new { mode = "local" }, new { provider = "vercel", connectionId = own, vercelProjectId = "prj_a" }), Ct);
        var problem = await Problems.AssertAsync(local, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(problem.GetProperty("errors").TryGetProperty("deployment.provider", out _), problem.ToString());

        await Problems.AssertAsync(
            await owner.Client.PostAsJsonAsync("/api/projects", NewProject(name, PatGit, new { provider = "vercel", connectionId = others, create = new { name = "todo" } }), Ct),
            HttpStatusCode.Forbidden,
            ErrorCodes.CredentialNotOwner);
        await Problems.AssertAsync(
            await owner.Client.PostAsJsonAsync("/api/projects", NewProject(name, PatGit, new { provider = "vercel", connectionId = own, create = new { name = "todo", rootDirectory = "apps/web" } }), Ct),
            HttpStatusCode.BadGateway,
            ErrorCodes.UpstreamError);
        Assert.False(await ProjectApi.WithDbAsync(factory, db => db.Projects.AnyAsync(p => p.Name == name, Ct)));

        // Without a deployment the wizard works as before.
        var created = await owner.Client.PostAsJsonAsync("/api/projects", NewProject(name, PatGit, null), Ct);
        Assert.False((await ProjectApi.ReadAsync<ProjectDto>(created, HttpStatusCode.Created)).DeploymentsEnabled);
    }

    [Fact]
    public async Task FR88_deploymentsEnabled_follows_the_project_deployment_settings()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        Assert.False(project.DeploymentsEnabled);

        await SeedSettingsAsync(project.Id, await SeedConnectionAsync(owner.User!.Id), owner.User!.Id);

        Assert.True((await ProjectApi.ReadAsync<ProjectDto>(await owner.Client.GetAsync(U($"/api/projects/{project.Id}"), Ct))).DeploymentsEnabled);
    }

    // ---------------------------------------------------------------- FR-90 previews

    [Fact]
    public async Task FR90_A_preview_needs_a_writer_and_deployment_settings()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: PatGit);
        var path = $"{fixture.Path}/deployments/preview";

        await Problems.AssertAsync(await fixture.Outsider.Client.PostAsync(U(path), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Viewer.Client.PostAsync(U(path), null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(U(path), null, Ct), HttpStatusCode.Conflict, ErrorCodes.DeploymentNotConfigured);

        await SeedSettingsAsync(fixture.Project.Id, await SeedConnectionAsync(fixture.Owner.User!.Id), fixture.Owner.User!.Id);

        // Configured, but execution hasn't created the version branch yet: nothing to deploy.
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(U(path), null, Ct), HttpStatusCode.Conflict, ErrorCodes.Conflict);
    }

    [Fact]
    public async Task FR90_FR91_A_writer_redeploys_a_preview_only_an_Owner_production()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: PatGit);
        var preview = await SeedDeploymentAsync(fixture.Project, DeploymentTarget.Preview, DeploymentStatus.Error);
        var production = await SeedDeploymentAsync(fixture.Project, DeploymentTarget.Production, DeploymentStatus.Ready);
        string Redeploy(Guid id) => $"{fixture.Path}/deployments/{id}/redeploy";

        await Problems.AssertAsync(await fixture.Outsider.Client.PostAsync(U(Redeploy(preview)), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Viewer.Client.PostAsync(U(Redeploy(preview)), null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(U(Redeploy(Guid.CreateVersion7())), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(U(Redeploy(preview)), null, Ct), HttpStatusCode.Conflict, ErrorCodes.DeploymentNotConfigured);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(U(Redeploy(production)), null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(U(Redeploy(production)), null, Ct), HttpStatusCode.Conflict, ErrorCodes.DeploymentNotConfigured);
    }

    [Fact]
    public async Task FR90_The_project_state_carries_latestDeployment()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client);

        var text = await owner.Client.GetStringAsync(U($"/api/projects/{project.Id}/state"), Ct);

        Assert.Contains("\"latestDeployment\":null", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- FR-91 deployments page, promote

    [Fact]
    public async Task FR91_Deployments_are_listed_for_members()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var path = $"{fixture.Path}/deployments";

        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(U(path), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Viewer.Client.GetAsync(U($"{path}?target=production&releaseVersionId={fixture.Project.ReleaseVersionId}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Viewer.Client.GetAsync(U($"{path}?target=staging"), Ct)).StatusCode);
    }

    [Fact]
    public async Task FR91_Only_an_Owner_promotes_and_only_a_ready_production_deployment_INVALID_DEPLOYMENT()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: PatGit);
        var ready = await SeedDeploymentAsync(fixture.Project, DeploymentTarget.Production, DeploymentStatus.Ready);
        var building = await SeedDeploymentAsync(fixture.Project, DeploymentTarget.Production, DeploymentStatus.Building);
        var preview = await SeedDeploymentAsync(fixture.Project, DeploymentTarget.Preview, DeploymentStatus.Ready);
        string Promote(Guid id) => $"{fixture.Path}/deployments/{id}/promote";

        await Problems.AssertAsync(await fixture.Outsider.Client.PostAsync(U(Promote(ready)), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Viewer.Client.PostAsync(U(Promote(ready)), null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(U(Promote(ready)), null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(U(Promote(Guid.CreateVersion7())), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(U(Promote(building)), null, Ct), HttpStatusCode.Conflict, ErrorCodes.InvalidDeployment);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(U(Promote(preview)), null, Ct), HttpStatusCode.Conflict, ErrorCodes.InvalidDeployment);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(U(Promote(ready)), null, Ct), HttpStatusCode.Conflict, ErrorCodes.DeploymentNotConfigured);
    }

    [Fact]
    public async Task FR91_A_deployment_of_another_project_is_NOT_FOUND()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: PatGit);
        using var stranger = await ApiSession.RegisterAsync(factory);
        var elsewhere = await ProjectApi.CreateAsync(stranger.Client, git: PatGit);
        var theirs = await SeedDeploymentAsync(elsewhere, DeploymentTarget.Production, DeploymentStatus.Ready);

        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(U($"{fixture.Path}/deployments/{theirs}/promote"), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(U($"{fixture.Path}/deployments/{theirs}/redeploy"), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    // ---------------------------------------------------------------- helpers

    private static StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");

    private static object NewProject(string name, object git, object? deployment) =>
        new { name, idea = "Build a todo app.", git, deployment };

    private Task<Guid> SeedConnectionAsync(Guid userId) => DeploymentHarness.SeedConnectionAsync(factory, userId);

    private Task SeedSettingsAsync(Guid projectId, Guid connectionId, Guid boundBy) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            db.ProjectDeploymentSettings.Add(new ProjectDeploymentSettings
            {
                ProjectId = projectId,
                Provider = DeploymentProvider.Fake,
                ConnectionId = connectionId,
                BoundByUserId = boundBy,
                VercelProjectId = "prj_contract",
                VercelProjectName = "todo",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        });

    private async Task<Guid> SeedDeploymentAsync(ProjectDto project, DeploymentTarget target, DeploymentStatus status)
    {
        var deployment = new Deployment
        {
            ProjectId = project.Id,
            ReleaseVersionId = project.ReleaseVersionId!.Value,
            Provider = DeploymentProvider.Fake,
            Target = target,
            Status = status,
            Trigger = DeploymentTrigger.Auto,
            ProviderDeploymentId = $"dpl_{Guid.NewGuid():N}",
            GitRef = target == DeploymentTarget.Production ? "main" : "aide/0.1.0",
            GitSha = new string('a', 40),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync(Ct);
        });
        return deployment.Id;
    }

    private async Task<HttpClient> BearerAsync(ApiSession session)
    {
        var created = await session.Client.PostAsJsonAsync("/api/me/tokens", new { name = "cli" }, Ct);
        var token = await ProjectApi.ReadAsync<CreatedApiTokenDto>(created, HttpStatusCode.Created);
        return ApiSession.BearerClient(factory, token.Token);
    }
}
