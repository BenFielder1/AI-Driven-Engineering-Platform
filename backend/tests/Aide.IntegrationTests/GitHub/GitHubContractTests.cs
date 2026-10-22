using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Tokens;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.GitHub;

/// <summary>
/// FR-80–FR-83, FR-86 contract: the GitHub App, connection and repo picker routes with their auth, request validation and
/// behaviour without an App; project creation and Git settings carry the v2 git mode. The App flows are in
/// <see cref="GitHubAppTests"/>.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitHubContractTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri U(string path) => new(path, UriKind.Relative);

    // ---------------------------------------------------------------- FR-80 Admin → Integrations → GitHub

    [Fact]
    public async Task FR80_GitHub_App_routes_need_sign_in_and_the_Admin_role()
    {
        using var anonymous = new ApiSession(factory);
        using var user = await ApiSession.RegisterAsync(factory);
        var manifest = new { ownerType = "user" };

        await Problems.AssertAsync(await anonymous.Client.GetAsync(U("/api/admin/integrations/github"), Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        await Problems.AssertAsync(await user.Client.GetAsync(U("/api/admin/integrations/github"), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await user.Client.PostAsJsonAsync("/api/admin/integrations/github/manifest", manifest, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await user.Client.GetAsync(U("/api/admin/integrations/github/callback?code=c&state=s"), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task FR80_Without_an_App_an_Admin_sees_not_configured_and_a_callback_without_a_valid_state_fails()
    {
        using var admin = await ApiSession.AdminAsync(factory);

        Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync(U("/api/admin/integrations/github"), Ct)).StatusCode);
        var callback = await admin.Client.GetAsync(U("/api/admin/integrations/github/callback?code=c&state=s"), Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/admin/integrations?error=GITHUB_AUTH_FAILED", callback.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task FR80_The_manifest_flow_needs_a_browser_session_API_tokens_get_FORBIDDEN()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        using var cli = await BearerAsync(admin);

        await Problems.AssertAsync(
            await cli.PostAsJsonAsync("/api/admin/integrations/github/manifest", new { ownerType = "user" }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(
            await cli.GetAsync(U("/api/admin/integrations/github/callback?code=c&state=s"), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        // Reading the App status is allowed with an Admin's token.
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync(U("/api/admin/integrations/github"), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("""{"org":"acme"}""")]
    [InlineData("""{"ownerType":"team"}""")]
    [InlineData("""{"ownerType":"organization","org":"-acme"}""")]
    public async Task FR80_An_invalid_manifest_request_is_400(string body)
    {
        using var admin = await ApiSession.AdminAsync(factory);

        var response = await admin.Client.PostAsync(
            U("/api/admin/integrations/github/manifest"), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------- FR-81 connection and repo picker

    [Theory]
    [InlineData("GET", "/api/me/connections/github", HttpStatusCode.OK, null)]
    [InlineData("DELETE", "/api/me/connections/github", HttpStatusCode.NoContent, null)]
    [InlineData("GET", "/api/me/connections/github/connect?returnUrl=/account", HttpStatusCode.NotFound, ErrorCodes.NotFound)]
    [InlineData("GET", "/api/me/github/repos?search=todo&page=2", HttpStatusCode.Conflict, ErrorCodes.GitHubNotConnected)]
    [InlineData("GET", "/api/me/github/owners", HttpStatusCode.Conflict, ErrorCodes.GitHubNotConnected)]
    [InlineData("GET", "/api/me/github/repos/acme/todo-app/refs", HttpStatusCode.Conflict, ErrorCodes.GitHubNotConnected)]
    public async Task FR81_GitHub_connection_and_picker_routes_need_sign_in_and_without_an_App_nothing_is_connected(
        string method, string path, HttpStatusCode expected, string? errorCode)
    {
        using var anonymous = new ApiSession(factory);
        using var user = await ApiSession.RegisterAsync(factory);

        await Problems.AssertAsync(await anonymous.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        var response = await user.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Ct);
        if (errorCode is null)
        {
            Assert.Equal(expected, response.StatusCode);
        }
        else
        {
            await Problems.AssertAsync(response, expected, errorCode);
        }
    }

    [Fact]
    public async Task FR81_Create_repository_needs_sign_in_and_validates_and_without_an_App_is_GITHUB_NOT_CONNECTED()
    {
        using var anonymous = new ApiSession(factory);
        using var user = await ApiSession.RegisterAsync(factory);
        var body = new { owner = "acme", name = "todo-app", description = "A todo app", @private = true };

        await Problems.AssertAsync(await anonymous.Client.PostAsJsonAsync("/api/me/github/repos", body, Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        await Problems.AssertAsync(await user.Client.PostAsJsonAsync("/api/me/github/repos", body, Ct), HttpStatusCode.Conflict, ErrorCodes.GitHubNotConnected);

        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PostAsJsonAsync("/api/me/github/repos", body with { name = "bad name" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PostAsJsonAsync("/api/me/github/repos", body with { name = ".." }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PostAsJsonAsync("/api/me/github/repos", body with { owner = "ac--me-" }, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("/api/me/github/repos?page=0")]
    [InlineData("/api/me/github/repos/-acme/todo/refs")]
    public async Task FR81_Invalid_picker_queries_are_400(string path)
    {
        using var user = await ApiSession.RegisterAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.GetAsync(U(path), Ct)).StatusCode);
    }

    [Fact]
    public async Task FR81_Changing_the_GitHub_connection_needs_a_browser_session_API_tokens_get_FORBIDDEN()
    {
        using var user = await ApiSession.RegisterAsync(factory);
        using var cli = await BearerAsync(user);

        await Problems.AssertAsync(await cli.DeleteAsync(U("/api/me/connections/github"), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await cli.GetAsync(U("/api/me/connections/github/connect"), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        // Reading the connection and the picker is allowed with a token (e.g. for the CLI).
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync(U("/api/me/connections/github"), Ct)).StatusCode);
        await Problems.AssertAsync(await cli.GetAsync(U("/api/me/github/repos"), Ct), HttpStatusCode.Conflict, ErrorCodes.GitHubNotConnected);
    }

    // ---------------------------------------------------------------- FR-82, FR-86 project creation

    [Fact]
    public async Task FR86_A_project_is_created_with_git_mode_and_the_v1_provider_alias_still_works()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var byMode = await ProjectApi.CreateAsync(owner.Client, git: new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_contractTestToken" });
        var byProvider = await ProjectApi.CreateAsync(owner.Client, git: new { provider = "local" });
        var both = await ProjectApi.CreateAsync(owner.Client, git: new { mode = "local", provider = "local" });

        var settings = await SettingsAsync(owner, byMode.Id);
        Assert.Equal((GitProvider.GithubPat, GitProvider.GithubPat, true), (settings.Mode, settings.Provider, settings.HasToken));
        Assert.Equal(GitProvider.Local, (await SettingsAsync(owner, byProvider.Id)).Mode);
        Assert.Equal(GitProvider.Local, (await SettingsAsync(owner, both.Id)).Mode);
    }

    [Theory]
    [InlineData("""{"mode":"local","provider":"github_pat"}""", "git.mode")]
    [InlineData("""{"defaultBranch":"main"}""", "git.mode")]
    [InlineData("""{"mode":"github_app"}""", "git.installationId")]
    [InlineData("""{"mode":"github_app","installationId":7}""", "git.repoFullName")]
    [InlineData("""{"mode":"github_app","installationId":7,"repoFullName":"acme"}""", "git.repoFullName")]
    [InlineData("""{"mode":"github_app","installationId":7,"repoFullName":"acme/todo","token":"ghp_x"}""", "git.token")]
    public async Task FR86_An_invalid_git_step_is_VALIDATION_400(string git, string field)
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var response = await PostProjectAsync(owner, git);

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), body.ToString());
    }

    [Fact]
    public async Task FR82_Without_an_App_a_github_app_project_is_GITHUB_NOT_CONNECTED_and_saves_nothing()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var name = ProjectApi.UniqueName("app");

        var response = await PostProjectAsync(owner, """{"mode":"github_app","installationId":7,"repoFullName":"acme/todo-app"}""", name);

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.GitHubNotConnected);
        Assert.False(await ProjectApi.WithDbAsync(factory, db => db.Projects.AnyAsync(p => p.Name == name, Ct)));
    }

    // ---------------------------------------------------------------- FR-83 Git settings and switch to App

    [Fact]
    public async Task FR83_A_PAT_project_reports_mode_github_pat_and_hasToken_without_the_token()
    {
        const string token = "ghp_patProjectSecretToken";
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client, git: new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token });

        var text = await owner.Client.GetStringAsync(U($"/api/projects/{project.Id}/settings"), Ct);
        var git = JsonSerializer.Deserialize<ProjectSettingsDto>(text, ApiSession.Json)!.Git;

        Assert.Equal((GitProvider.GithubPat, true, false), (git.Mode, git.HasToken, git.CanSwitchToApp));
        Assert.Equal(((long?)null, (long?)null, (string?)null, (bool?)null), (git.InstallationId, git.RepoId, git.RepoFullName, git.AppInstalledOnRepo));
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR83_Switch_to_GitHub_App_is_Owner_only_and_without_an_App_is_GITHUB_APP_NOT_INSTALLED()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_x1" });
        var path = $"{fixture.Path}/settings/git/switch-to-app";

        await Problems.AssertAsync(await fixture.Outsider.Client.PostAsync(U(path), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(U(path), null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Viewer.Client.PostAsync(U(path), null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(U(path), null, Ct), HttpStatusCode.Conflict, ErrorCodes.GitHubAppNotInstalled);
    }

    [Fact]
    public async Task FR83_PATCH_git_settings_cannot_move_a_project_onto_github_app()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client);

        var response = await ProjectApi.PatchAsync(owner.Client, $"/api/projects/{project.Id}/settings/git", new { provider = "github_app" });

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(body.GetProperty("errors").TryGetProperty("provider", out _), body.ToString());
        Assert.Equal(GitProvider.Local, (await SettingsAsync(owner, project.Id)).Mode);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<GitSettingsDto> SettingsAsync(ApiSession owner, Guid projectId) =>
        (await ProjectApi.ReadAsync<ProjectSettingsDto>(await owner.Client.GetAsync(U($"/api/projects/{projectId}/settings"), Ct))).Git;

    private static Task<HttpResponseMessage> PostProjectAsync(ApiSession owner, string gitJson, string? name = null)
    {
        var body = $$"""{"name":"{{name ?? ProjectApi.UniqueName()}}","idea":"Build a todo API.","git":{{gitJson}}}""";
        return owner.Client.PostAsync(U("/api/projects"), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);
    }

    private async Task<HttpClient> BearerAsync(ApiSession session)
    {
        var created = await session.Client.PostAsJsonAsync("/api/me/tokens", new { name = "cli" }, Ct);
        var token = await ProjectApi.ReadAsync<CreatedApiTokenDto>(created, HttpStatusCode.Created);
        return ApiSession.BearerClient(factory, token.Token);
    }
}
