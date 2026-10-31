using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Databases;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Databases;

/// <summary>
/// FR-152, FR-153, FR-145: managed databases through <c>PUT …/settings/deployment/databases/:environment</c> and the
/// <c>database_provisioning</c> job, on the Fake database provider (<c>Deployments:Provider=Fake</c>) and External (no
/// platform): cards, derived env vars, refusals on the card, timeout, unlinking and the Owner rules.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ManagedDatabaseTests(AideApiFactory factory)
{
    private static readonly object PatGit = new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_managedDatabaseToken" };

    /// <summary>FR-145 example: a Vue SPA with Supabase BaaS on Vercel.</summary>
    private static readonly object VueSupabaseStack = new
    {
        frontend = new { pack = "fe-vue", path = "." },
        backend = new { pack = "none" },
        data = new { pack = "data-supabase" },
        deploy = new { frontendHost = "deploy-vercel", databaseHost = "supabase", local = "none" },
    };

    /// <summary>A React SPA with an ASP.NET Core backend on Render and an External Postgres.</summary>
    private static readonly object ExternalStack = new
    {
        frontend = new { pack = "fe-react-vite", path = "frontend" },
        backend = new { pack = "be-aspnet", path = "backend" },
        data = new { pack = "data-efcore-postgres" },
        deploy = new { frontendHost = "deploy-vercel", backendHost = "deploy-render", databaseHost = "external", local = "none" },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string DatabasePath(Guid projectId, string environment) => $"{DeploymentHarness.SettingsPath(projectId)}/databases/{environment}";

    private async Task<(WebApplicationFactory<Program> App, ApiSession Owner, Guid ProjectId)> ProjectAsync(object stack)
    {
        var app = DeploymentHarness.Fake(factory);
        var owner = await ApiSession.RegisterAsync(app);
        var id = (await ProjectApi.CreateAsync(owner.Client, git: PatGit)).Id;
        await DeploymentHarness.SetStackAsync(owner.Client, id, stack);
        return (app, owner, id);
    }

    private static async Task<DeploymentDatabaseDto> PutAsync(HttpClient client, Guid projectId, string environment, object body) =>
        await ProjectApi.ReadAsync<DeploymentDatabaseDto>(await client.PutAsJsonAsync(DatabasePath(projectId, environment), body, Ct), HttpStatusCode.Accepted);

    private static Task<Job> ProvisioningJobAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.DatabaseProvisioning)
            .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
            .FirstAsync(Ct));

    private static async Task<DeploymentDatabaseDto> CardAsync(HttpClient client, Guid projectId, DeploymentEnvironment environment) =>
        (await DeploymentHarness.SettingsAsync(client, projectId)).Databases.Single(d => d.Environment == environment);

    [Fact]
    public async Task FR152_FR145_Provisioning_a_Supabase_project_polls_until_available_and_derives_the_public_VITE_vars()
    {
        var (app, owner, id) = await ProjectAsync(VueSupabaseStack);
        using var _ = owner;

        var queued = await PutAsync(owner.Client, id, "production", new { provider = "supabase", create = new { region = "eu-west-2" } });
        Assert.Equal(("supabase", "provisioning", "queued"), (queued.Provider, queued.Status, queued.Provisioning?.Step));

        var runs = await DeploymentHarness.SettleAsync(app, (await ProvisioningJobAsync(app, id)).Id);

        Assert.Equal(3, runs);
        var settings = await DeploymentHarness.SettingsAsync(owner.Client, id);
        var card = settings.Databases.Single(d => d.Environment == DeploymentEnvironment.Production);
        Assert.Equal(("available", "succeeded"), (card.Status, card.Provisioning?.State));
        Assert.Contains("(session pooler)", card.ConnectionSummary, StringComparison.Ordinal);
        Assert.Contains(ManagedDatabaseService.WarningSupabaseFreePausing, card.Warnings);
        Assert.Contains(ManagedDatabaseService.WarningPreviewUsesProduction, settings.Databases.Single(d => d.Environment == DeploymentEnvironment.Preview).Warnings);

        var vars = settings.Env.Variables.ToDictionary(v => v.Key);
        Assert.Equal((DeploymentEnvSource.Derived, true, true), (vars["VITE_SUPABASE_URL"].Source, vars["VITE_SUPABASE_URL"].Public, vars["VITE_SUPABASE_URL"].HasValue));
        Assert.Equal([DeploymentEnvironment.Preview, DeploymentEnvironment.Production], vars["VITE_SUPABASE_PUBLISHABLE_KEY"].Environments);
        Assert.DoesNotContain(vars.Keys, k => k.Contains("SECRET", StringComparison.Ordinal));
        var stored = await ProjectApi.WithDbAsync(app, db => db.DeploymentEnvVars.AsNoTracking().SingleAsync(v => v.ProjectId == id && v.Key == "VITE_SUPABASE_URL", Ct));
        Assert.Equal(DeploymentEnvSource.Derived, stored.Source);
    }

    [Fact]
    public async Task FR152_An_External_connection_string_is_stored_encrypted_never_returned_and_becomes_DATABASE_URL()
    {
        var (app, owner, id) = await ProjectAsync(ExternalStack);
        using var _ = owner;
        const string Url = "postgresql://app:sup3r-s3cret@db.example.com:5432/app";

        var queued = await PutAsync(owner.Client, id, "production", new { provider = "external", external = new { connectionString = Url } });
        await DeploymentHarness.SettleAsync(app, (await ProvisioningJobAsync(app, id)).Id);

        var response = await owner.Client.GetStringAsync(new Uri(DeploymentHarness.SettingsPath(id), UriKind.Relative), Ct);
        Assert.DoesNotContain("sup3r-s3cret", response, StringComparison.Ordinal);
        var card = await CardAsync(owner.Client, id, DeploymentEnvironment.Production);
        Assert.Equal(("external", "available", "db.example.com:5432/app"), (card.Provider, card.Status, card.ConnectionSummary));
        Assert.Equal("provisioning", queued.Status);
        var row = await ProjectApi.WithDbAsync(app, db => db.ManagedDatabases.AsNoTracking().SingleAsync(d => d.ProjectId == id, Ct));
        Assert.DoesNotContain("sup3r-s3cret", row.ConnectionEncrypted, StringComparison.Ordinal);
        var derived = (await DeploymentHarness.SettingsAsync(owner.Client, id)).Env.Variables.Single(v => v.Key == "DATABASE_URL");
        Assert.Equal((DeploymentEnvSource.Derived, true, DeploymentComponent.Backend), (derived.Source, derived.Sensitive, Assert.Single(derived.AppliesTo)));
    }

    [Fact]
    public async Task FR152_Provisioning_that_never_becomes_available_times_out_after_10_minutes()
    {
        var (app, owner, id) = await ProjectAsync(VueSupabaseStack);
        using var _ = owner;
        await PutAsync(owner.Client, id, "production", new { provider = "supabase", link = new { resourceId = "never-ready" } });
        var job = await ProvisioningJobAsync(app, id);
        Assert.True(await DeploymentHarness.RunOnceAsync(app, job.Id));
        Assert.True(await DeploymentHarness.RunOnceAsync(app, job.Id));

        // Move the start of provisioning back 11 minutes.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Aide.Infrastructure.Persistence.AideDbContext>();
            var secrets = scope.ServiceProvider.GetRequiredService<ManagedDatabaseSecrets>();
            var row = await db.ManagedDatabases.SingleAsync(d => d.ProjectId == id, Ct);
            secrets.SetConnection(row, secrets.Connection(row) with { ProvisioningStartedAt = DateTimeOffset.UtcNow.AddMinutes(-11) });
            await db.SaveChangesAsync(Ct);
        }

        Assert.False(await DeploymentHarness.RunOnceAsync(app, job.Id));

        var card = await CardAsync(owner.Client, id, DeploymentEnvironment.Production);
        Assert.Equal(("error", "failed", ErrorCodes.DatabaseProvisioningTimeout), (card.Status, card.Provisioning?.State, card.Provisioning?.ErrorCode));
        Assert.Equal(JobStatus.Failed, (await ProvisioningJobAsync(app, id)).Status);
    }

    [Fact]
    public async Task FR153_A_Supabase_token_without_API_Key_Secrets_Read_fails_with_the_missing_permission_on_the_card()
    {
        var (app, owner, id) = await ProjectAsync(VueSupabaseStack);
        using var _ = owner;
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "fake_scope_supabase01", provider: PlatformProvider.Supabase);

        await PutAsync(owner.Client, id, "production", new { provider = "supabase", connectionId = connection, create = new { region = "emea" } });
        await DeploymentHarness.SettleAsync(app, (await ProvisioningJobAsync(app, id)).Id);

        var card = await CardAsync(owner.Client, id, DeploymentEnvironment.Production);
        Assert.Equal(("error", ErrorCodes.ConnectionScopeInsufficient), (card.Status, card.Provisioning?.ErrorCode));
        Assert.Contains("API Key Secrets: Read", card.MissingPermissions);
        Assert.Equal(PlatformConnectionStatus.InsufficientScope, card.Binding?.Status);
        Assert.Equal(owner.User.Id, card.Binding?.BoundBy.Id);
    }

    [Fact]
    public async Task FR152_The_Supabase_free_project_limit_is_PLATFORM_PLAN_LIMIT_with_the_link_instead_warning()
    {
        var (app, owner, id) = await ProjectAsync(VueSupabaseStack);
        using var _ = owner;
        var connection = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "fake_limit_supabase01", provider: PlatformProvider.Supabase);

        await PutAsync(owner.Client, id, "production", new { provider = "supabase", connectionId = connection, create = new { region = "emea" } });
        await DeploymentHarness.SettleAsync(app, (await ProvisioningJobAsync(app, id)).Id);

        var card = await CardAsync(owner.Client, id, DeploymentEnvironment.Production);
        Assert.Equal(ErrorCodes.PlatformPlanLimit, card.Provisioning?.ErrorCode);
        Assert.Contains(ManagedDatabaseService.WarningSupabaseProjectLimit, card.Warnings);
    }

    [Fact]
    public async Task FR152_PUT_refusals_STACK_INCOMPATIBLE_CREDENTIAL_NOT_OWNER_FORBIDDEN_and_VALIDATION()
    {
        var app = DeploymentHarness.Fake(factory);
        using var fixture = await ProjectApi.FixtureAsync(app, git: PatGit);
        var id = fixture.Project.Id;
        await DeploymentHarness.SetStackAsync(fixture.Owner.Client, id, VueSupabaseStack);
        var someoneElses = await DeploymentHarness.SeedConnectionAsync(app, fixture.Viewer.User!.Id, token: "sbp_someoneElse0001", provider: PlatformProvider.Supabase);

        await Problems.AssertAsync(
            await fixture.Owner.Client.PutAsJsonAsync(DatabasePath(id, "production"), new { provider = "render", create = new { plan = "free" } }, Ct), HttpStatusCode.BadRequest, ErrorCodes.StackIncompatible);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PutAsJsonAsync(DatabasePath(id, "production"), new { provider = "supabase", connectionId = someoneElses, link = new { resourceId = "abcd" } }, Ct), HttpStatusCode.Forbidden, ErrorCodes.CredentialNotOwner);
        await Problems.AssertAsync(
            await fixture.Viewer.Client.PutAsJsonAsync(DatabasePath(id, "production"), new { provider = "supabase", link = new { resourceId = "abcd" } }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PutAsJsonAsync(DatabasePath(id, "staging"), new { provider = "supabase", link = new { resourceId = "abcd" } }, Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PutAsJsonAsync(DatabasePath(id, "production"), new { provider = "supabase", link = new { resourceId = "abcd" }, create = new { region = "emea" } }, Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PutAsJsonAsync(DatabasePath(Guid.NewGuid(), "production"), new { provider = "supabase", link = new { resourceId = "abcd" } }, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        using var anonymous = new ApiSession(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Client.PutAsJsonAsync(DatabasePath(id, "production"), new { provider = "supabase" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task FR152_DELETE_unlinks_the_database_and_removes_its_derived_vars_and_restore_needs_a_paused_one()
    {
        var (app, owner, id) = await ProjectAsync(VueSupabaseStack);
        using var _ = owner;
        await PutAsync(owner.Client, id, "production", new { provider = "supabase", create = new { region = "eu-west-2" } });
        await DeploymentHarness.SettleAsync(app, (await ProvisioningJobAsync(app, id)).Id);

        await Problems.AssertAsync(
            await owner.Client.PostAsync(new Uri($"{DatabasePath(id, "production")}/restore", UriKind.Relative), null, Ct), HttpStatusCode.Conflict, ErrorCodes.Conflict);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync(new Uri(DatabasePath(id, "production"), UriKind.Relative), Ct)).StatusCode);

        var settings = await DeploymentHarness.SettingsAsync(owner.Client, id);
        Assert.Equal("not_configured", settings.Databases.Single(d => d.Environment == DeploymentEnvironment.Production).Status);
        Assert.DoesNotContain(settings.Env.Variables, v => v.Source == DeploymentEnvSource.Derived && v.Key.Contains("SUPABASE", StringComparison.Ordinal));
        await Problems.AssertAsync(await owner.Client.DeleteAsync(new Uri(DatabasePath(id, "production"), UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }
}
