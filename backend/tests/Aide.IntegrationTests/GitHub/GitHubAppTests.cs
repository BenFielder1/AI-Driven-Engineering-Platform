using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Auth;
using Aide.Api.Features.GitHub;
using Aide.Api.Features.Projects;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Infrastructure;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.GitHub;

/// <summary>
/// FR-80–FR-83, FR-86, §4.14 against a fake GitHub (<see cref="GitHubAppHarness"/>): the App's status and manifest flow,
/// sign-in and connect through the App's user authorization, the connection and its refresh, the repo picker, repository
/// creation, github_app projects and the PAT fallback's switch to the App.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitHubAppTests(AideApiFactory factory)
{
    private const long OrgInstallation = 2002;
    private const long UserInstallation = 2001;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri U(string path) => new(path, UriKind.Relative);

    private static long NewGitHubId() => RandomNumberGenerator.GetInt32(1, int.MaxValue) + 20_000_000_000L;

    // ---------------------------------------------------------------- FR-80 status and manifest

    [Fact]
    public async Task FR80_Without_an_App_the_status_is_not_configured()
    {
        using var admin = await ApiSession.AdminAsync(factory);

        var status = await ProjectApi.ReadAsync<AdminGitHubAppDto>(await admin.Client.GetAsync(U("/api/admin/integrations/github"), Ct));

        Assert.Equal((GitHubAppStatus.NotConfigured, (GitHubAppSource?)null, (string?)null, false), (status.Status, status.Source, status.InstallUrl, status.CanCreateRepositories));
        Assert.Empty(status.Permissions);
    }

    [Fact]
    public async Task FR80_A_configured_App_reports_its_status_install_link_and_permissions_never_its_secrets()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var admin = await ApiSession.AdminAsync(harness.App);

        var response = await admin.Client.GetAsync(U("/api/admin/integrations/github"), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var status = JsonSerializer.Deserialize<AdminGitHubAppDto>(text, ApiSession.Json)!;

        Assert.Equal((GitHubAppStatus.Configured, GitHubAppSource.Configuration, GitHubAppHarness.AppId, GitHubAppHarness.Slug), (status.Status, status.Source, status.AppId, status.Slug));
        Assert.Equal("https://github.com/apps/aide-test/installations/new", status.InstallUrl);
        Assert.Equal(("acme", GitHubOwnerType.Organization, true), (status.OwnerLogin, status.OwnerType, status.CanCreateRepositories));
        Assert.Equal("write", status.Permissions["contents"]);
        Assert.DoesNotContain(GitHubAppHarness.ClientSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        harness.AssertAppJwt(harness.Api.Requests.Single(r => r.Path == "/app").Authorization);
    }

    [Fact]
    public async Task FR80_Given_no_App_when_an_Admin_completes_the_manifest_flow_then_the_App_is_configured_and_sign_in_offers_GitHub()
    {
        var connectionString = await factory.CreateMigratedDatabaseAsync(Ct);
        await using var harness = GitHubAppHarness.Create(
            factory, configureApp: false, configure: b => b.UseSetting($"ConnectionStrings:{DependencyInjection.ConnectionStringName}", connectionString));
        using var admin = await ApiSession.RegisterAsync(harness.App); // the first account of this database is the Admin
        using var anonymous = new ApiSession(harness.App);
        Assert.False((await anonymous.Client.GetFromJsonAsync<AuthProvidersDto>("/api/auth/providers", ApiSession.Json, Ct))!.Github);

        var started = await ProjectApi.ReadAsync<GitHubAppManifestResponseDto>(
            await admin.Client.PostAsJsonAsync("/api/admin/integrations/github/manifest", new { ownerType = "user" }, Ct));

        // §4.14 manifest: name from App:PublicBaseUrl, sign-in callback, no webhook, the App permissions.
        var manifest = started.Manifest;
        Assert.Equal("AIDE (localhost)", manifest.Name);
        Assert.Equal(["http://localhost:8080/api/auth/github/callback"], manifest.CallbackUrls);
        Assert.Equal("http://localhost:8080/api/admin/integrations/github/callback", manifest.RedirectUrl);
        Assert.False(manifest.Public);
        Assert.Equal(
            ["administration=write", "checks=read", "contents=write", "emails=read", "metadata=read", "pull_requests=write", "statuses=read", "workflows=write"],
            manifest.DefaultPermissions.Select(p => $"{p.Key}={p.Value}").Order(StringComparer.Ordinal));
        Assert.Equal($"https://github.com/settings/apps/new?state={Uri.EscapeDataString(started.State)}", started.PostUrl);
        Assert.True(started.State.Length <= AdminGitHubLimits.StateMaxLength);

        const string pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIfake\n-----END RSA PRIVATE KEY-----\n";
        const string secret = "manifest-client-secret-abcdef";
        harness.Api.On(HttpMethod.Post, "/app-manifests/manifest-code/conversions", HttpStatusCode.Created, JsonSerializer.Serialize(new
        {
            id = 777001,
            slug = "aide-localhost",
            owner = new { login = "ben", type = "User" },
            client_id = "Iv23manifestClient",
            client_secret = secret,
            pem,
            html_url = "https://github.com/apps/aide-localhost",
            permissions = new { contents = "write", administration = "write", metadata = "read" },
        }));

        var callback = await admin.Client.GetAsync(U($"/api/admin/integrations/github/callback?code=manifest-code&state={Uri.EscapeDataString(started.State)}"), Ct);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/admin/integrations?github=configured", callback.Headers.Location?.OriginalString);
        var status = await ProjectApi.ReadAsync<AdminGitHubAppDto>(await admin.Client.GetAsync(U("/api/admin/integrations/github"), Ct));
        Assert.Equal((GitHubAppStatus.Configured, GitHubAppSource.Database, 777001L, "aide-localhost", "ben"), (status.Status, status.Source, status.AppId, status.Slug, status.OwnerLogin));
        Assert.Equal(("https://github.com/apps/aide-localhost/installations/new", true), (status.InstallUrl, status.CanCreateRepositories));

        // Stored encrypted (per-purpose Data Protection), never in clear text.
        var row = await ProjectApi.WithDbAsync(harness.App, db => db.GitHubApps.AsNoTracking().SingleAsync(Ct));
        Assert.Equal(("Iv23manifestClient", 777001L), (row.ClientId, row.AppId));
        Assert.DoesNotContain(secret, row.ClientSecretEncrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", row.PrivateKeyEncrypted, StringComparison.Ordinal);

        // The sign-in page now shows "Continue with GitHub" through the App (D-V2-7).
        var providers = await anonymous.Client.GetFromJsonAsync<AuthProvidersDto>("/api/auth/providers", ApiSession.Json, Ct);
        Assert.Equal((true, GitHubSignInMode.App), (providers!.Github, providers.GithubMode));

        // The state was single-use, and a second App can't be started.
        var replay = await admin.Client.GetAsync(U($"/api/admin/integrations/github/callback?code=manifest-code&state={Uri.EscapeDataString(started.State)}"), Ct);
        Assert.Equal("/admin/integrations?error=GITHUB_AUTH_FAILED", replay.Headers.Location?.OriginalString);
        await Problems.AssertAsync(
            await admin.Client.PostAsJsonAsync("/api/admin/integrations/github/manifest", new { ownerType = "user" }, Ct), HttpStatusCode.Conflict, ErrorCodes.Conflict);
        Assert.Equal(1, harness.Api.Count(HttpMethod.Post, "/app-manifests/manifest-code/conversions"));
    }

    [Fact]
    public async Task FR80_A_forged_or_foreign_manifest_state_redirects_with_GITHUB_AUTH_FAILED_and_exchanges_nothing()
    {
        await using var harness = GitHubAppHarness.Create(factory, configureApp: false);
        using var admin = await ApiSession.AdminAsync(harness.App);
        var started = await ProjectApi.ReadAsync<GitHubAppManifestResponseDto>(
            await admin.Client.PostAsJsonAsync("/api/admin/integrations/github/manifest", new { ownerType = "organization", org = "acme" }, Ct));
        Assert.StartsWith("https://github.com/organizations/acme/settings/apps/new?state=", started.PostUrl, StringComparison.Ordinal);

        var forged = await admin.Client.GetAsync(U("/api/admin/integrations/github/callback?code=c&state=forged"), Ct);
        var tampered = await admin.Client.GetAsync(U($"/api/admin/integrations/github/callback?code=c&state={Uri.EscapeDataString(started.State[..^2] + "AA")}"), Ct);

        foreach (var response in new[] { forged, tampered })
        {
            Assert.Equal("/admin/integrations?error=GITHUB_AUTH_FAILED", response.Headers.Location?.OriginalString);
        }

        Assert.DoesNotContain(harness.Api.Requests, r => r.Path.StartsWith("/app-manifests/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR80_An_organization_manifest_needs_the_org_and_a_configured_App_is_CONFLICT()
    {
        await using (var none = GitHubAppHarness.Create(factory, configureApp: false))
        {
            using var admin = await ApiSession.AdminAsync(none.App);
            var body = await Problems.AssertAsync(
                await admin.Client.PostAsJsonAsync("/api/admin/integrations/github/manifest", new { ownerType = "organization" }, Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
            Assert.True(body.GetProperty("errors").TryGetProperty("org", out _));
        }

        await using var harness = GitHubAppHarness.Create(factory);
        using var configured = await ApiSession.AdminAsync(harness.App);
        await Problems.AssertAsync(
            await configured.Client.PostAsJsonAsync("/api/admin/integrations/github/manifest", new { ownerType = "user" }, Ct), HttpStatusCode.Conflict, ErrorCodes.Conflict);
    }

    // ---------------------------------------------------------------- FR-80, D-V2-7 sign-in through the App

    [Fact]
    public async Task FR80_Given_a_v1_user_linked_to_GitHub_id_42_when_they_sign_in_through_the_App_then_they_reach_the_same_account_and_are_connected()
    {
        await using var harness = GitHubAppHarness.Create(factory, configure: b =>
        {
            // The v1 OAuth App is still configured, but no longer used once an App exists (D-V2-7).
            b.UseSetting("GitHub:OAuth:ClientId", "v1-oauth-client");
            b.UseSetting("GitHub:OAuth:ClientSecret", "v1-oauth-secret");
        });
        using var v1 = await ApiSession.RegisterAsync(harness.App);
        await ProjectApi.WithDbAsync(harness.App, db => db.Users.Where(u => u.Id == v1.User!.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.GithubId, "42"), Ct));
        harness.OnUser(42, "octo-v1", "octo-v1@example.com");
        using var browser = new ApiSession(harness.App);
        Assert.Equal(GitHubSignInMode.App, (await browser.Client.GetFromJsonAsync<AuthProvidersDto>("/api/auth/providers", ApiSession.Json, Ct))!.GithubMode);

        var callback = await GitHubAppHarness.RunAppFlowAsync(browser, "/api/auth/github?returnUrl=%2Fprojects");

        Assert.Equal("/projects", callback.Headers.Location?.OriginalString);
        var me = await browser.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        Assert.Equal(v1.User!.Id, me!.Id);

        // FR-81: signing in through the App connects GitHub; both tokens stored encrypted, expiring in 8 hours.
        var connection = await ProjectApi.WithDbAsync(harness.App, db => db.GitHubConnections.AsNoTracking().SingleAsync(c => c.UserId == v1.User.Id, Ct));
        Assert.Equal((42L, "octo-v1", GitHubConnectionStatus.Valid), (connection.GithubUserId, connection.Login, connection.Status));
        Assert.DoesNotContain(GitHubAppHarness.UserToken, connection.AccessTokenEncrypted, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubAppHarness.RefreshToken, connection.RefreshTokenEncrypted!, StringComparison.Ordinal);
        Assert.InRange(connection.AccessExpiresAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromHours(7.9), TimeSpan.FromHours(8.1));

        var exchange = harness.Api.Requests.Single(r => r.Path == "/login/oauth/access_token");
        var form = QueryHelpers.ParseQuery(exchange.Body);
        Assert.Equal((GitHubAppHarness.ClientId, "authorization_code"), (form["client_id"].ToString(), form["grant_type"].ToString()));
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(GitHubAppHarness.UserToken, StringComparison.Ordinal)
            || e.AllText.Contains(GitHubAppHarness.RefreshToken, StringComparison.Ordinal)
            || e.AllText.Contains(GitHubAppHarness.ClientSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR80_A_new_GitHub_user_signing_in_through_the_App_gets_an_account_and_a_connection()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        var githubId = NewGitHubId();
        var email = ApiSession.NewEmail("gh-app");
        harness.OnUser(githubId, $"new{githubId}", email);
        harness.OnInstallations();
        using var browser = new ApiSession(harness.App);

        var callback = await GitHubAppHarness.RunAppFlowAsync(browser, "/api/auth/github");

        Assert.Equal("/", callback.Headers.Location?.OriginalString);
        var me = await browser.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        Assert.Equal((email, true), (me!.Email, me.GithubLinked));
        var connection = await ProjectApi.ReadAsync<GitHubConnectionDto>(await browser.Client.GetAsync(U("/api/me/connections/github"), Ct));
        Assert.Equal((true, githubId), (connection.Connected, connection.GithubUserId));
    }

    // ---------------------------------------------------------------- FR-81 connection

    [Fact]
    public async Task FR81_The_connection_shows_login_status_and_installations_with_manage_links_and_never_a_token()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var user = await ApiSession.RegisterAsync(harness.App);
        var githubId = NewGitHubId();
        await harness.SeedConnectionAsync(user.User!.Id, githubId, "octo");
        harness.OnInstallations((UserInstallation, "octo", "User", "selected"), (OrgInstallation, "acme", "Organization", "all"));

        var response = await user.Client.GetAsync(U("/api/me/connections/github"), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var connection = JsonSerializer.Deserialize<GitHubConnectionDto>(text, ApiSession.Json)!;

        Assert.Equal((true, "octo", githubId, GitHubConnectionStatus.Valid), (connection.Connected, connection.Login, connection.GithubUserId, connection.Status));
        Assert.Equal("https://github.com/apps/aide-test/installations/new", connection.InstallUrl);
        Assert.Equal(
            [
                new GitHubInstallationDto(UserInstallation, new GitHubAccountDto("octo", GitHubOwnerType.User), GitHubRepositorySelection.Selected, "https://github.com/settings/installations/2001"),
                new GitHubInstallationDto(OrgInstallation, new GitHubAccountDto("acme", GitHubOwnerType.Organization), GitHubRepositorySelection.All, "https://github.com/organizations/acme/settings/installations/2002"),
            ],
            connection.Installations);
        Assert.DoesNotContain(GitHubAppHarness.UserToken, text, StringComparison.Ordinal);
        Assert.Equal($"Bearer {GitHubAppHarness.UserToken}", harness.Api.Requests.Single(r => r.Path == "/user/installations").Authorization);
    }

    [Fact]
    public async Task FR81_A_user_token_about_to_expire_is_refreshed_before_it_is_used()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var user = await ApiSession.RegisterAsync(harness.App);
        await harness.SeedConnectionAsync(user.User!.Id, NewGitHubId(), "octo", accessExpiresAt: DateTimeOffset.UtcNow.AddMinutes(2));
        harness.OnInstallations((UserInstallation, "octo", "User", "all"));

        var connection = await ProjectApi.ReadAsync<GitHubConnectionDto>(await user.Client.GetAsync(U("/api/me/connections/github"), Ct));

        var refresh = QueryHelpers.ParseQuery(harness.Api.Requests.Single(r => r.Path == "/login/oauth/access_token").Body);
        Assert.Equal(("refresh_token", GitHubAppHarness.RefreshToken), (refresh["grant_type"].ToString(), refresh["refresh_token"].ToString()));
        Assert.Equal($"Bearer {GitHubAppHarness.RefreshedToken}", harness.Api.Requests.Single(r => r.Path == "/user/installations").Authorization);
        Assert.InRange(connection.AccessExpiresAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromHours(7.9), TimeSpan.FromHours(8.1));
    }

    [Fact]
    public async Task S4_14_A_failed_refresh_marks_the_connection_invalid_records_CONNECTION_INVALID_and_the_picker_is_GITHUB_NOT_CONNECTED()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var user = await ApiSession.RegisterAsync(harness.App);
        await harness.SeedConnectionAsync(user.User!.Id, NewGitHubId(), "octo", accessExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        await ProjectApi.WithDbAsync(harness.App, async db =>
        {
            // A refresh token GitHub no longer accepts.
            var scope = harness.App.Services.GetRequiredService<Aide.Infrastructure.Security.ISecretProtectors>();
            var revoked = scope.ForPurpose(Aide.Infrastructure.Security.SecretPurposes.GitHubUserToken).Protect("ghr_revoked");
            await db.GitHubConnections.Where(c => c.UserId == user.User.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.RefreshTokenEncrypted, revoked), Ct);
        });

        await Problems.AssertAsync(await user.Client.GetAsync(U("/api/me/github/repos"), Ct), HttpStatusCode.Conflict, ErrorCodes.GitHubNotConnected);

        var connection = await ProjectApi.ReadAsync<GitHubConnectionDto>(await user.Client.GetAsync(U("/api/me/connections/github"), Ct));
        Assert.Equal((true, GitHubConnectionStatus.Invalid), (connection.Connected, connection.Status));
        Assert.Empty(connection.Installations);
        var invalid = await ProjectApi.WithDbAsync(harness.App, db => db.Events.AsNoTracking()
            .Where(e => e.Type == EventTypes.ConnectionInvalid && e.ProjectId == null).Select(e => e.Payload).ToListAsync(Ct));
        Assert.Contains(invalid, p => p.Contains(user.User.Id.ToString(), StringComparison.Ordinal) && p.Contains("\"github\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR81_Connect_links_GitHub_to_the_signed_in_user_and_returns_to_the_page()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var user = await ApiSession.RegisterAsync(harness.App);
        var githubId = NewGitHubId();
        harness.OnUser(githubId, "connector", null);

        var callback = await GitHubAppHarness.RunAppFlowAsync(user, "/api/me/connections/github/connect?returnUrl=%2Faccount%2Fconnections");

        Assert.Equal("/account/connections", callback.Headers.Location?.OriginalString);
        var me = await user.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        Assert.True(me!.GithubLinked);
        Assert.Equal(githubId, (await ProjectApi.WithDbAsync(harness.App, db => db.GitHubConnections.AsNoTracking().SingleAsync(c => c.UserId == user.User!.Id, Ct))).GithubUserId);
    }

    [Fact]
    public async Task FR81_Connecting_a_GitHub_account_linked_to_another_user_is_GITHUB_AUTH_FAILED()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var owner = await ApiSession.RegisterAsync(harness.App);
        using var other = await ApiSession.RegisterAsync(harness.App);
        var githubId = NewGitHubId();
        await ProjectApi.WithDbAsync(harness.App, db => db.Users.Where(u => u.Id == owner.User!.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.GithubId, githubId.ToString(System.Globalization.CultureInfo.InvariantCulture)), Ct));
        harness.OnUser(githubId, "taken", null);

        var callback = await GitHubAppHarness.RunAppFlowAsync(other, "/api/me/connections/github/connect?returnUrl=%2Faccount%2Fconnections");

        Assert.Equal("/account/connections?error=GITHUB_AUTH_FAILED&reason=already_linked", callback.Headers.Location?.OriginalString);
        Assert.False(await ProjectApi.WithDbAsync(harness.App, db => db.GitHubConnections.AnyAsync(c => c.UserId == other.User!.Id, Ct)));
    }

    [Fact]
    public async Task FR81_Without_an_App_connect_is_NOT_FOUND_and_disconnect_removes_the_connection()
    {
        using (var plain = await ApiSession.RegisterAsync(factory))
        {
            await Problems.AssertAsync(await plain.Client.GetAsync(U("/api/me/connections/github/connect"), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
            var none = await ProjectApi.ReadAsync<GitHubConnectionDto>(await plain.Client.GetAsync(U("/api/me/connections/github"), Ct));
            Assert.Equal((false, (string?)null, (string?)null), (none.Connected, none.Login, none.InstallUrl));
        }

        await using var harness = GitHubAppHarness.Create(factory);
        using var user = await ApiSession.RegisterAsync(harness.App);
        await harness.SeedConnectionAsync(user.User!.Id, NewGitHubId(), "octo");

        Assert.Equal(HttpStatusCode.NoContent, (await user.Client.DeleteAsync(U("/api/me/connections/github"), Ct)).StatusCode);

        var connection = await ProjectApi.ReadAsync<GitHubConnectionDto>(await user.Client.GetAsync(U("/api/me/connections/github"), Ct));
        Assert.False(connection.Connected);
        await Problems.AssertAsync(await user.Client.GetAsync(U("/api/me/github/owners"), Ct), HttpStatusCode.Conflict, ErrorCodes.GitHubNotConnected);
    }

    [Fact]
    public async Task FR81_Concurrent_refreshes_spend_the_single_use_refresh_token_once_and_the_connection_stays_valid()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var user = await ApiSession.RegisterAsync(harness.App);
        await harness.SeedConnectionAsync(user.User!.Id, NewGitHubId(), "octo", accessExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1));
        harness.OnInstallations((UserInstallation, "octo", "User", "all"));
        var uses = 0;
        harness.Api.On(HttpMethod.Post, "/login/oauth/access_token", r =>
        {
            // GitHub refresh tokens are single-use: the second use is refused.
            if (!r.Body.Contains($"refresh_token={GitHubAppHarness.RefreshToken}", StringComparison.Ordinal) || Interlocked.Increment(ref uses) > 1)
            {
                return (HttpStatusCode.OK, """{"error":"bad_refresh_token"}""");
            }

            Thread.Sleep(300); // keep the second request waiting on the lock
            return (HttpStatusCode.OK, $$"""{"access_token":"{{GitHubAppHarness.RefreshedToken}}","expires_in":28800,"refresh_token":"ghr_next","refresh_token_expires_in":15811200}""");
        });

        var responses = await Task.WhenAll(
            user.Client.GetAsync(U("/api/me/connections/github"), Ct),
            user.Client.GetAsync(U("/api/me/connections/github"), Ct));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, harness.Api.Count(HttpMethod.Post, "/login/oauth/access_token"));
        var connection = await ProjectApi.WithDbAsync(harness.App, db => db.GitHubConnections.AsNoTracking().SingleAsync(c => c.UserId == user.User.Id, Ct));
        Assert.Equal(GitHubConnectionStatus.Valid, connection.Status);
        Assert.All(harness.Api.Requests.Where(r => r.Path == "/user/installations"), r => Assert.Equal($"Bearer {GitHubAppHarness.RefreshedToken}", r.Authorization));
    }

    [Fact]
    public async Task FR81_Declining_on_GitHub_during_connect_returns_to_the_page_with_access_denied()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var user = await ApiSession.RegisterAsync(harness.App);
        var start = await user.Client.GetAsync(U("/api/me/connections/github/connect?returnUrl=%2Faccount%2Fconnections"), Ct);
        var state = Uri.EscapeDataString(QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString());

        var callback = await user.Client.GetAsync(U($"/api/auth/github/callback?error=access_denied&state={state}"), Ct);

        Assert.Equal("/account/connections?error=GITHUB_AUTH_FAILED&reason=access_denied", callback.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task FR80_With_the_App_a_refused_email_list_means_no_email_and_a_linked_user_still_signs_in()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var linked = await ApiSession.RegisterAsync(harness.App);
        var githubId = NewGitHubId();
        await ProjectApi.WithDbAsync(harness.App, db => db.Users.Where(u => u.Id == linked.User!.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.GithubId, githubId.ToString(System.Globalization.CultureInfo.InvariantCulture)), Ct));
        harness.OnUser(githubId, "noemail", null);
        harness.OnInstallations();
        harness.Api.On(HttpMethod.Get, "/user/emails", HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}""");
        using var browser = new ApiSession(harness.App);

        var callback = await GitHubAppHarness.RunAppFlowAsync(browser, "/api/auth/github");

        Assert.Equal("/", callback.Headers.Location?.OriginalString);
        Assert.Equal(linked.User!.Id, (await browser.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct))!.Id);
    }

    // ---------------------------------------------------------------- FR-81 repo picker

    [Fact]
    public async Task FR81_The_picker_lists_the_App_repos_across_installations_with_search_and_pages()
    {
        await using var harness = await ConnectedAsync();
        harness.Harness.OnInstallations((UserInstallation, "octo", "User", "selected"), (OrgInstallation, "acme", "Organization", "all"));
        harness.Harness.Api.On(HttpMethod.Get, $"/user/installations/{UserInstallation}/repositories", HttpStatusCode.OK,
            $$"""{"total_count":1,"repositories":[{{GitHubAppHarness.RepoJson(11, "octo", "notes", "User", isPrivate: false, size: 0, push: false)}}]}""");
        var orgRepos = Enumerable.Range(1, 40).Select(n => GitHubAppHarness.RepoJson(100 + n, "acme", $"todo-{n:00}", size: 12));
        harness.Harness.Api.On(HttpMethod.Get, $"/user/installations/{OrgInstallation}/repositories", HttpStatusCode.OK,
            $$"""{"total_count":40,"repositories":[{{string.Join(',', orgRepos)}}]}""");

        var first = await ProjectApi.ReadAsync<GitHubRepoPageDto>(await harness.User.Client.GetAsync(U("/api/me/github/repos?search=TODO"), Ct));
        var second = await ProjectApi.ReadAsync<GitHubRepoPageDto>(await harness.User.Client.GetAsync(U("/api/me/github/repos?search=todo&page=2"), Ct));
        var notes = await ProjectApi.ReadAsync<GitHubRepoPageDto>(await harness.User.Client.GetAsync(U("/api/me/github/repos?search=notes"), Ct));

        Assert.Equal((30, true, 1), (first.Items.Count, first.HasMore, first.Page));
        Assert.Equal((10, false), (second.Items.Count, second.HasMore));
        Assert.Equal("acme/todo-01", first.Items[0].FullName);
        Assert.Equal(new GitHubRepoDto(101, "acme/todo-01", "acme", "todo-01", true, "main", OrgInstallation, null, "https://github.com/acme/todo-01", true), first.Items[0]);
        Assert.Equal(new GitHubRepoDto(11, "octo/notes", "octo", "notes", false, "main", UserInstallation, true, "https://github.com/octo/notes", false), Assert.Single(notes.Items));
    }

    [Fact]
    public async Task FR81_Owners_are_the_user_and_organisations_with_where_the_App_is_installed_and_creating_is_possible()
    {
        await using var harness = await ConnectedAsync();
        harness.Harness.OnInstallations((OrgInstallation, "acme", "Organization", "all"));
        harness.Harness.Api.On(HttpMethod.Get, "/user/orgs", HttpStatusCode.OK, """[{"login":"acme"},{"login":"globex"}]""");

        var owners = await ProjectApi.ReadAsync<List<GitHubOwnerDto>>(await harness.User.Client.GetAsync(U("/api/me/github/owners"), Ct));

        Assert.Equal(
            [
                new GitHubOwnerDto("octo", GitHubOwnerType.User, null, false, false),
                new GitHubOwnerDto("acme", GitHubOwnerType.Organization, OrgInstallation, true, true),
                new GitHubOwnerDto("globex", GitHubOwnerType.Organization, null, false, false),
            ],
            owners);
    }

    [Fact]
    public async Task FR86_Refs_report_the_default_branch_emptiness_releases_with_the_latest_and_semver_tags()
    {
        await using var harness = await ConnectedAsync();
        var api = harness.Harness.Api;
        api.On(HttpMethod.Get, "/repos/acme/shop", HttpStatusCode.OK, GitHubAppHarness.RepoJson(55, "acme", "shop", defaultBranch: "trunk", size: 300));
        harness.Harness.OnBranchRoot("acme", "shop", "trunk", new string('a', 40), "README.md", "src", "package.json");
        api.On(HttpMethod.Get, "/repos/acme/shop/releases", HttpStatusCode.OK,
            """[{"tag_name":"v2.3.0","name":"2.3","published_at":"2026-09-01T10:00:00Z","draft":false},{"tag_name":"v2.2.0","name":null,"published_at":"2026-08-01T10:00:00Z","draft":false},{"tag_name":"v9.9.9","draft":true}]""");
        api.On(HttpMethod.Get, "/repos/acme/shop/releases/latest", HttpStatusCode.OK, """{"tag_name":"v2.3.0"}""");
        api.On(HttpMethod.Get, "/repos/acme/shop/tags", HttpStatusCode.OK,
            """[{"name":"v2.3.0","commit":{"sha":"c230"}},{"name":"nightly","commit":{"sha":"c999"}}]""");

        var refs = await ProjectApi.ReadAsync<GitHubRepoRefsDto>(await harness.User.Client.GetAsync(U("/api/me/github/repos/acme/shop/refs"), Ct));

        Assert.Equal(("trunk", false), (refs.DefaultBranch, refs.Empty));
        Assert.Equal(
            [
                new GitHubReleaseDto("v2.3.0", "2.3", DateTimeOffset.Parse("2026-09-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), true),
                new GitHubReleaseDto("v2.2.0", null, DateTimeOffset.Parse("2026-08-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), false),
            ],
            refs.Releases);
        Assert.Equal([new GitHubTagDto("v2.3.0", "c230", "2.3.0"), new GitHubTagDto("nightly", "c999", null)], refs.Tags);
    }

    [Fact]
    public async Task FR86_Refs_of_a_repository_the_App_cant_reach_are_GITHUB_APP_NOT_INSTALLED_with_the_install_link()
    {
        await using var harness = await ConnectedAsync();

        var body = await Problems.AssertAsync(
            await harness.User.Client.GetAsync(U("/api/me/github/repos/acme/secret/refs"), Ct), HttpStatusCode.Conflict, ErrorCodes.GitHubAppNotInstalled);

        Assert.Equal("https://github.com/apps/aide-test/installations/new", body.GetProperty("installUrl").GetString());
    }

    // ---------------------------------------------------------------- FR-81 create repository

    [Fact]
    public async Task FR81_Given_a_connected_user_when_they_create_acme_todo_app_private_then_it_exists_with_main_and_the_App_can_access_it()
    {
        await using var harness = await ConnectedAsync();
        harness.Harness.OnInstallations((OrgInstallation, "acme", "Organization", "all"));
        harness.Harness.Api.On(HttpMethod.Post, "/orgs/acme/repos", HttpStatusCode.Created, GitHubAppHarness.RepoJson(9090, "acme", "todo-app"));
        harness.Harness.OnRepoInstallation("acme", "todo-app", OrgInstallation);

        var response = await harness.User.Client.PostAsJsonAsync("/api/me/github/repos", new { owner = "acme", name = "todo-app", description = "A todo app", @private = true }, Ct);

        var repo = await ProjectApi.ReadAsync<GitHubRepoDto>(response, HttpStatusCode.Created);
        Assert.Equal(new GitHubRepoDto(9090, "acme/todo-app", "acme", "todo-app", true, "main", OrgInstallation, true, "https://github.com/acme/todo-app", true), repo);
        var create = harness.Harness.Api.Requests.Single(r => r.Path == "/orgs/acme/repos");
        Assert.Equal($"Bearer {GitHubAppHarness.UserToken}", create.Authorization); // §4.14: the user token, not the App's
        using var body = JsonDocument.Parse(create.Body);
        Assert.Equal(("todo-app", true, true, "A todo app"), (
            body.RootElement.GetProperty("name").GetString(),
            body.RootElement.GetProperty("private").GetBoolean(),
            body.RootElement.GetProperty("auto_init").GetBoolean(),
            body.RootElement.GetProperty("description").GetString()));
    }

    [Fact]
    public async Task FR81_A_repository_for_the_user_is_created_with_POST_user_repos()
    {
        await using var harness = await ConnectedAsync();
        harness.Harness.OnInstallations((UserInstallation, "octo", "User", "all"));
        harness.Harness.Api.On(HttpMethod.Post, "/user/repos", HttpStatusCode.Created, GitHubAppHarness.RepoJson(9091, "octo", "diary", "User", isPrivate: false));
        harness.Harness.OnRepoInstallation("octo", "diary", UserInstallation);

        var repo = await ProjectApi.ReadAsync<GitHubRepoDto>(
            await harness.User.Client.PostAsJsonAsync("/api/me/github/repos", new { owner = "OCTO", name = "diary", @private = false }, Ct), HttpStatusCode.Created);

        Assert.Equal(("octo/diary", UserInstallation), (repo.FullName, repo.InstallationId));
    }

    [Fact]
    public async Task FR81_A_name_collision_is_REPO_NAME_TAKEN_409()
    {
        await using var harness = await ConnectedAsync();
        harness.Harness.OnInstallations((OrgInstallation, "acme", "Organization", "all"));
        harness.Harness.Api.On(HttpMethod.Post, "/orgs/acme/repos", HttpStatusCode.UnprocessableEntity,
            """{"message":"Repository creation failed.","errors":[{"resource":"Repository","code":"custom","field":"name","message":"name already exists on this account"}]}""");

        await Problems.AssertAsync(
            await harness.User.Client.PostAsJsonAsync("/api/me/github/repos", new { owner = "acme", name = "todo-app", @private = true }, Ct), HttpStatusCode.Conflict, ErrorCodes.RepoNameTaken);
    }

    [Fact]
    public async Task FR81_Creating_where_the_App_isnt_installed_is_GITHUB_APP_NOT_INSTALLED_and_creates_nothing()
    {
        await using var harness = await ConnectedAsync();
        harness.Harness.OnInstallations((OrgInstallation, "acme", "Organization", "all"));

        var body = await Problems.AssertAsync(
            await harness.User.Client.PostAsJsonAsync("/api/me/github/repos", new { owner = "globex", name = "todo-app", @private = true }, Ct), HttpStatusCode.Conflict, ErrorCodes.GitHubAppNotInstalled);

        Assert.Equal("https://github.com/apps/aide-test/installations/new", body.GetProperty("installUrl").GetString());
        Assert.DoesNotContain(harness.Harness.Api.Requests, r => r.Method == "POST" && r.Path.EndsWith("/repos", StringComparison.Ordinal));
    }

    [Fact]
    public async Task S4_14_A_new_repository_outside_a_selected_repositories_installation_is_GITHUB_APP_NOT_INSTALLED()
    {
        await using var harness = await ConnectedAsync();
        harness.Harness.OnInstallations((OrgInstallation, "acme", "Organization", "selected"));
        harness.Harness.Api.On(HttpMethod.Post, "/orgs/acme/repos", HttpStatusCode.Created, GitHubAppHarness.RepoJson(9092, "acme", "todo-app"));

        await Problems.AssertAsync(
            await harness.User.Client.PostAsJsonAsync("/api/me/github/repos", new { owner = "acme", name = "todo-app", @private = true }, Ct), HttpStatusCode.Conflict, ErrorCodes.GitHubAppNotInstalled);
    }

    [Fact]
    public async Task FR81_Without_the_Administration_permission_creating_is_FORBIDDEN()
    {
        await using var harness = await ConnectedAsync();
        harness.Harness.Api.On(HttpMethod.Get, "/app", HttpStatusCode.OK, GitHubAppHarness.AppJson(administration: false));
        harness.Harness.OnInstallations((OrgInstallation, "acme", "Organization", "all"));

        await Problems.AssertAsync(
            await harness.User.Client.PostAsJsonAsync("/api/me/github/repos", new { owner = "acme", name = "todo-app", @private = true }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        var owners = await ProjectApi.ReadAsync<List<GitHubOwnerDto>>(await harness.User.Client.GetAsync(U("/api/me/github/owners"), Ct));
        Assert.All(owners, o => Assert.False(o.CanCreate));
    }

    [Fact]
    public async Task S4_14_A_GitHub_rate_limit_is_429_GITHUB_RATE_LIMITED_with_Retry_After_until_the_reset()
    {
        await using var harness = await ConnectedAsync();
        var reset = DateTimeOffset.UtcNow.AddSeconds(120).ToUnixTimeSeconds();
        harness.Harness.Api.On(HttpMethod.Get, "/user/installations", HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""",
            new Dictionary<string, string> { ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = reset.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        var response = await harness.User.Client.GetAsync(U("/api/me/github/repos"), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.TooManyRequests, ErrorCodes.GitHubRateLimited);
        var retryAfter = int.Parse(Assert.Single(response.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 100, 121);
    }

    // ---------------------------------------------------------------- FR-82, FR-86 github_app projects

    [Fact]
    public async Task FR82_With_an_App_the_default_git_mode_is_github_app()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var owner = await ApiSession.RegisterAsync(harness.App);

        var response = await PostProjectAsync(owner, """{"defaultBranch":"main"}""");

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(body.GetProperty("errors").TryGetProperty("git.installationId", out _), body.ToString());
    }

    [Fact]
    public async Task FR82_A_github_app_project_stores_the_installation_repo_id_and_full_name_and_no_token()
    {
        await using var harness = await ConnectedAsync();
        harness.Harness.OnRepoInstallation("acme", "todo-app", OrgInstallation);
        harness.Harness.Api.On(HttpMethod.Get, "/repos/acme/todo-app", HttpStatusCode.OK, GitHubAppHarness.RepoJson(9090, "acme", "todo-app"));
        harness.Harness.OnBranchRoot("acme", "todo-app", "main", new string('b', 40), "README.md");

        var project = await ProjectApi.ReadAsync<ProjectDto>(
            await PostProjectAsync(harness.User, $$"""{"mode":"github_app","installationId":{{OrgInstallation}},"repoFullName":"acme/todo-app"}"""), HttpStatusCode.Created);

        var git = (await ProjectApi.ReadAsync<ProjectSettingsDto>(await harness.User.Client.GetAsync(U($"/api/projects/{project.Id}/settings"), Ct))).Git;
        Assert.Equal((GitProvider.GithubApp, GitProvider.GithubApp, (long?)OrgInstallation, (long?)9090, "acme/todo-app", false), (git.Mode, git.Provider, git.InstallationId, git.RepoId, git.RepoFullName, git.HasToken));
        Assert.Equal("main", git.DefaultBranch);

        // A README-only repository (GitHub's auto_init) is "start from scratch": nothing to build on.
        var version = await ProjectApi.WithDbAsync(harness.Harness.App, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == project.Id, Ct));
        Assert.Null(version.BaseCommitSha);
    }

    [Fact]
    public async Task FR86_Given_a_non_empty_repo_in_start_from_scratch_then_the_project_builds_on_its_head_never_released()
    {
        await using var harness = await ConnectedAsync();
        var head = new string('c', 40);
        harness.Harness.OnRepoInstallation("acme", "legacy", OrgInstallation);
        harness.Harness.Api.On(HttpMethod.Get, "/repos/acme/legacy", HttpStatusCode.OK, GitHubAppHarness.RepoJson(9093, "acme", "legacy", defaultBranch: "develop", size: 900));
        harness.Harness.OnBranchRoot("acme", "legacy", "develop", head, "README.md", "src", "package.json");

        var project = await ProjectApi.ReadAsync<ProjectDto>(
            await PostProjectAsync(harness.User, $$"""{"mode":"github_app","installationId":{{OrgInstallation}},"repoFullName":"acme/legacy"}"""), HttpStatusCode.Created);

        var version = await ProjectApi.WithDbAsync(harness.Harness.App, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == project.Id, Ct));
        Assert.Equal((head, (string?)null, ReleaseVersionStatus.InDevelopment), (version.BaseCommitSha, version.BaseTag, version.Status));
        Assert.Null(project.ReleasedVersion);
        var created = Assert.Single(await LifecycleSeed.EventsAsync(harness.Harness.App, project.Id, EventTypes.ProjectCreated));
        Assert.True(LifecycleSeed.Payload(created.Payload).GetProperty("repoHasCode").GetBoolean());
        var git = (await ProjectApi.ReadAsync<ProjectSettingsDto>(await harness.User.Client.GetAsync(U($"/api/projects/{project.Id}/settings"), Ct))).Git;
        Assert.Equal("develop", git.DefaultBranch); // the repository's own default branch
    }

    [Fact]
    public async Task FR82_A_repo_the_App_cant_access_for_the_user_is_GITHUB_APP_NOT_INSTALLED_and_nothing_is_saved()
    {
        await using var harness = await ConnectedAsync();
        harness.Harness.OnRepoInstallation("acme", "todo-app", 999); // another installation than the one sent
        var name = ProjectApi.UniqueName("app");

        var body = await Problems.AssertAsync(
            await PostProjectAsync(harness.User, $$"""{"mode":"github_app","installationId":{{OrgInstallation}},"repoFullName":"acme/todo-app"}""", name),
            HttpStatusCode.Conflict,
            ErrorCodes.GitHubAppNotInstalled);

        Assert.Equal("https://github.com/apps/aide-test/installations/new", body.GetProperty("installUrl").GetString());
        Assert.False(await ProjectApi.WithDbAsync(harness.Harness.App, db => db.Projects.AnyAsync(p => p.Name == name, Ct)));
    }

    [Fact]
    public async Task FR82_Creating_a_github_app_project_without_a_GitHub_connection_is_GITHUB_NOT_CONNECTED()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var owner = await ApiSession.RegisterAsync(harness.App);

        await Problems.AssertAsync(
            await PostProjectAsync(owner, $$"""{"mode":"github_app","installationId":{{OrgInstallation}},"repoFullName":"acme/todo-app"}"""), HttpStatusCode.Conflict, ErrorCodes.GitHubNotConnected);
    }

    // ---------------------------------------------------------------- FR-83 PAT fallback and switch

    [Fact]
    public async Task FR83_Given_a_PAT_project_on_a_repo_with_the_App_then_settings_offer_Switch_to_GitHub_App_and_switching_drops_the_PAT()
    {
        const string token = "ghp_switchMeToTheApp0123456789";
        await using var harness = GitHubAppHarness.Create(factory);
        using var owner = await ApiSession.RegisterAsync(harness.App);
        await harness.SeedConnectionAsync(owner.User!.Id, NewGitHubId(), "octo");
        var project = await ProjectApi.CreateAsync(owner.Client, git: new { mode = "github_pat", repoUrl = "https://github.com/acme/widgets", token });
        harness.OnRepoInstallation("acme", "widgets", OrgInstallation);
        harness.Api.On(HttpMethod.Get, "/repos/acme/widgets", r => r.Authorization == $"Bearer {GitHubAppHarness.UserToken}"
            ? (HttpStatusCode.OK, GitHubAppHarness.RepoJson(31337, "acme", "widgets", push: true))
            : (HttpStatusCode.NotFound, """{"message":"Not Found"}"""));

        var before = (await ProjectApi.ReadAsync<ProjectSettingsDto>(await owner.Client.GetAsync(U($"/api/projects/{project.Id}/settings"), Ct))).Git;
        Assert.Equal((GitProvider.GithubPat, true, (bool?)true, true), (before.Mode, before.HasToken, before.AppInstalledOnRepo, before.CanSwitchToApp));

        var switched = await ProjectApi.ReadAsync<GitSettingsDto>(await owner.Client.PostAsync(U($"/api/projects/{project.Id}/settings/git/switch-to-app"), null, Ct));

        Assert.Equal((GitProvider.GithubApp, (long?)OrgInstallation, (long?)31337, "acme/widgets", false), (switched.Mode, switched.InstallationId, switched.RepoId, switched.RepoFullName, switched.HasToken));
        var stored = await ProjectApi.WithDbAsync(harness.App, db => db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct));
        Assert.Null(stored.TokenEncrypted);
        Assert.Equal(GitProvider.GithubApp, stored.Provider);

        // §8: no installation token was minted to switch (none unscoped, none at all).
        Assert.DoesNotContain(harness.Api.Requests, r => r.Path.EndsWith("/access_tokens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR83_A_PAT_project_for_someone_elses_repo_cant_be_switched_to_the_App_FORBIDDEN_and_nothing_changes()
    {
        // The attack: a github_pat project for victim-org/repo with no token, then "Switch to GitHub App" to borrow the
        // App's installation (installed there by the victim) for writes.
        await using var harness = GitHubAppHarness.Create(factory);
        using var attacker = await ApiSession.RegisterAsync(harness.App);
        var project = await ProjectApi.CreateAsync(attacker.Client, git: new { mode = "github_pat", repoUrl = "https://github.com/victim-org/repo" });
        harness.OnRepoInstallation("victim-org", "repo", 5005);
        harness.OnInstallationToken(5005);
        var path = U($"/api/projects/{project.Id}/settings/git/switch-to-app");

        // Without a GitHub connection: GITHUB_NOT_CONNECTED.
        await Problems.AssertAsync(await attacker.Client.PostAsync(path, null, Ct), HttpStatusCode.Conflict, ErrorCodes.GitHubNotConnected);

        // Connected, but the repo isn't visible to them (404), or only readable (no push): FORBIDDEN.
        await harness.SeedConnectionAsync(attacker.User!.Id, NewGitHubId(), "mallory");
        await Problems.AssertAsync(await attacker.Client.PostAsync(path, null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        harness.Api.On(HttpMethod.Get, "/repos/victim-org/repo", HttpStatusCode.OK, GitHubAppHarness.RepoJson(4040, "victim-org", "repo", isPrivate: false, push: false));
        await Problems.AssertAsync(await attacker.Client.PostAsync(path, null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        var stored = await ProjectApi.WithDbAsync(harness.App, db => db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct));
        Assert.Equal((GitProvider.GithubPat, (long?)null, (long?)null, (string?)null), (stored.Provider, stored.InstallationId, stored.RepoId, stored.RepoFullName));
        Assert.DoesNotContain(harness.Api.Requests, r => r.Path.EndsWith("/access_tokens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR82_A_github_app_project_on_a_repo_the_caller_can_only_read_is_FORBIDDEN_and_nothing_is_saved()
    {
        await using var harness = await ConnectedAsync();
        harness.Harness.OnRepoInstallation("acme", "readonly", OrgInstallation);
        harness.Harness.Api.On(HttpMethod.Get, "/repos/acme/readonly", HttpStatusCode.OK, GitHubAppHarness.RepoJson(9094, "acme", "readonly", push: false));
        var name = ProjectApi.UniqueName("ro");

        await Problems.AssertAsync(
            await PostProjectAsync(harness.User, $$"""{"mode":"github_app","installationId":{{OrgInstallation}},"repoFullName":"acme/readonly"}""", name),
            HttpStatusCode.Forbidden,
            ErrorCodes.Forbidden);

        Assert.False(await ProjectApi.WithDbAsync(harness.Harness.App, db => db.Projects.AnyAsync(p => p.Name == name, Ct)));
    }

    [Fact]
    public async Task FR83_Switching_a_repo_without_the_App_is_GITHUB_APP_NOT_INSTALLED_with_the_install_link()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var owner = await ApiSession.RegisterAsync(harness.App);
        await harness.SeedConnectionAsync(owner.User!.Id, NewGitHubId(), "octo");
        var project = await ProjectApi.CreateAsync(owner.Client, git: new { mode = "github_pat", repoUrl = "https://github.com/acme/private", token = "ghp_x2" });

        var git = (await ProjectApi.ReadAsync<ProjectSettingsDto>(await owner.Client.GetAsync(U($"/api/projects/{project.Id}/settings"), Ct))).Git;
        Assert.Equal(((bool?)false, false), (git.AppInstalledOnRepo, git.CanSwitchToApp));

        var body = await Problems.AssertAsync(
            await owner.Client.PostAsync(U($"/api/projects/{project.Id}/settings/git/switch-to-app"), null, Ct), HttpStatusCode.Conflict, ErrorCodes.GitHubAppNotInstalled);
        Assert.Equal("https://github.com/apps/aide-test/installations/new", body.GetProperty("installUrl").GetString());
        Assert.Equal(GitProvider.GithubPat, (await ProjectApi.WithDbAsync(harness.App, db => db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct))).Provider);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<ConnectedUser> ConnectedAsync()
    {
        var harness = GitHubAppHarness.Create(factory);
        var user = await ApiSession.RegisterAsync(harness.App);
        await harness.SeedConnectionAsync(user.User!.Id, NewGitHubId(), "octo");
        return new ConnectedUser(harness, user);
    }

    private static Task<HttpResponseMessage> PostProjectAsync(ApiSession owner, string gitJson, string? name = null)
    {
        var body = $$"""{"name":"{{name ?? ProjectApi.UniqueName()}}","idea":"Build a todo API.","git":{{gitJson}}}""";
        return owner.Client.PostAsync(U("/api/projects"), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);
    }

    private sealed record ConnectedUser(GitHubAppHarness Harness, ApiSession User) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            User.Dispose();
            await Harness.DisposeAsync();
        }
    }
}
