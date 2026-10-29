using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Connections;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Platforms;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Connections;

/// <summary>
/// FR-142 (and v2 FR-87 for Vercel): platform connections against stub platform APIs (never a real host): validation per
/// provider (§4.20, §4.21), encryption, rejection, missing permissions, rate limits, re-testing (invalid + owner
/// notification), renaming, deletion (FR-144 bindings cleared), and picker data.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class PlatformConnectionTests(AideApiFactory factory)
{
    private const string Path = "/api/me/connections/platforms";
    private const string TeamToken = "vcp_teamScopedSecretToken0123456789";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri U(string path) => new(path, UriKind.Relative);

    /// <summary>The app whose <paramref name="clientName"/> HttpClient is answered by <paramref name="api"/>.</summary>
    private static WebApplicationFactory<Program> App(WebApplicationFactory<Program> factory, string clientName, FakeGitHubApi api) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => api)));

    private static Task<HttpResponseMessage> CreateAsync(ApiSession user, string provider, string token, string? accountId = null, string label = "Mine") =>
        user.Client.PostAsJsonAsync(Path, new { provider, label, token, accountId }, Ct);

    private static Task<List<AuditEvent>> InvalidEventsAsync(WebApplicationFactory<Program> app, Guid connectionId) =>
        ProjectApi.WithDbAsync(app, async db => (await db.Events.AsNoTracking().Where(e => e.Type == EventTypes.ConnectionInvalid).ToListAsync(Ct))
            .Where(e => e.Payload.Contains(connectionId.ToString(), StringComparison.Ordinal)).ToList());

    // ---------------------------------------------------------------- Vercel (v2 logic kept)

    private static FakeGitHubApi TeamVercel() => new FakeGitHubApi()
        .On(HttpMethod.Get, "/v2/user", HttpStatusCode.OK, """{"user":{"id":"usr_ana","username":"ana-dev","email":"ana@example.com"}}""")
        .On(HttpMethod.Get, "/v5/user/tokens/current", HttpStatusCode.OK, """{"token":{"id":"tok_1","name":"aide","expiresAt":1893456000000,"scopes":[{"type":"team","teamId":"team_acme","origin":"manual"}]}}""")
        .On(HttpMethod.Get, "/v2/teams/team_acme", HttpStatusCode.OK, """{"id":"team_acme","slug":"acme","name":"Acme"}""")
        .On(HttpMethod.Get, "/v9/projects", HttpStatusCode.OK, """{"projects":[{"id":"prj_todo","name":"todo","link":{"type":"github","org":"acme","repo":"todo"}}]}""");

    [Fact]
    public async Task FR142_FR87_A_valid_Vercel_team_token_is_saved_encrypted_with_its_team_and_never_returned()
    {
        var vercel = TeamVercel();
        var app = App(factory, VercelDeploymentProvider.HttpClientName, vercel);
        using var user = await ApiSession.RegisterAsync(app);

        var response = await CreateAsync(user, "vercel", TeamToken, label: " Acme team ");

        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain(TeamToken, text, StringComparison.Ordinal);
        var dto = JsonSerializer.Deserialize<PlatformConnectionDto>(text, ApiSession.Json)!;
        Assert.Equal((PlatformProvider.Vercel, "Acme team", "6789", "usr_ana", "ana-dev"), (dto.Provider, dto.Label, dto.Last4, dto.AccountId, dto.AccountName));
        Assert.Equal(("team_acme", "acme", PlatformConnectionStatus.Valid), (dto.Metadata.TeamId, dto.Metadata.TeamSlug, dto.Status));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1893456000000), dto.ExpiresAt);
        Assert.NotNull(dto.LastValidatedAt);
        Assert.All(vercel.Requests, r => Assert.Equal($"Bearer {TeamToken}", r.Authorization));

        var row = await ProjectApi.WithDbAsync(app, db => db.PlatformConnections.AsNoTracking().SingleAsync(c => c.Id == dto.Id, Ct));
        Assert.DoesNotContain(TeamToken, row.SecretEncrypted, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(TeamToken, StringComparison.Ordinal));

        var listed = await ProjectApi.ReadAsync<List<PlatformConnectionDto>>(await user.Client.GetAsync(U(Path), Ct));
        Assert.Equal(dto.Id, Assert.Single(listed).Id);

        // Picker: the team the token is scoped to, and its projects (with teamId).
        var accounts = await ProjectApi.ReadAsync<List<PlatformAccountDto>>(await user.Client.GetAsync(U($"{Path}/{dto.Id}/accounts"), Ct));
        Assert.Equal(("team_acme", "acme"), (Assert.Single(accounts).Id, accounts[0].Slug));
        var projects = await ProjectApi.ReadAsync<List<PlatformResourceDto>>(await user.Client.GetAsync(U($"{Path}/{dto.Id}/resources"), Ct));
        Assert.Equal(("prj_todo", "todo", "acme/todo"), (Assert.Single(projects).Id, projects[0].Name, projects[0].Detail));
        Assert.Contains(vercel.Requests, r => r.Path == "/v9/projects" && r.Query.Contains("teamId=team_acme", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR142_FR87_A_token_Vercel_rejects_is_CREDENTIAL_INVALID_and_nothing_is_saved()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v2/user", HttpStatusCode.Forbidden, """{"error":{"code":"forbidden","message":"Not authorized","invalidToken":true}}""");
        var app = App(factory, VercelDeploymentProvider.HttpClientName, vercel);
        using var user = await ApiSession.RegisterAsync(app);

        await Problems.AssertAsync(await CreateAsync(user, "vercel", "vcp_rejected"), HttpStatusCode.BadRequest, ErrorCodes.CredentialInvalid);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.PlatformConnections.AnyAsync(c => c.UserId == user.User!.Id, Ct)));
    }

    [Fact]
    public async Task FR142_A_platform_that_cannot_be_reached_is_UPSTREAM_ERROR_and_nothing_is_saved()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        foreach (var provider in new[] { "vercel", "netlify", "cloudflare", "render", "railway", "supabase" })
        {
            await Problems.AssertAsync(await CreateAsync(user, provider, "tok_unreachable"), HttpStatusCode.BadGateway, ErrorCodes.UpstreamError);
        }

        Assert.False(await ProjectApi.WithDbAsync(factory, db => db.PlatformConnections.AnyAsync(c => c.UserId == user.User!.Id, Ct)));
    }

    [Fact]
    public async Task FR142_Testing_again_marks_a_rejected_token_invalid_notifies_its_owner_once_and_renaming_keeps_it_secret()
    {
        var rejected = false;
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v2/user", _ => rejected
                ? (HttpStatusCode.Unauthorized, """{"error":{"code":"forbidden","message":"Not authorized"}}""")
                : (HttpStatusCode.OK, """{"user":{"id":"usr_c","username":"cleo"}}"""));
        var app = App(factory, VercelDeploymentProvider.HttpClientName, vercel);
        using var user = await ApiSession.RegisterAsync(app);
        var created = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "vercel", "vcp_willBeRevoked"), HttpStatusCode.Created);

        rejected = true;
        var tested = await ProjectApi.ReadAsync<PlatformConnectionDto>(await user.Client.PostAsync(U($"{Path}/{created.Id}/test"), null, Ct));
        Assert.Equal(PlatformConnectionStatus.Invalid, tested.Status);
        await ProjectApi.ReadAsync<PlatformConnectionDto>(await user.Client.PostAsync(U($"{Path}/{created.Id}/test"), null, Ct));

        var events = await InvalidEventsAsync(app, created.Id);
        var payload = JsonDocument.Parse(Assert.Single(events).Payload).RootElement;
        Assert.Equal(("vercel", user.User!.Id.ToString()), (payload.GetProperty("provider").GetString(), payload.GetProperty("userId").GetString()));
        Assert.Null(events[0].ProjectId);

        var renamed = await ProjectApi.ReadAsync<PlatformConnectionDto>(await ProjectApi.PatchAsync(user.Client, $"{Path}/{created.Id}", new { label = " Renamed " }));
        Assert.Equal(("Renamed", PlatformConnectionStatus.Invalid), (renamed.Label, renamed.Status));

        // An invalid connection's picker data isn't fetched: test it again or add a new token.
        await Problems.AssertAsync(await user.Client.GetAsync(U($"{Path}/{created.Id}/accounts"), Ct), HttpStatusCode.BadRequest, ErrorCodes.CredentialInvalid);

        // A token that works again revives it.
        rejected = false;
        var revived = await ProjectApi.ReadAsync<PlatformConnectionDto>(await user.Client.PostAsync(U($"{Path}/{created.Id}/test"), null, Ct));
        Assert.Equal(PlatformConnectionStatus.Valid, revived.Status);
    }

    [Fact]
    public async Task FR142_FR144_Deleting_a_connection_clears_the_bindings_of_its_targets_and_keeps_the_targets()
    {
        var app = App(factory, VercelDeploymentProvider.HttpClientName, TeamVercel());
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_bindingClearToken" });
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id);
        await DeploymentHarness.BindAsync(app, project.Id, connection, owner.User!.Id);
        var bound = await ProjectApi.ReadAsync<List<PlatformConnectionDto>>(await owner.Client.GetAsync(U(Path), Ct));
        Assert.Equal(project.Id, Assert.Single(Assert.Single(bound).BoundProjects).Id);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync(U($"{Path}/{connection}"), Ct)).StatusCode);

        var target = await DeploymentHarness.FrontendTargetAsync(app, project.Id);
        Assert.Equal(((Guid?)null, (Guid?)null, DeploymentHarness.VercelProjectId, DeploymentTargetStatus.Attention), (target.ConnectionId, target.BoundByUserId, target.ResourceId, target.Status));
        var cleared = await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.SettingsUpdated);
        Assert.Contains(cleared, e => e.Payload.Contains("connection_deleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR142_S9_The_Fake_provider_accepts_any_token_without_HTTP_and_rejects_fake_invalid_ones()
    {
        var app = DeploymentHarness.Fake(factory);
        using var user = await ApiSession.RegisterAsync(app);

        var team = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "vercel", "fake_team_e2e"), HttpStatusCode.Created);
        Assert.Equal(("fake-team", PlatformConnectionStatus.Valid), (team.Metadata.TeamSlug, team.Status));
        var netlify = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "netlify", "fake_netlify"), HttpStatusCode.Created);
        Assert.Equal((FakePlatformConnectionValidator.AccountId, PlatformConnectionStatus.Valid), (netlify.AccountId, netlify.Status));
        var supabase = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "supabase", "fake_scope_supabase"), HttpStatusCode.Created);
        Assert.Equal(PlatformConnectionStatus.InsufficientScope, supabase.Status);

        await Problems.AssertAsync(await CreateAsync(user, "render", "fake_invalid_token"), HttpStatusCode.BadRequest, ErrorCodes.CredentialInvalid);
    }

    // ---------------------------------------------------------------- Netlify

    [Fact]
    public async Task FR142_Netlify_checks_the_user_and_accounts_and_lists_sites()
    {
        var netlify = new FakeGitHubApi()
            .On(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, """{"id":"u_1","full_name":"Ana Dev","email":"ana@example.com"}""")
            .On(HttpMethod.Get, "/api/v1/accounts", HttpStatusCode.OK, """[{"id":"acc_1","name":"Acme","slug":"acme","type_name":"Pro"},{"id":"acc_2","name":"Ana","slug":"ana","type_name":"Starter"}]""")
            .On(HttpMethod.Get, "/api/v1/acme/sites", HttpStatusCode.OK, """[{"id":"site_1","name":"todo","ssl_url":"https://todo.netlify.app","build_settings":{"repo_path":"acme/todo"}}]""");
        var app = App(factory, PlatformHosts.NetlifyClient, netlify);
        using var user = await ApiSession.RegisterAsync(app);

        var dto = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "netlify", "nfp_netlifyToken1234", accountId: "acme"), HttpStatusCode.Created);

        Assert.Equal(("acc_1", "Acme", PlatformConnectionStatus.Valid), (dto.AccountId, dto.AccountName, dto.Status));
        Assert.Equal(["acc_1", "acc_2"], dto.Metadata.Accounts.Select(a => a.Id));
        Assert.All(netlify.Requests, r => Assert.Equal("Bearer nfp_netlifyToken1234", r.Authorization));

        var sites = await ProjectApi.ReadAsync<List<PlatformResourceDto>>(await user.Client.GetAsync(U($"{Path}/{dto.Id}/resources?type=sites"), Ct));
        Assert.Equal(("site_1", "https://todo.netlify.app", "acme/todo"), (Assert.Single(sites).Id, sites[0].Url, sites[0].Detail));

        // Netlify lists sites only.
        await Problems.AssertAsync(await user.Client.GetAsync(U($"{Path}/{dto.Id}/resources?type=workers"), Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // An account the token doesn't reach.
        await Problems.AssertAsync(await CreateAsync(user, "netlify", "nfp_netlifyToken1234", accountId: "someone-else"), HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR142_A_platform_429_is_UPSTREAM_RATE_LIMITED_with_Retry_After()
    {
        var netlify = new FakeGitHubApi()
            .On(HttpMethod.Get, "/api/v1/user", HttpStatusCode.TooManyRequests, """{"message":"Rate limit exceeded"}""", new Dictionary<string, string> { ["Retry-After"] = "30" });
        var app = App(factory, PlatformHosts.NetlifyClient, netlify);
        using var user = await ApiSession.RegisterAsync(app);

        var response = await CreateAsync(user, "netlify", "nfp_limited");

        await Problems.AssertAsync(response, HttpStatusCode.TooManyRequests, ErrorCodes.UpstreamRateLimited);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    // ---------------------------------------------------------------- Cloudflare

    [Fact]
    public async Task FR142_Cloudflare_user_token_records_its_policies_and_lists_workers_and_pages_projects()
    {
        var cloudflare = new FakeGitHubApi()
            .On(HttpMethod.Get, "/client/v4/user/tokens/verify", HttpStatusCode.OK, """{"success":true,"result":{"id":"tok_cf","status":"active","expires_on":"2027-01-01T00:00:00Z"}}""")
            .On(HttpMethod.Get, "/client/v4/accounts", HttpStatusCode.OK, """{"success":true,"result":[{"id":"cf_acc","name":"Acme","type":"standard"}]}""")
            .On(HttpMethod.Get, "/client/v4/user/tokens/tok_cf", HttpStatusCode.OK,
                """{"success":true,"result":{"policies":[{"effect":"allow","permission_groups":[{"name":"Workers Scripts Write"},{"name":"Workers CI Write"}]},{"permission_groups":[{"name":"Account Settings Read"}]}]}}""")
            .On(HttpMethod.Get, "/client/v4/accounts/cf_acc/workers/scripts", HttpStatusCode.OK, """{"success":true,"result":[{"id":"todo-worker"}]}""")
            .On(HttpMethod.Get, "/client/v4/accounts/cf_acc/pages/projects", HttpStatusCode.OK, """{"success":true,"result":[{"name":"todo-pages","subdomain":"todo-pages.pages.dev","production_branch":"main"}]}""");
        var app = App(factory, PlatformHosts.CloudflareClient, cloudflare);
        using var user = await ApiSession.RegisterAsync(app);

        var dto = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "cloudflare", "cf_userToken0123"), HttpStatusCode.Created);

        Assert.Equal(("cf_acc", "Acme", "user"), (dto.AccountId, dto.AccountName, dto.Metadata.TokenKind));
        Assert.Equal(["Account Settings Read", "Workers CI Write", "Workers Scripts Write"], dto.Metadata.Policies);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), dto.ExpiresAt);

        var workers = await ProjectApi.ReadAsync<List<PlatformResourceDto>>(await user.Client.GetAsync(U($"{Path}/{dto.Id}/resources"), Ct));
        Assert.Equal("todo-worker", Assert.Single(workers).Id);
        var pages = await ProjectApi.ReadAsync<List<PlatformResourceDto>>(await user.Client.GetAsync(U($"{Path}/{dto.Id}/resources?type=projects"), Ct));
        Assert.Equal(("todo-pages", "https://todo-pages.pages.dev"), (Assert.Single(pages).Id, pages[0].Url));
    }

    [Fact]
    public async Task FR142_A_Cloudflare_account_owned_cfat_token_is_CONNECTION_SCOPE_INSUFFICIENT_without_calling_Cloudflare()
    {
        var cloudflare = new FakeGitHubApi();
        var app = App(factory, PlatformHosts.CloudflareClient, cloudflare);
        using var user = await ApiSession.RegisterAsync(app);

        var problem = await Problems.AssertAsync(await CreateAsync(user, "cloudflare", "cfat_accountOwnedToken"), HttpStatusCode.BadRequest, ErrorCodes.ConnectionScopeInsufficient);

        Assert.Contains("user API token", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Empty(cloudflare.Requests);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.PlatformConnections.AnyAsync(c => c.UserId == user.User!.Id, Ct)));
    }

    [Fact]
    public async Task FR142_A_Cloudflare_token_without_optional_permissions_saves_valid_and_a_refused_picker_request_fails_alone()
    {
        const string NotAllowed = """{"success":false,"errors":[{"code":9109,"message":"Unauthorized to access requested resource"}]}""";
        var cloudflare = new FakeGitHubApi()
            .On(HttpMethod.Get, "/client/v4/user/tokens/verify", HttpStatusCode.OK, """{"success":true,"result":{"id":"tok_cf","status":"active"}}""")
            .On(HttpMethod.Get, "/client/v4/accounts", HttpStatusCode.OK, """{"success":true,"result":[{"id":"cf_acc","name":"Acme"}]}""")
            .On(HttpMethod.Get, "/client/v4/user/tokens/tok_cf", HttpStatusCode.Forbidden, NotAllowed)
            .On(HttpMethod.Get, "/client/v4/accounts/cf_acc/pages/projects", HttpStatusCode.Forbidden,
                """{"success":false,"errors":[{"code":10000,"message":"Authentication error"}]}""");
        var app = App(factory, PlatformHosts.CloudflareClient, cloudflare);
        using var user = await ApiSession.RegisterAsync(app);

        // No "API Tokens Read" (not required by §4.20): valid, policies unknown.
        var dto = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "cloudflare", "cf_noTokenRead"), HttpStatusCode.Created);
        Assert.Equal(PlatformConnectionStatus.Valid, dto.Status);
        Assert.Empty(dto.Metadata.Policies);
        Assert.Empty(dto.Metadata.MissingPermissions);

        // No Pages Edit: the Pages picker fails, the connection stays valid and nobody is notified.
        await Problems.AssertAsync(
            await user.Client.GetAsync(U($"{Path}/{dto.Id}/resources?type=projects"), Ct), HttpStatusCode.BadRequest, ErrorCodes.ConnectionScopeInsufficient);
        var row = await ProjectApi.WithDbAsync(app, db => db.PlatformConnections.AsNoTracking().SingleAsync(c => c.Id == dto.Id, Ct));
        Assert.Equal(PlatformConnectionStatus.Valid, row.Status);
        Assert.Empty(await InvalidEventsAsync(app, dto.Id));
    }

    [Fact]
    public async Task FR142_A_Cloudflare_invalid_token_403_is_CREDENTIAL_INVALID()
    {
        var cloudflare = new FakeGitHubApi()
            .On(HttpMethod.Get, "/client/v4/user/tokens/verify", HttpStatusCode.Forbidden, """{"success":false,"errors":[{"code":1000,"message":"Invalid API Token"}]}""");
        var app = App(factory, PlatformHosts.CloudflareClient, cloudflare);
        using var user = await ApiSession.RegisterAsync(app);

        await Problems.AssertAsync(await CreateAsync(user, "cloudflare", "cf_badToken"), HttpStatusCode.BadRequest, ErrorCodes.CredentialInvalid);
    }

    // ---------------------------------------------------------------- Render

    [Fact]
    public async Task FR142_Render_checks_the_key_lists_workspaces_from_owners_and_services()
    {
        var render = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v1/users", HttpStatusCode.OK, """{"name":"Ana","email":"ana@example.com"}""")
            .On(HttpMethod.Get, "/v1/owners", HttpStatusCode.OK, """[{"owner":{"id":"tea_1","name":"Acme","type":"team"},"cursor":"c1"},{"owner":{"id":"usr_1","name":"Ana","type":"user"},"cursor":"c2"}]""")
            .On(HttpMethod.Get, "/v1/services", HttpStatusCode.OK, """[{"service":{"id":"srv_1","name":"todo-api","type":"web_service","serviceDetails":{"url":"https://todo-api.onrender.com"}},"cursor":"c"}]""");
        var app = App(factory, PlatformHosts.RenderClient, render);
        using var user = await ApiSession.RegisterAsync(app);

        var dto = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "render", "rnd_renderKey1234", accountId: "tea_1"), HttpStatusCode.Created);

        Assert.Equal(("tea_1", "Acme"), (dto.AccountId, dto.AccountName));
        Assert.Equal(["tea_1", "usr_1"], dto.Metadata.Accounts.Select(a => a.Id));

        var services = await ProjectApi.ReadAsync<List<PlatformResourceDto>>(await user.Client.GetAsync(U($"{Path}/{dto.Id}/resources?type=services"), Ct));
        Assert.Equal(("srv_1", "web_service"), (Assert.Single(services).Id, services[0].Detail));
        Assert.Contains(render.Requests, r => r.Path == "/v1/services" && r.Query.Contains("ownerId=tea_1", StringComparison.Ordinal));
        var regions = await ProjectApi.ReadAsync<List<PlatformResourceDto>>(await user.Client.GetAsync(U($"{Path}/{dto.Id}/resources?type=regions"), Ct));
        Assert.Contains(regions, r => r.Id == "frankfurt");

        var accounts = await ProjectApi.ReadAsync<List<PlatformAccountDto>>(await user.Client.GetAsync(U($"{Path}/{dto.Id}/accounts"), Ct));
        Assert.Equal(2, accounts.Count);
    }

    // ---------------------------------------------------------------- Railway

    [Fact]
    public async Task FR142_Railway_account_token_answers_me_and_a_workspace_token_its_workspace()
    {
        var railway = new FakeGitHubApi()
            .On(HttpMethod.Post, "/graphql/v2", request =>
                request.Authorization == "Bearer rw_workspaceToken" && request.Body.Contains("me {", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"errors":[{"message":"Not Authorized"}],"data":null}""")
                : request.Body.Contains("me {", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"data":{"me":{"id":"u_1","name":"Ana","email":"ana@example.com","workspaces":[{"id":"ws_1","name":"Acme"}]}}}""")
                : request.Body.Contains("workspace(", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"data":{"workspace":{"id":"ws_2","name":"Team"}}}""")
                : (HttpStatusCode.OK, """{"data":{"projects":{"edges":[{"node":{"id":"prj_r","name":"todo"}}]}}}"""));
        var app = App(factory, PlatformHosts.RailwayClient, railway);
        using var user = await ApiSession.RegisterAsync(app);

        var account = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "railway", "rw_accountToken"), HttpStatusCode.Created);
        Assert.Equal(("ws_1", "Acme", "account"), (account.AccountId, account.AccountName, account.Metadata.TokenKind));

        var workspace = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "railway", "rw_workspaceToken", accountId: "ws_2"), HttpStatusCode.Created);
        Assert.Equal(("ws_2", "Team", "workspace"), (workspace.AccountId, workspace.AccountName, workspace.Metadata.TokenKind));
        Assert.All(railway.Requests, r => Assert.StartsWith("Bearer rw_", r.Authorization, StringComparison.Ordinal));

        // Without its workspace id, a workspace token can't be checked: rejected.
        await Problems.AssertAsync(await CreateAsync(user, "railway", "rw_workspaceToken"), HttpStatusCode.BadRequest, ErrorCodes.CredentialInvalid);

        var projects = await ProjectApi.ReadAsync<List<PlatformResourceDto>>(await user.Client.GetAsync(U($"{Path}/{workspace.Id}/resources?type=projects"), Ct));
        Assert.Equal("prj_r", Assert.Single(projects).Id);
    }

    // ---------------------------------------------------------------- Supabase

    [Fact]
    public async Task FR142_Supabase_valid_token_lists_orgs_and_projects()
    {
        var supabase = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v1/organizations", HttpStatusCode.OK, """[{"id":"acme-org","slug":"acme-org","name":"Acme"}]""")
            .On(HttpMethod.Get, "/v1/organizations/acme-org/projects", HttpStatusCode.OK, """{"projects":[{"ref":"abcdefghijklmnop","name":"todo","region":"eu-west-1","status":"ACTIVE_HEALTHY"}]}""")
            .On(HttpMethod.Get, "/v1/projects/abcdefghijklmnop/api-keys", HttpStatusCode.OK, """[{"name":"anon","type":"publishable"}]""");
        var app = App(factory, PlatformHosts.SupabaseClient, supabase);
        using var user = await ApiSession.RegisterAsync(app);

        var dto = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "supabase", "sbp_scopedToken1234"), HttpStatusCode.Created);

        Assert.Equal(("acme-org", "Acme", PlatformConnectionStatus.Valid), (dto.AccountId, dto.AccountName, dto.Status));
        Assert.Empty(dto.Metadata.MissingPermissions);
        var projects = await ProjectApi.ReadAsync<List<PlatformResourceDto>>(await user.Client.GetAsync(U($"{Path}/{dto.Id}/resources?type=projects"), Ct));
        Assert.Equal(("abcdefghijklmnop", "https://abcdefghijklmnop.supabase.co"), (Assert.Single(projects).Id, projects[0].Url));
        var orgs = await ProjectApi.ReadAsync<List<PlatformResourceDto>>(await user.Client.GetAsync(U($"{Path}/{dto.Id}/resources?type=orgs"), Ct));
        Assert.Equal("acme-org", Assert.Single(orgs).Id);
    }

    [Fact]
    public async Task FR142_A_Supabase_token_without_API_Key_Secrets_Read_saves_as_insufficient_scope_with_the_missing_permission()
    {
        var supabase = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v1/organizations", HttpStatusCode.OK, """[{"id":"acme-org","slug":"acme-org","name":"Acme"}]""")
            .On(HttpMethod.Get, "/v1/organizations/acme-org/projects", HttpStatusCode.OK, """[{"ref":"abcdefghijklmnop","name":"todo"}]""")
            .On(HttpMethod.Get, "/v1/projects/abcdefghijklmnop/api-keys", HttpStatusCode.Forbidden,
                """{"message":"Your token is missing permissions","missing_permissions":["API Key Secrets: Read"]}""");
        var app = App(factory, PlatformHosts.SupabaseClient, supabase);
        using var user = await ApiSession.RegisterAsync(app);

        var dto = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "supabase", "sbp_noSecrets"), HttpStatusCode.Created);

        Assert.Equal(PlatformConnectionStatus.InsufficientScope, dto.Status);
        Assert.Equal(["API Key Secrets: Read"], dto.Metadata.MissingPermissions);
        Assert.Equal("acme-org", dto.AccountId);
    }

    [Fact]
    public async Task FR142_A_Supabase_403_without_names_still_saves_as_insufficient_scope_with_the_probed_permission()
    {
        var supabase = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v1/organizations", HttpStatusCode.OK, """[{"id":"acme-org","name":"Acme"}]""")
            .On(HttpMethod.Get, "/v1/organizations/acme-org/projects", HttpStatusCode.OK, """[{"ref":"abcdefghijklmnop","name":"todo"}]""")
            .On(HttpMethod.Get, "/v1/projects/abcdefghijklmnop/api-keys", HttpStatusCode.Forbidden, """{"message":"Forbidden"}""");
        var app = App(factory, PlatformHosts.SupabaseClient, supabase);
        using var user = await ApiSession.RegisterAsync(app);

        var dto = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "supabase", "sbp_noNames"), HttpStatusCode.Created);

        Assert.Equal(PlatformConnectionStatus.InsufficientScope, dto.Status);
        Assert.Equal([SupabaseConnectionValidator.ApiKeySecretsRead], dto.Metadata.MissingPermissions);
    }

    [Fact]
    public async Task FR142_A_Supabase_401_is_CREDENTIAL_INVALID_and_on_retest_marks_the_connection_invalid_with_a_notification()
    {
        var revoked = false;
        var supabase = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v1/organizations", _ => revoked
                ? (HttpStatusCode.Unauthorized, """{"message":"Unauthorized"}""")
                : (HttpStatusCode.OK, """[{"id":"acme-org","name":"Acme"}]"""))
            .On(HttpMethod.Get, "/v1/organizations/acme-org/projects", HttpStatusCode.OK, "[]");
        var app = App(factory, PlatformHosts.SupabaseClient, supabase);
        using var user = await ApiSession.RegisterAsync(app);
        var dto = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "supabase", "sbp_revokedLater"), HttpStatusCode.Created);

        revoked = true;
        await Problems.AssertAsync(await CreateAsync(user, "supabase", "sbp_revokedLater"), HttpStatusCode.BadRequest, ErrorCodes.CredentialInvalid);
        var tested = await ProjectApi.ReadAsync<PlatformConnectionDto>(await user.Client.PostAsync(U($"{Path}/{dto.Id}/test"), null, Ct));

        Assert.Equal(PlatformConnectionStatus.Invalid, tested.Status);
        var payload = JsonDocument.Parse(Assert.Single(await InvalidEventsAsync(app, dto.Id)).Payload).RootElement;
        Assert.Equal("supabase", payload.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task FR142_A_picker_rejection_marks_the_connection_invalid_and_notifies_its_owner()
    {
        var revoked = false;
        var render = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v1/users", HttpStatusCode.OK, """{"name":"Ana"}""")
            .On(HttpMethod.Get, "/v1/owners", _ => revoked ? (HttpStatusCode.Unauthorized, """{"message":"unauthorized"}""") : (HttpStatusCode.OK, """[{"owner":{"id":"tea_1","name":"Acme"}}]"""));
        var app = App(factory, PlatformHosts.RenderClient, render);
        using var user = await ApiSession.RegisterAsync(app);
        var dto = await ProjectApi.ReadAsync<PlatformConnectionDto>(await CreateAsync(user, "render", "rnd_key"), HttpStatusCode.Created);

        revoked = true;
        await Problems.AssertAsync(await user.Client.GetAsync(U($"{Path}/{dto.Id}/accounts"), Ct), HttpStatusCode.BadRequest, ErrorCodes.CredentialInvalid);

        var row = await ProjectApi.WithDbAsync(app, db => db.PlatformConnections.AsNoTracking().SingleAsync(c => c.Id == dto.Id, Ct));
        Assert.Equal(PlatformConnectionStatus.Invalid, row.Status);
        Assert.Single(await InvalidEventsAsync(app, dto.Id));
    }
}
