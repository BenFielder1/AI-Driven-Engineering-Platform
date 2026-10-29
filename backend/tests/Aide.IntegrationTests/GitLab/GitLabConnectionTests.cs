using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.GitLab;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.GitLab;

/// <summary>
/// FR-131 token fallback and token lifecycle, FR-132 repo picker and create, against a stub GitLab: scope checks, encryption,
/// refresh rotation persisted, invalid after a second 401 (with <c>connection_invalid</c>), and the create error map.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitLabConnectionTests(AideApiFactory factory)
{
    private const string Token = "glpat-0123456789abcdefghij";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeGitHubApi TokenGitLab(string scopes, string? expiresAt = "2027-06-01") => new FakeGitHubApi()
        .On(HttpMethod.Get, "/api/v4/user", HttpStatusCode.OK, """{"id":4242,"username":"ana","name":"Ana","state":"active"}""")
        .On(HttpMethod.Get, "/api/v4/personal_access_tokens/self", HttpStatusCode.OK,
            $$"""{"id":9,"name":"aide","revoked":false,"active":true,"scopes":{{scopes}},"expires_at":{{(expiresAt is null ? "null" : $"\"{expiresAt}\"")}}}""");

    [Fact]
    public async Task FR131_A_token_missing_write_repository_is_CONNECTION_SCOPE_INSUFFICIENT_and_nothing_is_saved()
    {
        var app = GitLabHarness.App(factory, TokenGitLab("""["api","read_user"]"""));
        using var user = await ApiSession.RegisterAsync(app);
        var instance = await GitLabHarness.SeedInstanceAsync(app, oauth: false);

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/gitlab", new { instanceId = instance, token = Token }, Ct);

        var problem = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ConnectionScopeInsufficient);
        Assert.Equal(["write_repository"], problem.GetProperty("details").EnumerateArray().Select(e => e.GetString()));
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.GitConnections.AnyAsync(c => c.UserId == user.User!.Id, Ct)));
    }

    [Fact]
    public async Task FR131_A_valid_token_is_saved_encrypted_with_its_expiry_warning_and_never_returned()
    {
        var soon = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var gitlab = TokenGitLab("""["api","write_repository"]""", soon);
        var app = GitLabHarness.App(factory, gitlab);
        using var user = await ApiSession.RegisterAsync(app);
        var instance = await GitLabHarness.SeedInstanceAsync(app, oauth: false);

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/gitlab", new { instanceId = instance, token = Token }, Ct);

        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        var dto = System.Text.Json.JsonSerializer.Deserialize<GitLabConnectionDto>(text, ApiSession.Json)!;
        Assert.Equal(("ana", "4242", GitConnectionAuthKind.Token, GitConnectionStatus.Valid, true), (dto.Login, dto.ExternalUserId, dto.AuthKind, dto.Status, dto.ExpiryWarning));
        Assert.All(gitlab.Requests, r => Assert.Equal($"Bearer {Token}", r.Authorization));
        var row = await ProjectApi.WithDbAsync(app, db => db.GitConnections.AsNoTracking().SingleAsync(c => c.Id == dto.Id, Ct));
        Assert.Equal(Token, GitLabHarness.Decrypt(app, row.AccessTokenEncrypted));
        Assert.DoesNotContain(Token, row.AccessTokenEncrypted, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR131_A_token_GitLab_rejects_is_CREDENTIAL_INVALID()
    {
        var gitlab = new FakeGitHubApi().On(HttpMethod.Get, "/api/v4/user", HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""");
        var app = GitLabHarness.App(factory, gitlab);
        using var user = await ApiSession.RegisterAsync(app);

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/gitlab", new { token = Token }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.CredentialInvalid);
    }

    [Fact]
    public async Task FR131_Connection_mutations_need_a_browser_session_and_others_connections_are_404()
    {
        var app = GitLabHarness.App(factory, TokenGitLab("""["api","write_repository"]"""));
        using var owner = await ApiSession.RegisterAsync(app);
        using var other = await ApiSession.RegisterAsync(app);
        var instance = await GitLabHarness.SeedInstanceAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, owner.User!.Id, instance);

        await Problems.AssertAsync(await other.Client.DeleteAsync(new Uri($"/api/me/connections/gitlab/{connection}", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await other.Client.GetAsync(new Uri($"/api/me/git/gitlab/repos?connectionId={connection}", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync(new Uri($"/api/me/connections/gitlab/{connection}", UriKind.Relative), Ct)).StatusCode);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.GitConnections.AnyAsync(c => c.Id == connection, Ct)));
    }

    [Fact]
    public async Task FR131_An_expiring_OAuth_token_is_refreshed_and_the_rotated_pair_is_stored_before_use()
    {
        var gitlab = new FakeGitHubApi()
            .On(HttpMethod.Post, "/oauth/token", HttpStatusCode.OK,
                """{"access_token":"gla-new-access","token_type":"Bearer","expires_in":7200,"refresh_token":"glr-new-refresh","scope":"api write_repository openid profile email"}""")
            .On(HttpMethod.Get, "/api/v4/projects", HttpStatusCode.OK,
                """[{"id":42,"name":"todo","path":"todo","path_with_namespace":"acme/todo","default_branch":"main","web_url":"https://gitlab.com/acme/todo","namespace":{"full_path":"acme"}}]""");
        var app = GitLabHarness.App(factory, gitlab, gitLabComOAuth: true);
        using var user = await ApiSession.RegisterAsync(app);
        var gitlabCom = await GitLabHarness.GitLabComIdAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, user.User!.Id, gitlabCom, accessToken: "gla-old-access", refreshToken: "glr-old-refresh",
            accessExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1), authKind: GitConnectionAuthKind.Oauth);

        var page = await ProjectApi.ReadAsync<GitLabRepoPageDto>(
            await user.Client.GetAsync(new Uri($"/api/me/git/gitlab/repos?connectionId={connection}&search=to", UriKind.Relative), Ct));

        Assert.Equal("acme/todo", Assert.Single(page.Items).FullPath);
        var refresh = GitLabHarness.Form(Assert.Single(gitlab.Requests, r => r.Path == "/oauth/token").Body);
        Assert.Equal(("refresh_token", "glr-old-refresh", GitLabHarness.CallbackUrl), (refresh["grant_type"], refresh["refresh_token"], refresh["redirect_uri"]));
        var projects = Assert.Single(gitlab.Requests, r => r.Path == "/api/v4/projects");
        Assert.Equal("Bearer gla-new-access", projects.Authorization);
        Assert.Contains("min_access_level=30", projects.Query, StringComparison.Ordinal);
        Assert.Contains("search=to", projects.Query, StringComparison.Ordinal);
        var row = await ProjectApi.WithDbAsync(app, db => db.GitConnections.AsNoTracking().SingleAsync(c => c.Id == connection, Ct));
        Assert.Equal(("gla-new-access", "glr-new-refresh"), (GitLabHarness.Decrypt(app, row.AccessTokenEncrypted), GitLabHarness.Decrypt(app, row.RefreshTokenEncrypted!)));
        Assert.True(row.AccessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(100));
    }

    [Fact]
    public async Task FR131_A_request_cancelled_after_GitLab_rotated_the_pair_still_stores_the_rotated_pair()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var gitlab = new FakeGitHubApi()
            .On(HttpMethod.Post, "/oauth/token", _ =>
            {
                // GitLab has rotated the pair (the old one is now dead) when the caller goes away.
                cancel.Cancel();
                return (HttpStatusCode.OK, """{"access_token":"gla-rotated","token_type":"Bearer","expires_in":7200,"refresh_token":"glr-rotated"}""");
            });
        var app = GitLabHarness.App(factory, gitlab, gitLabComOAuth: true);
        using var user = await ApiSession.RegisterAsync(app);
        var gitlabCom = await GitLabHarness.GitLabComIdAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, user.User!.Id, gitlabCom, accessToken: "gla-old", refreshToken: "glr-old",
            accessExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1), authKind: GitConnectionAuthKind.Oauth);

        var tokens = app.Services.GetRequiredService<Aide.Infrastructure.GitLab.IGitConnectionTokens>();
        var result = await tokens.GetAccessTokenAsync(connection, cancel.Token);

        Assert.True(cancel.IsCancellationRequested);
        Assert.True(result.IsSuccess);
        Assert.Equal("gla-rotated", result.Value.Token);
        var row = await ProjectApi.WithDbAsync(app, db => db.GitConnections.AsNoTracking().SingleAsync(c => c.Id == connection, Ct));
        Assert.Equal(GitConnectionStatus.Valid, row.Status);
        Assert.Equal(("gla-rotated", "glr-rotated"), (GitLabHarness.Decrypt(app, row.AccessTokenEncrypted), GitLabHarness.Decrypt(app, row.RefreshTokenEncrypted!)));
    }

    [Fact]
    public async Task FR131_A_refresh_blocked_by_the_SSRF_guard_is_GIT_HOST_NOT_ALLOWED_and_leaves_the_connection_valid()
    {
        // The stub replaces the SocketsHttpHandler whose ConnectCallback is the guard, so it fails the way the guard does
        // (the instance's name now resolves to a private address on a server that refuses private networks).
        var gitlab = new FakeGitHubApi()
            .On(HttpMethod.Post, "/oauth/token", _ => throw new HttpRequestException(
                "The connection was refused.", new Aide.Infrastructure.GitLab.GitHostBlockedException("gitlab.example resolves to a private address.")));
        var app = GitLabHarness.App(factory, gitlab);
        using var user = await ApiSession.RegisterAsync(app);
        var instance = await GitLabHarness.SeedInstanceAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, user.User!.Id, instance, accessToken: "gla-old", refreshToken: "glr-old",
            accessExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1), authKind: GitConnectionAuthKind.Oauth);

        var response = await user.Client.PostAsync(new Uri($"/api/me/connections/gitlab/{connection}/test", UriKind.Relative), null, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.GitHostNotAllowed);
        Assert.DoesNotContain(gitlab.Requests, r => r.Path == "/api/v4/user");
        var events = await ProjectApi.WithDbAsync(app, db => db.Events.AsNoTracking().Where(e => e.Type == EventTypes.ConnectionInvalid).ToListAsync(Ct));
        Assert.DoesNotContain(events, e => e.Payload.Contains(connection.ToString(), StringComparison.Ordinal));
        var row = await ProjectApi.WithDbAsync(app, db => db.GitConnections.AsNoTracking().SingleAsync(c => c.Id == connection, Ct));
        Assert.Equal(GitConnectionStatus.Valid, row.Status);
        Assert.Equal("glr-old", GitLabHarness.Decrypt(app, row.RefreshTokenEncrypted!));
    }

    [Fact]
    public async Task FR131_A_401_after_one_refresh_marks_the_connection_invalid_and_notifies_its_owner()
    {
        var gitlab = new FakeGitHubApi()
            .On(HttpMethod.Post, "/oauth/token", HttpStatusCode.OK, """{"access_token":"gla-refreshed","expires_in":7200,"refresh_token":"glr-refreshed"}""")
            .On(HttpMethod.Get, "/api/v4/projects", HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""");
        var app = GitLabHarness.App(factory, gitlab, gitLabComOAuth: true);
        using var user = await ApiSession.RegisterAsync(app);
        var gitlabCom = await GitLabHarness.GitLabComIdAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, user.User!.Id, gitlabCom, accessToken: "gla-revoked", refreshToken: "glr-current",
            accessExpiresAt: DateTimeOffset.UtcNow.AddHours(1), authKind: GitConnectionAuthKind.Oauth);

        var response = await user.Client.GetAsync(new Uri($"/api/me/git/gitlab/repos?connectionId={connection}", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.GitNotConnected);
        Assert.Equal(2, gitlab.Count(HttpMethod.Get, "/api/v4/projects"));
        Assert.Single(gitlab.Requests, r => r.Path == "/oauth/token");
        var row = await ProjectApi.WithDbAsync(app, db => db.GitConnections.AsNoTracking().SingleAsync(c => c.Id == connection, Ct));
        Assert.Equal(GitConnectionStatus.Invalid, row.Status);
        var events = await ProjectApi.WithDbAsync(app, db => db.Events.AsNoTracking().Where(e => e.Type == EventTypes.ConnectionInvalid).ToListAsync(Ct));
        Assert.Contains(events, e => e.Payload.Contains(connection.ToString(), StringComparison.Ordinal) && e.Payload.Contains("\"gitlab\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR132_Creating_acme_todo_app_in_group_acme_initialises_main_with_a_README()
    {
        var gitlab = new FakeGitHubApi()
            .On(HttpMethod.Get, "/api/v4/user", HttpStatusCode.OK, """{"id":4242,"username":"ana"}""")
            .On(HttpMethod.Get, "/api/v4/namespaces/acme", HttpStatusCode.OK, """{"id":7,"kind":"group","name":"Acme","full_path":"acme"}""")
            .On(HttpMethod.Post, "/api/v4/projects", HttpStatusCode.Created,
                """{"id":99,"name":"todo-app","path":"todo-app","path_with_namespace":"acme/todo-app","default_branch":"main","web_url":"https://gitlab.example/acme/todo-app","visibility":"private","namespace":{"full_path":"acme"}}""");
        var app = GitLabHarness.App(factory, gitlab);
        using var user = await ApiSession.RegisterAsync(app);
        var instance = await GitLabHarness.SeedInstanceAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, user.User!.Id, instance);

        var created = await ProjectApi.ReadAsync<GitLabRepoDto>(
            await user.Client.PostAsJsonAsync("/api/me/git/gitlab/repos", new { connectionId = connection, owner = "acme", name = "todo-app", @private = true }, Ct),
            HttpStatusCode.Created);

        Assert.Equal(("99", "acme/todo-app", "main", "private"), (created.Id, created.FullPath, created.DefaultBranch, created.Visibility));
        var body = GitLabHarness.Body(Assert.Single(gitlab.Requests, r => r.Method == "POST"));
        Assert.True(body.GetProperty("initialize_with_readme").GetBoolean());
        Assert.Equal(("main", "private", 7L), (body.GetProperty("default_branch").GetString(), body.GetProperty("visibility").GetString(), body.GetProperty("namespace_id").GetInt64()));
    }

    [Fact]
    public async Task FR132_A_taken_name_is_REPO_NAME_TAKEN_a_refused_group_FORBIDDEN_and_the_creation_limit_UPSTREAM_RATE_LIMITED()
    {
        var answer = (HttpStatusCode.BadRequest, """{"message":{"name":["has already been taken"],"path":["has already been taken"]}}""");
        var gitlab = new FakeGitHubApi()
            .On(HttpMethod.Get, "/api/v4/user", HttpStatusCode.OK, """{"id":4242,"username":"ana"}""")
            .On(HttpMethod.Get, "/api/v4/namespaces/acme", HttpStatusCode.OK, """{"id":7,"kind":"group","name":"Acme","full_path":"acme"}""")
            .On(HttpMethod.Post, "/api/v4/projects", _ => answer);
        var app = GitLabHarness.App(factory, gitlab);
        using var user = await ApiSession.RegisterAsync(app);
        var instance = await GitLabHarness.SeedInstanceAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, user.User!.Id, instance);
        var request = new { connectionId = connection, owner = "acme", name = "todo-app" };

        await Problems.AssertAsync(await user.Client.PostAsJsonAsync("/api/me/git/gitlab/repos", request, Ct), HttpStatusCode.Conflict, ErrorCodes.RepoNameTaken);

        answer = (HttpStatusCode.Forbidden, """{"message":"403 Forbidden"}""");
        await Problems.AssertAsync(await user.Client.PostAsJsonAsync("/api/me/git/gitlab/repos", request, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        answer = (HttpStatusCode.TooManyRequests, """{"message":"This endpoint has been requested too many times. Try again later."}""");
        var limited = await user.Client.PostAsJsonAsync("/api/me/git/gitlab/repos", request, Ct);
        await Problems.AssertAsync(limited, (HttpStatusCode)429, ErrorCodes.UpstreamRateLimited);
    }

    [Fact]
    public async Task FR132_Owners_are_the_personal_namespace_and_groups_with_their_creation_rights()
    {
        var gitlab = new FakeGitHubApi()
            .On(HttpMethod.Get, "/api/v4/user", HttpStatusCode.OK, """{"id":4242,"username":"ana","name":"Ana"}""")
            .On(HttpMethod.Get, "/api/v4/namespaces/ana", HttpStatusCode.OK, """{"id":5,"kind":"user","name":"Ana","full_path":"ana"}""")
            .On(HttpMethod.Get, "/api/v4/groups", r => r.Query.Contains("min_access_level=30", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """[{"id":7,"name":"Acme","full_path":"acme","full_name":"Acme","project_creation_level":"developer"},{"id":8,"name":"Ops","full_path":"ops","full_name":"Ops","project_creation_level":"maintainer"}]""")
                : (HttpStatusCode.OK, "[]"));
        var app = GitLabHarness.App(factory, gitlab);
        using var user = await ApiSession.RegisterAsync(app);
        var instance = await GitLabHarness.SeedInstanceAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, user.User!.Id, instance);

        var owners = await ProjectApi.ReadAsync<List<GitLabOwnerDto>>(await user.Client.GetAsync(new Uri($"/api/me/git/gitlab/owners?connectionId={connection}", UriKind.Relative), Ct));

        Assert.Equal(
            [(GitLabOwnerKind.User, "ana", true), (GitLabOwnerKind.Group, "acme", true), (GitLabOwnerKind.Group, "ops", false)],
            owners.Select(o => (o.Kind, o.FullPath, o.CanCreateProjects)));
        Assert.NotNull(owners[2].Reason);
    }
}
