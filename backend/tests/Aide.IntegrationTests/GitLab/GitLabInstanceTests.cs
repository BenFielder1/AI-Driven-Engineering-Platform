using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.GitLab;
using Aide.Domain.Common;
using Aide.Domain.GitHosts;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.GitLab;

/// <summary>
/// FR-130: Admin → Integrations → GitLab against a stub GitLab API and stub DNS: gitlab.com by default, the SSRF guard
/// (169.254.169.254 refused; 10.0.4.7 only with private networks), the reachability check and version, secrets as flags,
/// configured instances, and removal rules.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitLabInstanceTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeGitHubApi Version(string version) =>
        new FakeGitHubApi().On(HttpMethod.Get, "/api/v4/version", HttpStatusCode.OK, $$"""{"version":"{{version}}","revision":"abc123"}""");

    private static FakeGitHubApi Unauthenticated() =>
        new FakeGitHubApi().On(HttpMethod.Get, "/api/v4/version", HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""");

    [Fact]
    public async Task FR130_GitLab_com_is_listed_by_default_with_the_callback_URL_and_scopes()
    {
        var app = GitLabHarness.App(factory, new FakeGitHubApi());
        using var admin = await ApiSession.AdminAsync(app);

        var overview = await ProjectApi.ReadAsync<AdminGitLabIntegrationDto>(await admin.Client.GetAsync(new Uri("/api/admin/integrations/gitlab", UriKind.Relative), Ct));

        Assert.Equal(GitLabHarness.CallbackUrl, overview.CallbackUrl);
        Assert.Equal(["api", "write_repository", "openid", "profile", "email"], overview.Scopes);
        Assert.Equal("17.11", overview.MinimumVersion);
        var gitlabCom = overview.Instances[0];
        Assert.True(gitlabCom.IsGitLabCom);
        Assert.Equal(("https://gitlab.com", GitHostInstanceSource.Database), (gitlabCom.BaseUrl, gitlabCom.Source));
        Assert.Single(overview.Instances, i => i.IsGitLabCom);
    }

    [Fact]
    public async Task FR130_Adding_169_254_169_254_is_GIT_HOST_NOT_ALLOWED_and_GitLab_is_never_called()
    {
        var gitlab = Version("17.11.0");
        var app = GitLabHarness.App(factory, gitlab);
        using var admin = await ApiSession.AdminAsync(app);

        var response = await admin.Client.PostAsJsonAsync("/api/admin/integrations/gitlab", new { name = "Metadata", baseUrl = "https://169.254.169.254" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.GitHostNotAllowed);
        Assert.Empty(gitlab.Requests);
    }

    [Fact]
    public async Task FR130_A_host_resolving_to_10_0_4_7_is_accepted_with_private_networks_and_refused_without()
    {
        var host = $"gitlab-{Guid.NewGuid():N}.internal.example";
        var dns = new StubGitHostDns((host, "10.0.4.7"));

        var local = GitLabHarness.App(factory, Unauthenticated(), dns, allowPrivateNetworks: true);
        using (var admin = await ApiSession.AdminAsync(local))
        {
            var created = await ProjectApi.ReadAsync<AdminGitLabInstanceDto>(
                await admin.Client.PostAsJsonAsync("/api/admin/integrations/gitlab", new { name = "Internal", baseUrl = $"https://{host}/" }, Ct), HttpStatusCode.Created);
            Assert.Equal(($"https://{host}", (string?)null, GitLabVersionVerdict.Unknown), (created.BaseUrl, created.Version, created.VersionStatus));
        }

        var railway = GitLabHarness.App(factory, Unauthenticated(), new StubGitHostDns(($"other-{host}", "10.0.4.7")), allowPrivateNetworks: false);
        using var railwayAdmin = await ApiSession.AdminAsync(railway);
        var refused = await railwayAdmin.Client.PostAsJsonAsync("/api/admin/integrations/gitlab", new { name = "Internal", baseUrl = $"https://other-{host}" }, Ct);
        await Problems.AssertAsync(refused, HttpStatusCode.BadRequest, ErrorCodes.GitHostNotAllowed);
    }

    [Fact]
    public async Task FR130_The_version_is_recorded_an_old_one_warns_and_the_secret_is_a_flag()
    {
        var app = GitLabHarness.App(factory, Version("17.4.2-ee"));
        using var admin = await ApiSession.AdminAsync(app);
        var baseUrl = GitLabHarness.UniqueBaseUrl();

        var response = await admin.Client.PostAsJsonAsync(
            "/api/admin/integrations/gitlab",
            new { name = "Old GitLab", baseUrl, clientId = "app-id", clientSecret = "super-secret-value" },
            Ct);

        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain("super-secret-value", text, StringComparison.Ordinal);
        var dto = System.Text.Json.JsonSerializer.Deserialize<AdminGitLabInstanceDto>(text, ApiSession.Json)!;
        Assert.Equal(("17.4.2-ee", GitLabVersionVerdict.Old, true, true), (dto.Version, dto.VersionStatus, dto.HasClientSecret, dto.OauthConfigured));
        Assert.NotNull(dto.VersionWarning);
        var row = await ProjectApi.WithDbAsync(app, db => db.GitHostInstances.AsNoTracking().SingleAsync(i => i.Id == dto.Id, Ct));
        Assert.DoesNotContain("super-secret-value", row.OauthClientSecretEncrypted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR130_A_host_that_doesnt_answer_like_GitLab_is_UPSTREAM_ERROR_and_nothing_is_saved()
    {
        var notGitLab = new FakeGitHubApi().On(HttpMethod.Get, "/api/v4/version", HttpStatusCode.OK, """{"hello":"world"}""");
        var app = GitLabHarness.App(factory, notGitLab);
        using var admin = await ApiSession.AdminAsync(app);
        var baseUrl = GitLabHarness.UniqueBaseUrl();

        var response = await admin.Client.PostAsJsonAsync("/api/admin/integrations/gitlab", new { name = "Not GitLab", baseUrl }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadGateway, ErrorCodes.UpstreamError);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.GitHostInstances.AnyAsync(i => i.BaseUrl == baseUrl, Ct)));
    }

    [Fact]
    public async Task FR130_Http_needs_allow_insecure_http()
    {
        var app = GitLabHarness.App(factory, Version("18.0.0"));
        using var admin = await ApiSession.AdminAsync(app);
        var baseUrl = GitLabHarness.UniqueBaseUrl().Replace("https://", "http://", StringComparison.Ordinal);

        await Problems.AssertAsync(
            await admin.Client.PostAsJsonAsync("/api/admin/integrations/gitlab", new { name = "Lab", baseUrl }, Ct), HttpStatusCode.BadRequest, ErrorCodes.GitHostNotAllowed);
        var created = await ProjectApi.ReadAsync<AdminGitLabInstanceDto>(
            await admin.Client.PostAsJsonAsync("/api/admin/integrations/gitlab", new { name = "Lab", baseUrl, allowInsecureHttp = true }, Ct), HttpStatusCode.Created);
        Assert.Equal(GitLabVersionVerdict.Supported, created.VersionStatus);
    }

    [Fact]
    public async Task FR130_Adding_an_existing_base_URL_or_gitlab_com_is_CONFLICT()
    {
        var app = GitLabHarness.App(factory, Version("17.11.0"));
        using var admin = await ApiSession.AdminAsync(app);
        var baseUrl = GitLabHarness.UniqueBaseUrl();
        await ProjectApi.ReadAsync<AdminGitLabInstanceDto>(
            await admin.Client.PostAsJsonAsync("/api/admin/integrations/gitlab", new { name = "First", baseUrl }, Ct), HttpStatusCode.Created);

        await Problems.AssertAsync(
            await admin.Client.PostAsJsonAsync("/api/admin/integrations/gitlab", new { name = "Again", baseUrl = baseUrl + "/" }, Ct), HttpStatusCode.Conflict, ErrorCodes.Conflict);
        await Problems.AssertAsync(
            await admin.Client.PostAsJsonAsync("/api/admin/integrations/gitlab", new { name = "Dot com", baseUrl = "https://gitlab.com" }, Ct), HttpStatusCode.Conflict, ErrorCodes.Conflict);
    }

    [Fact]
    public async Task FR130_Users_get_403_API_tokens_cant_mutate_and_anonymous_gets_401()
    {
        var app = GitLabHarness.App(factory, Version("17.11.0"));
        using var user = await ApiSession.RegisterAsync(app);
        using var anonymous = new ApiSession(app);

        await Problems.AssertAsync(await user.Client.GetAsync(new Uri("/api/admin/integrations/gitlab", UriKind.Relative), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await anonymous.Client.GetAsync(new Uri("/api/admin/integrations/gitlab", UriKind.Relative), Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }

    [Fact]
    public async Task FR130_Removing_an_instance_a_project_uses_is_CONFLICT_and_an_unused_one_goes_with_its_connections()
    {
        var app = GitLabHarness.App(factory, Version("17.11.0"));
        using var admin = await ApiSession.AdminAsync(app);
        using var owner = await ApiSession.RegisterAsync(app);
        var used = await GitLabHarness.SeedInstanceAsync(app);
        var unused = await GitLabHarness.SeedInstanceAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client);
        await GitLabHarness.BindProjectAsync(app, project.Id, used);
        await GitLabHarness.SeedConnectionAsync(app, owner.User!.Id, unused);

        await Problems.AssertAsync(
            await admin.Client.DeleteAsync(new Uri($"/api/admin/integrations/gitlab/{used}", UriKind.Relative), Ct), HttpStatusCode.Conflict, ErrorCodes.Conflict);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteAsync(new Uri($"/api/admin/integrations/gitlab/{unused}", UriKind.Relative), Ct)).StatusCode);

        Assert.False(await ProjectApi.WithDbAsync(app, db => db.GitConnections.AnyAsync(c => c.InstanceId == unused, Ct)));
        Assert.True(await ProjectApi.WithDbAsync(app, db => db.GitHostInstances.AnyAsync(i => i.Id == used, Ct)));
        var gitlabCom = await GitLabHarness.GitLabComIdAsync(app);
        await Problems.AssertAsync(
            await admin.Client.DeleteAsync(new Uri($"/api/admin/integrations/gitlab/{gitlabCom}", UriKind.Relative), Ct), HttpStatusCode.Conflict, ErrorCodes.Conflict);
    }

    [Fact]
    public async Task FR130_Disabling_an_instance_hides_it_from_users_and_stops_its_connections()
    {
        var gitlab = new FakeGitHubApi().On(HttpMethod.Get, "/api/v4/projects", HttpStatusCode.OK, "[]");
        var app = GitLabHarness.App(factory, gitlab);
        using var admin = await ApiSession.AdminAsync(app);
        using var user = await ApiSession.RegisterAsync(app);
        var instance = await GitLabHarness.SeedInstanceAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, user.User!.Id, instance);

        var disabled = await ProjectApi.ReadAsync<AdminGitLabInstanceDto>(
            await ProjectApi.PatchAsync(admin.Client, $"/api/admin/integrations/gitlab/{instance}", new { enabled = false }));
        Assert.False(disabled.Enabled);

        var instances = await ProjectApi.ReadAsync<List<GitLabInstanceSummaryDto>>(await user.Client.GetAsync(new Uri("/api/me/connections/gitlab/instances", UriKind.Relative), Ct));
        Assert.DoesNotContain(instances, i => i.Id == instance);
        await Problems.AssertAsync(
            await user.Client.GetAsync(new Uri($"/api/me/git/gitlab/repos?connectionId={connection}", UriKind.Relative), Ct), HttpStatusCode.Conflict, ErrorCodes.GitNotConnected);
        Assert.Empty(gitlab.Requests);
    }

    [Fact]
    public async Task FR130_S9_A_configured_instance_takes_precedence_and_only_enabled_changes_here()
    {
        var baseUrl = GitLabHarness.UniqueBaseUrl("configured");
        var app = GitLabHarness.App(factory, Version("17.11.0"), settings: new Dictionary<string, string>
        {
            ["GitLab:Instances:0:Name"] = "Configured GitLab",
            ["GitLab:Instances:0:BaseUrl"] = baseUrl,
            ["GitLab:Instances:0:ClientId"] = "configured-id",
            ["GitLab:Instances:0:ClientSecret"] = "configured-secret",
        });
        using var admin = await ApiSession.AdminAsync(app);

        var overview = await ProjectApi.ReadAsync<AdminGitLabIntegrationDto>(await admin.Client.GetAsync(new Uri("/api/admin/integrations/gitlab", UriKind.Relative), Ct));

        var configured = Assert.Single(overview.Instances, i => i.BaseUrl == baseUrl);
        Assert.Equal(("Configured GitLab", GitHostInstanceSource.Configuration, "configured-id", true), (configured.Name, configured.Source, configured.ClientId, configured.OauthConfigured));
        var row = await ProjectApi.WithDbAsync(app, db => db.GitHostInstances.AsNoTracking().SingleAsync(i => i.Id == configured.Id, Ct));
        Assert.Equal(string.Empty, row.OauthClientSecretEncrypted);
        await Problems.AssertAsync(
            await ProjectApi.PatchAsync(admin.Client, $"/api/admin/integrations/gitlab/{configured.Id}", new { name = "Renamed" }), HttpStatusCode.Conflict, ErrorCodes.Conflict);
        var providers = await ProjectApi.ReadAsync<Aide.Api.Features.Auth.AuthProvidersDto>(await admin.Client.GetAsync(new Uri("/api/auth/providers", UriKind.Relative), Ct));
        Assert.Contains(providers.Gitlab, i => i.Id == configured.Id);
    }
}
