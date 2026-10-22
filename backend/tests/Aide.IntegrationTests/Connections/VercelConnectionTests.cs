using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Connections;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Connections;

/// <summary>
/// FR-87: Vercel connections against a stub Vercel API (never api.vercel.com): validation with <c>GET /v2/user</c>, the
/// token's scope and team, encryption, rejection, re-testing, renaming and deletion (FR-88 bindings cleared).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class VercelConnectionTests(AideApiFactory factory)
{
    private const string TeamToken = "vcp_teamScopedSecretToken0123456789";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeGitHubApi TeamVercel() => new FakeGitHubApi()
        .On(HttpMethod.Get, "/v2/user", HttpStatusCode.OK, """{"user":{"id":"usr_ana","username":"ana-dev","email":"ana@example.com"}}""")
        .On(HttpMethod.Get, "/v5/user/tokens/current", HttpStatusCode.OK, """{"token":{"id":"tok_1","name":"aide","expiresAt":1893456000000,"scopes":[{"type":"team","teamId":"team_acme","origin":"manual"}]}}""")
        .On(HttpMethod.Get, "/v2/teams/team_acme", HttpStatusCode.OK, """{"id":"team_acme","slug":"acme","name":"Acme"}""");

    [Fact]
    public async Task FR87_A_valid_team_token_is_saved_encrypted_and_shows_the_team_slug_and_valid()
    {
        var vercel = TeamVercel();
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var user = await ApiSession.RegisterAsync(app);

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/vercel", new { label = " Acme team ", token = TeamToken }, Ct);

        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain(TeamToken, text, StringComparison.Ordinal);
        var dto = System.Text.Json.JsonSerializer.Deserialize<VercelConnectionDto>(text, ApiSession.Json)!;
        Assert.Equal(("Acme team", "6789", "ana-dev", "team_acme", "acme"), (dto.Label, dto.Last4, dto.AccountName, dto.TeamId, dto.TeamSlug));
        Assert.Equal((VercelTokenScope.Team, VercelConnectionStatus.Valid), (dto.Scope, dto.Status));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1893456000000), dto.ExpiresAt);
        Assert.NotNull(dto.LastValidatedAt);
        Assert.All(vercel.Requests, r => Assert.Equal($"Bearer {TeamToken}", r.Authorization));

        var row = await ProjectApi.WithDbAsync(app, db => db.VercelConnections.AsNoTracking().SingleAsync(c => c.Id == dto.Id, Ct));
        Assert.DoesNotContain(TeamToken, row.TokenEncrypted, StringComparison.Ordinal);
        Assert.Equal("usr_ana", row.VercelUserId);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(TeamToken, StringComparison.Ordinal));

        var listed = await ProjectApi.ReadAsync<List<VercelConnectionDto>>(await user.Client.GetAsync(new Uri("/api/me/connections/vercel", UriKind.Relative), Ct));
        Assert.Equal(dto.Id, Assert.Single(listed).Id);
    }

    [Fact]
    public async Task FR87_An_account_token_without_introspection_is_account_scoped()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v2/user", HttpStatusCode.OK, """{"user":{"id":"usr_ben","username":"ben"}}""");
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var user = await ApiSession.RegisterAsync(app);

        var dto = await ProjectApi.ReadAsync<VercelConnectionDto>(
            await user.Client.PostAsJsonAsync("/api/me/connections/vercel", new { label = "Me", token = "vcp_accountToken" }, Ct), HttpStatusCode.Created);

        Assert.Equal((VercelTokenScope.Account, (string?)null, (DateTimeOffset?)null, "ben"), (dto.Scope, dto.TeamSlug, dto.ExpiresAt, dto.AccountName));
    }

    [Fact]
    public async Task FR87_A_token_Vercel_rejects_is_CREDENTIAL_INVALID_and_nothing_is_saved()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v2/user", HttpStatusCode.Forbidden, """{"error":{"code":"forbidden","message":"Not authorized","invalidToken":true}}""");
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var user = await ApiSession.RegisterAsync(app);

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/vercel", new { label = "Bad", token = "vcp_rejected" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.CredentialInvalid);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.VercelConnections.AnyAsync(c => c.UserId == user.User!.Id, Ct)));
    }

    [Fact]
    public async Task FR87_Vercel_unreachable_is_UPSTREAM_ERROR_and_nothing_is_saved()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/vercel", new { label = "Offline", token = "vcp_unreachable" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadGateway, ErrorCodes.UpstreamError);
        Assert.False(await ProjectApi.WithDbAsync(factory, db => db.VercelConnections.AnyAsync(c => c.UserId == user.User!.Id, Ct)));
    }

    [Fact]
    public async Task FR87_Testing_again_marks_a_rejected_token_invalid_and_renaming_keeps_it_secret()
    {
        var rejected = false;
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v2/user", _ => rejected
                ? (HttpStatusCode.Unauthorized, """{"error":{"code":"forbidden","message":"Not authorized"}}""")
                : (HttpStatusCode.OK, """{"user":{"id":"usr_c","username":"cleo"}}"""));
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var user = await ApiSession.RegisterAsync(app);
        var created = await ProjectApi.ReadAsync<VercelConnectionDto>(
            await user.Client.PostAsJsonAsync("/api/me/connections/vercel", new { label = "Mine", token = "vcp_willBeRevoked" }, Ct), HttpStatusCode.Created);

        rejected = true;
        var tested = await ProjectApi.ReadAsync<VercelConnectionDto>(await user.Client.PostAsync(new Uri($"/api/me/connections/vercel/{created.Id}/test", UriKind.Relative), null, Ct));
        Assert.Equal(VercelConnectionStatus.Invalid, tested.Status);

        var renamed = await ProjectApi.ReadAsync<VercelConnectionDto>(await ProjectApi.PatchAsync(user.Client, $"/api/me/connections/vercel/{created.Id}", new { label = " Renamed " }));
        Assert.Equal(("Renamed", VercelConnectionStatus.Invalid), (renamed.Label, renamed.Status));
    }

    [Fact]
    public async Task FR87_FR88_Deleting_a_connection_clears_the_bindings_of_its_projects_and_keeps_their_settings()
    {
        var app = DeploymentHarness.Vercel(factory, TeamVercel());
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_bindingClearToken" });
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id);
        await DeploymentHarness.BindAsync(app, project.Id, connection, owner.User!.Id);
        var bound = await ProjectApi.ReadAsync<List<VercelConnectionDto>>(await owner.Client.GetAsync(new Uri("/api/me/connections/vercel", UriKind.Relative), Ct));
        Assert.Equal(project.Id, Assert.Single(Assert.Single(bound).BoundProjects).Id);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync(new Uri($"/api/me/connections/vercel/{connection}", UriKind.Relative), Ct)).StatusCode);

        var settings = await ProjectApi.WithDbAsync(app, db => db.ProjectDeploymentSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct));
        Assert.Equal(((Guid?)null, (Guid?)null, DeploymentHarness.VercelProjectId), (settings.ConnectionId, settings.BoundByUserId, settings.VercelProjectId));
        var cleared = await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.SettingsUpdated);
        Assert.Contains(cleared, e => e.Payload.Contains("connection_deleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR87_S9_The_Fake_provider_accepts_any_token_without_HTTP_and_rejects_fake_invalid_ones()
    {
        var app = DeploymentHarness.Fake(factory);
        using var user = await ApiSession.RegisterAsync(app);

        var team = await ProjectApi.ReadAsync<VercelConnectionDto>(
            await user.Client.PostAsJsonAsync("/api/me/connections/vercel", new { label = "Fake", token = "fake_team_e2e" }, Ct), HttpStatusCode.Created);
        Assert.Equal(("fake-team", VercelConnectionStatus.Valid), (team.TeamSlug, team.Status));

        await Problems.AssertAsync(
            await user.Client.PostAsJsonAsync("/api/me/connections/vercel", new { label = "Bad", token = "fake_invalid_token" }, Ct), HttpStatusCode.BadRequest, ErrorCodes.CredentialInvalid);
    }
}
