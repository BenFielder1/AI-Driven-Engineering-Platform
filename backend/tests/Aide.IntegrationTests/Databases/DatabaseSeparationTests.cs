using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Databases;
using Aide.Api.Jobs;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Databases;

/// <summary>
/// §4.21, FR-148, FR-153: the preview database is never production's. Linking (either environment) refuses the other
/// environment's resource or connection with <c>VALIDATION</c>; a preview row that is production's anyway (written behind
/// the API) is never reset (endpoint and job) and never migrated (a failed <c>VALIDATION</c> run that ran nothing).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class DatabaseSeparationTests(AideApiFactory factory)
{
    private static readonly object PatGit = new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_separationToken0001" };

    private static readonly object VueSupabaseStack = new
    {
        frontend = new { pack = "fe-vue", path = "." },
        backend = new { pack = "none" },
        data = new { pack = "data-supabase" },
        deploy = new { frontendHost = "deploy-vercel", databaseHost = "supabase", local = "none" },
    };

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

    private static async Task SetUpAsync(WebApplicationFactory<Program> app, HttpClient client, Guid projectId, string environment, object body)
    {
        await ProjectApi.ReadAsync<DeploymentDatabaseDto>(await client.PutAsJsonAsync(DatabasePath(projectId, environment), body, Ct), HttpStatusCode.Accepted);
        var job = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.DatabaseProvisioning)
            .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
            .FirstAsync(Ct));
        await DeploymentHarness.SettleAsync(app, job.Id);
    }

    private static void AssertField(System.Text.Json.JsonElement problem, string field) =>
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"No errors entry for {field}: {problem}");

    /// <summary>Adds a Supabase row for <paramref name="environment"/> with ref <paramref name="supabaseRef"/>, available.</summary>
    private static async Task<Guid> SeedSupabaseAsync(WebApplicationFactory<Program> app, Guid projectId, DeploymentEnvironment environment, string supabaseRef)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        var secrets = scope.ServiceProvider.GetRequiredService<ManagedDatabaseSecrets>();
        var now = DateTimeOffset.UtcNow;
        var row = new ManagedDatabase
        {
            ProjectId = projectId,
            Environment = environment,
            Provider = ManagedDatabaseProvider.Supabase,
            ResourceId = supabaseRef,
            Status = ManagedDatabaseStatus.Available,
            CreatedAt = now,
            UpdatedAt = now,
        };
        secrets.SetConnection(row, new ManagedDatabaseConnection
        {
            SupabaseRef = supabaseRef,
            PoolerHost = $"aws-0-{projectId:N}.pooler.supabase.test",
            PoolerUser = $"postgres.{supabaseRef}",
            DatabaseName = "postgres",
        });
        secrets.SetPassword(row, "fake-password");
        db.ManagedDatabases.Add(row);
        await db.SaveChangesAsync(Ct);
        return row.Id;
    }

    [Fact]
    public async Task S4_21_Linking_the_production_Supabase_project_as_the_preview_database_is_refused_with_VALIDATION()
    {
        var (app, owner, id) = await ProjectAsync(VueSupabaseStack);
        using var _ = owner;
        await SetUpAsync(app, owner.Client, id, "production", new { provider = "supabase", link = new { resourceId = "prodref" } });

        var problem = await Problems.AssertAsync(
            await owner.Client.PutAsJsonAsync(DatabasePath(id, "preview"), new { provider = "supabase", link = new { resourceId = " PRODREF " } }, Ct),
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation);

        AssertField(problem, "link.resourceId");
        Assert.Contains(DatabaseSeparationRules.NotSeparateMessage, problem.ToString(), StringComparison.Ordinal);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.ManagedDatabases.AnyAsync(d => d.ProjectId == id && d.Environment == DeploymentEnvironment.Preview, Ct)));

        // A separate project links fine.
        await SetUpAsync(app, owner.Client, id, "preview", new { provider = "supabase", link = new { resourceId = "previewref" } });
        var preview = await ProjectApi.WithDbAsync(app, db => db.ManagedDatabases.AsNoTracking().SingleAsync(d => d.ProjectId == id && d.Environment == DeploymentEnvironment.Preview, Ct));
        Assert.Equal(ManagedDatabaseStatus.Available, preview.Status);
    }

    [Fact]
    public async Task S4_21_An_External_connection_string_on_the_other_environments_host_port_and_database_is_refused()
    {
        var (app, owner, id) = await ProjectAsync(ExternalStack);
        using var _ = owner;
        await SetUpAsync(app, owner.Client, id, "preview", new { provider = "external", external = new { connectionString = "postgresql://app:one@db.example.com:5432/app" } });

        // Either side may be the one being set: production on the preview database is refused too.
        var problem = await Problems.AssertAsync(
            await owner.Client.PutAsJsonAsync(
                DatabasePath(id, "production"), new { provider = "external", external = new { connectionString = "postgres://other:two@DB.example.com/app" } }, Ct),
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation);

        AssertField(problem, "external.connectionString");
        await SetUpAsync(app, owner.Client, id, "production", new { provider = "external", external = new { connectionString = "postgresql://app:one@db.example.com:5432/app_production" } });
    }

    [Fact]
    public async Task S4_21_FR148_A_preview_row_that_is_production_is_never_reset_by_the_endpoint_or_the_job()
    {
        var (app, owner, id) = await ProjectAsync(VueSupabaseStack);
        using var _ = owner;
        await SeedSupabaseAsync(app, id, DeploymentEnvironment.Production, "same");
        var previewId = await SeedSupabaseAsync(app, id, DeploymentEnvironment.Preview, "same");

        await Problems.AssertAsync(
            await owner.Client.PostAsync(new Uri($"{DatabasePath(id, "preview")}/reset", UriKind.Relative), null, Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // A job queued before the rows matched (or behind the API) fails without running any SQL.
        Guid jobId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            var job = scope.ServiceProvider.GetRequiredService<JobEnqueuer>().Enqueue(
                JobTypes.PreviewDatabaseReset, new PreviewDatabaseResetPayload(id, previewId, PreviewResetMethod.ResetSchema, "test"), id);
            await db.SaveChangesAsync(Ct);
            jobId = job.Id;
        }

        Assert.False(await DeploymentHarness.RunOnceAsync(app, jobId));
        var ran = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct));
        Assert.Equal(JobStatus.Failed, ran.Status);
        Assert.Empty(await LifecycleSeed.EventsAsync(app, id, EventTypes.DatabaseReset));
    }

    [Fact]
    public async Task S4_21_FR153_A_preview_migration_run_on_the_production_database_runs_nothing_and_fails_with_VALIDATION()
    {
        var (app, owner, id) = await ProjectAsync(VueSupabaseStack);
        using var _ = owner;
        await SeedSupabaseAsync(app, id, DeploymentEnvironment.Production, "same");
        await SeedSupabaseAsync(app, id, DeploymentEnvironment.Preview, "same");
        var versionId = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == id).Select(v => v.Id).FirstAsync(Ct));

        await using var scope = app.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<MigrationRunService>()
            .RunAsync(id, DeploymentEnvironment.Preview, versionId, null, new string('a', 40), Ct);

        Assert.Equal((MigrationRunStatus.Failed, ErrorCodes.Validation), (run.Status, run.ErrorCode));
        Assert.Empty(run.Applied);
        Assert.Contains(DatabaseSeparationRules.NotSeparateMessage, run.ErrorMessage, StringComparison.Ordinal);
    }
}
