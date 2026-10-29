using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Auth;
using Aide.Api.Features.Bitbucket;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Security;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Bitbucket;

/// <summary>
/// FR-133: Admin → Integrations → Bitbucket: the OAuth consumer saved encrypted (secret as a flag), the registration help,
/// configuration precedence (<c>Bitbucket__OAuth__*</c>, CONFLICT here), and <c>/api/auth/providers</c>.<c>bitbucket</c>.
/// The Bitbucket Cloud row is shared by the collection, so each test sets the consumer state it needs.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class BitbucketAdminTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR133_An_Admin_saves_the_consumer_encrypted_and_sees_the_callback_and_permissions_never_the_secret()
    {
        var app = BitbucketHarness.App(factory, new FakeGitHubApi());
        await BitbucketHarness.SetDatabaseConsumerAsync(app, null, null);
        using var admin = await ApiSession.AdminAsync(app);

        var response = await admin.Client.PutAsJsonAsync("/api/admin/integrations/bitbucket", new { clientId = BitbucketHarness.ClientId, clientSecret = BitbucketHarness.ClientSecret }, Ct);

        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(BitbucketHarness.ClientSecret, text, StringComparison.Ordinal);
        var dto = System.Text.Json.JsonSerializer.Deserialize<AdminBitbucketIntegrationDto>(text, ApiSession.Json)!;
        Assert.Equal((BitbucketHarness.CallbackUrl, GitHostInstanceSource.Database, BitbucketHarness.ClientId, true, true),
            (dto.CallbackUrl, dto.Source, dto.ClientId, dto.HasClientSecret, dto.OauthConfigured));
        Assert.Equal(["account", "email", "repository:write", "repository:admin", "pullrequest:write", "project", "webhook"], dto.Permissions);

        var row = await ProjectApi.WithDbAsync(app, db => db.GitHostInstances.AsNoTracking().SingleAsync(i => i.Kind == GitHostKind.Bitbucket, Ct));
        Assert.Null(row.BaseUrl);
        Assert.DoesNotContain(BitbucketHarness.ClientSecret, row.OauthClientSecretEncrypted, StringComparison.Ordinal);
        Assert.Equal(BitbucketHarness.ClientSecret, BitbucketHarness.Decrypt(app, row.OauthClientSecretEncrypted, SecretPurposes.GitHostClientSecret));
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(BitbucketHarness.ClientSecret, StringComparison.Ordinal));

        using var anonymous = new ApiSession(app);
        var providers = await ProjectApi.ReadAsync<AuthProvidersDto>(await anonymous.Client.GetAsync(new Uri("/api/auth/providers", UriKind.Relative), Ct));
        Assert.True(providers.Bitbucket);

        // An omitted secret keeps the saved one.
        var renamed = await ProjectApi.ReadAsync<AdminBitbucketIntegrationDto>(
            await admin.Client.PutAsJsonAsync("/api/admin/integrations/bitbucket", new { clientId = "new-key" }, Ct));
        Assert.Equal(("new-key", true), (renamed.ClientId, renamed.HasClientSecret));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteAsync(new Uri("/api/admin/integrations/bitbucket", UriKind.Relative), Ct)).StatusCode);
        var removed = await ProjectApi.ReadAsync<AdminBitbucketIntegrationDto>(await admin.Client.GetAsync(new Uri("/api/admin/integrations/bitbucket", UriKind.Relative), Ct));
        Assert.Equal((null, false, false), (removed.ClientId, removed.HasClientSecret, removed.OauthConfigured));
        providers = await ProjectApi.ReadAsync<AuthProvidersDto>(await anonymous.Client.GetAsync(new Uri("/api/auth/providers", UriKind.Relative), Ct));
        Assert.False(providers.Bitbucket);
    }

    [Fact]
    public async Task FR133_A_configured_consumer_takes_precedence_and_can_t_be_changed_here()
    {
        var app = BitbucketHarness.App(factory, new FakeGitHubApi(), configuredConsumer: true);
        using var admin = await ApiSession.AdminAsync(app);

        var overview = await ProjectApi.ReadAsync<AdminBitbucketIntegrationDto>(await admin.Client.GetAsync(new Uri("/api/admin/integrations/bitbucket", UriKind.Relative), Ct));
        var put = await admin.Client.PutAsJsonAsync("/api/admin/integrations/bitbucket", new { clientId = "other", clientSecret = "other-secret" }, Ct);
        var delete = await admin.Client.DeleteAsync(new Uri("/api/admin/integrations/bitbucket", UriKind.Relative), Ct);

        Assert.Equal((GitHostInstanceSource.Configuration, BitbucketHarness.ClientId, true), (overview.Source, overview.ClientId, overview.OauthConfigured));
        await Problems.AssertAsync(put, HttpStatusCode.Conflict, ErrorCodes.Conflict);
        await Problems.AssertAsync(delete, HttpStatusCode.Conflict, ErrorCodes.Conflict);
    }

    [Fact]
    public async Task FR133_The_consumer_is_Admin_only_and_needs_a_key()
    {
        var app = BitbucketHarness.App(factory, new FakeGitHubApi());
        await BitbucketHarness.SetDatabaseConsumerAsync(app, null, null);
        using var user = await ApiSession.RegisterAsync(app);
        using var admin = await ApiSession.AdminAsync(app);
        using var anonymous = new ApiSession(app);

        await Problems.AssertAsync(await anonymous.Client.GetAsync(new Uri("/api/admin/integrations/bitbucket", UriKind.Relative), Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        await Problems.AssertAsync(await user.Client.GetAsync(new Uri("/api/admin/integrations/bitbucket", UriKind.Relative), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(
            await user.Client.PutAsJsonAsync("/api/admin/integrations/bitbucket", new { clientId = "k", clientSecret = "s" }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        var noSecret = await admin.Client.PutAsJsonAsync("/api/admin/integrations/bitbucket", new { clientId = "key-only" }, Ct);
        var problem = await Problems.AssertAsync(noSecret, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(problem.GetProperty("errors").TryGetProperty("clientSecret", out _));
    }

    [Fact]
    public async Task FR134_Sign_in_start_is_404_without_a_consumer()
    {
        var app = BitbucketHarness.App(factory, new FakeGitHubApi());
        await BitbucketHarness.SetDatabaseConsumerAsync(app, null, null);
        using var browser = new ApiSession(app);

        var response = await browser.Client.GetAsync(new Uri("/api/auth/bitbucket/start?mode=signin", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR134_A_database_consumer_enables_sign_in()
    {
        var app = BitbucketHarness.App(factory, new FakeGitHubApi());
        await BitbucketHarness.SetDatabaseConsumerAsync(app, BitbucketHarness.ClientId, BitbucketHarness.ClientSecret);
        try
        {
            using var browser = new ApiSession(app);
            var authorize = await BitbucketHarness.StartAsync(browser);
            Assert.Equal(BitbucketHarness.ClientId, authorize["client_id"]);
        }
        finally
        {
            await BitbucketHarness.SetDatabaseConsumerAsync(app, null, null);
        }

        Assert.True(await ProjectApi.WithDbAsync(app, db => db.GitHostInstances.AnyAsync(i => i.Kind == GitHostKind.Bitbucket && i.OauthClientId == string.Empty, Ct)));
    }
}
