using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Activity;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Databases;
using Aide.Api.Features.Notifications;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Notifications;

/// <summary>
/// FR-155: the v4 notification types (Owners only, channels), the feed and admin log, and the database expiry sweep on the
/// Fake database provider: "Given a Render free Postgres 7 days from expiry, then Owners get a 'database expiring'
/// notification with the date", once per database per expiry across repeated sweeps and restarts.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class Fr155NotificationTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> App => DeploymentHarness.Fake(NotificationTestApp.For(factory).Factory);

    private static async Task<Guid> SeedDatabaseAsync(WebApplicationFactory<Program> app, Guid projectId, ManagedDatabaseProvider provider, string resourceId, DateTimeOffset? expiresAt) =>
        await ProjectApi.WithDbAsync(app, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var row = new ManagedDatabase
            {
                ProjectId = projectId,
                Environment = DeploymentEnvironment.Production,
                Provider = provider,
                ResourceId = resourceId,
                Status = ManagedDatabaseStatus.Available,
                ExpiresAt = expiresAt,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ManagedDatabases.Add(row);
            await db.SaveChangesAsync(Ct);
            return row.Id;
        });

    /// <summary>One sweep in its own scope, as each job run (and each restart) gets.</summary>
    private static async Task SweepAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseExpirySweepService>().RunAsync(Ct);
    }

    private static Task<ManagedDatabaseStatus> StatusAsync(WebApplicationFactory<Program> app, Guid databaseId) =>
        ProjectApi.WithDbAsync(app, db => db.ManagedDatabases.AsNoTracking().Where(d => d.Id == databaseId).Select(d => d.Status).SingleAsync(Ct));

    private static Task AddEventAsync(WebApplicationFactory<Program> app, string type, Guid projectId, object payload) =>
        ProjectApi.WithDbAsync(app, async db =>
        {
            db.AddEvent(type, projectId, payload, SystemActor.Name, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        });

    [Fact]
    public async Task FR155_A_Render_free_Postgres_7_days_from_expiry_notifies_Owners_once_with_the_date()
    {
        var app = App;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7).AddMinutes(-1);
        var database = await SeedDatabaseAsync(app, id, ManagedDatabaseProvider.Render, "render-db", expiresAt);

        await SweepAsync(app);
        await SweepAsync(app);
        await NotificationJobs.RunQueuedAsync(app, id);
        await SweepAsync(app);
        await NotificationJobs.RunQueuedAsync(app, id);

        Assert.Equal(ManagedDatabaseStatus.Expiring, await StatusAsync(app, database));
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.DatabaseExpiring));
        var only = Assert.Single(await NotificationJobs.InAppAsync(app, id));
        Assert.Equal((fixture.Owner.User!.Id, "database_expiring", $"/projects/{id}/settings/deployment"), (only.UserId, only.Type, only.Link));
        Assert.Contains(expiresAt.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), only.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR155_A_database_further_from_its_expiry_stays_quiet()
    {
        var app = App;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var database = await SeedDatabaseAsync(app, id, ManagedDatabaseProvider.Render, "render-db", DateTimeOffset.UtcNow.AddDays(8));

        await SweepAsync(app);

        Assert.Equal(ManagedDatabaseStatus.Available, await StatusAsync(app, database));
        Assert.Empty(await LifecycleSeed.EventsAsync(app, id, EventTypes.DatabaseExpiring));
    }

    [Fact]
    public async Task FR155_The_same_expiry_isnt_announced_again_after_the_database_is_linked_again()
    {
        var app = App;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var database = await SeedDatabaseAsync(app, id, ManagedDatabaseProvider.Render, "render-db", DateTimeOffset.UtcNow.AddDays(3));
        await SweepAsync(app);

        await ProjectApi.WithDbAsync(app, db => db.ManagedDatabases.Where(d => d.Id == database)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ManagedDatabaseStatus.Available), Ct));
        await SweepAsync(app);

        Assert.Equal(ManagedDatabaseStatus.Expiring, await StatusAsync(app, database));
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.DatabaseExpiring));
    }

    [Fact]
    public async Task FR155_A_paused_Supabase_project_notifies_Owners_once_and_a_healthy_one_doesnt()
    {
        var app = App;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var database = await SeedDatabaseAsync(app, id, ManagedDatabaseProvider.Supabase, "fake-paused-project", null);

        await SweepAsync(app);
        await SweepAsync(app);
        await NotificationJobs.RunQueuedAsync(app, id);

        Assert.Equal(ManagedDatabaseStatus.Paused, await StatusAsync(app, database));
        var only = Assert.Single(await NotificationJobs.InAppAsync(app, id));
        Assert.Equal((fixture.Owner.User!.Id, "database_expiring"), (only.UserId, only.Type));
        Assert.Contains("paused", only.Title, StringComparison.Ordinal);

        using var healthy = await ProjectApi.FixtureAsync(app);
        var other = await SeedDatabaseAsync(app, healthy.Project.Id, ManagedDatabaseProvider.Supabase, "fake-healthy-project", null);
        await SweepAsync(app);
        Assert.Equal(ManagedDatabaseStatus.Available, await StatusAsync(app, other));
        Assert.Empty(await LifecycleSeed.EventsAsync(app, healthy.Project.Id, EventTypes.DatabaseExpiring));
    }

    [Fact]
    public async Task FR155_The_sweep_job_is_queued_once_while_one_is_waiting_and_runs_on_the_job_table()
    {
        var app = App;
        var sweeper = app.Services.GetRequiredService<NotificationSweeper>();
        await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Type == JobTypes.DatabaseExpirySweep).ExecuteDeleteAsync(Ct));

        Assert.True(await sweeper.QueueDatabaseExpirySweepAsync(Ct));
        Assert.False(await sweeper.QueueDatabaseExpirySweepAsync(Ct));

        var job = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Type == JobTypes.DatabaseExpirySweep, Ct));
        await AgentJobs.RunAsync(app, job);
        var finished = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct));
        Assert.Equal(JobStatus.Succeeded, finished.Status);
        Assert.True(await sweeper.QueueDatabaseExpirySweepAsync(Ct));
        await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Type == JobTypes.DatabaseExpirySweep).ExecuteDeleteAsync(Ct));
    }

    [Theory]
    [InlineData(DeploymentTargetFailureReasons.TokenInvalid, 1)]
    [InlineData(DeploymentTargetFailureReasons.GitAccessLost, 1)]
    [InlineData(DeploymentTargetFailureReasons.SetupFailed, 0)]
    [InlineData(DeploymentTargetFailureReasons.ReadinessFailed, 0)]
    public async Task FR155_A_failed_target_tells_Owners_only_when_it_needs_their_attention(string reason, int notifications)
    {
        var app = App;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;

        await AddEventAsync(app, EventTypes.DeploymentTargetFailed, id, new { targetId = Guid.NewGuid(), component = "backend", provider = "render", reason, errorCode = (string?)null });
        await NotificationJobs.RunQueuedAsync(app, id);

        var inApp = await NotificationJobs.InAppAsync(app, id);
        Assert.Equal(notifications, inApp.Count);
        Assert.All(inApp, n => Assert.Equal((fixture.Owner.User!.Id, "deployment_target_attention"), (n.UserId, n.Type)));
    }

    [Fact]
    public async Task FR155_A_production_migration_failure_notifies_Owners_once_as_migration_failed()
    {
        var app = App;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;

        await AddEventAsync(app, EventTypes.MigrationRunFailed, id, new { runId = Guid.NewGuid(), environment = "production", releaseId = Guid.NewGuid(), gitSha = new string('a', 40), errorCode = ErrorCodes.MigrationFailed });
        await AddEventAsync(app, EventTypes.DeploymentFailed, id, new { deploymentId = (Guid?)null, component = "migrations", target = "production", errorCode = ErrorCodes.MigrationFailed, releaseVersion = "1.2.0", deploymentFailed = true });
        await NotificationJobs.RunQueuedAsync(app, id);

        var only = Assert.Single(await NotificationJobs.InAppAsync(app, id));
        Assert.Equal((fixture.Owner.User!.Id, "migration_failed", $"/projects/{id}/migrations"), (only.UserId, only.Type, only.Link));
    }

    [Fact]
    public async Task FR155_A_channel_subscribed_to_deployment_failed_gets_one_migration_failed_delivery_and_Owners_one_in_app()
    {
        var app = App;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var ops = ApiSession.NewEmail("ops");
        var both = ApiSession.NewEmail("both");
        var neither = ApiSession.NewEmail("neither");
        var test = NotificationTestApp.For(factory);
        await NotificationScenario.ConfigureChannelsAsync(
            fixture.Owner.Client,
            id,
            new { type = "email", target = ops, events = NotificationScenario.Ev("deployment_failed") },
            new { type = "email", target = both, events = NotificationScenario.Ev("deployment_failed", "migration_failed") },
            new { type = "email", target = neither, events = NotificationScenario.Ev("run_failed") });

        await AddEventAsync(app, EventTypes.MigrationRunFailed, id, new { runId = Guid.NewGuid(), environment = "production", errorCode = ErrorCodes.MigrationFailed });
        await NotificationJobs.RunQueuedAsync(app, id);

        Assert.Equal("migration_failed", Assert.Single(await NotificationJobs.InAppAsync(app, id)).Type);
        Assert.Single(test.Email.Sent, m => m.To == ops);
        Assert.Single(test.Email.Sent, m => m.To == both);
        Assert.DoesNotContain(test.Email.Sent, m => m.To == neither);
    }

    [Fact]
    public async Task FR155_Database_provisioning_failure_notifies_Owners_and_subscribed_channels()
    {
        var app = App;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var ops = ApiSession.NewEmail("ops");
        var test = NotificationTestApp.For(factory);
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id, new { type = "email", target = ops, events = NotificationScenario.Ev("database_provisioning_failed") });

        await AddEventAsync(app, EventTypes.DatabaseProvisioningFailed, id, new { databaseId = Guid.NewGuid(), environment = "production", provider = "supabase", errorCode = ErrorCodes.PlatformPlanLimit });
        await NotificationJobs.RunQueuedAsync(app, id);

        var only = Assert.Single(await NotificationJobs.InAppAsync(app, id));
        Assert.Equal((fixture.Owner.User!.Id, "database_provisioning_failed"), (only.UserId, only.Type));
        Assert.Contains(test.Email.Sent, m => m.To == ops && m.TextBody.Contains(ErrorCodes.PlatformPlanLimit, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR155_The_new_events_are_in_the_feed_and_the_admin_log_with_their_category_and_summary()
    {
        var app = App;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var events = new (string Type, object Payload, string Summary)[]
        {
            (EventTypes.StackDetected, new { version = "1.3.0" }, "Stack detected at the base of version 1.3.0"),
            (EventTypes.DeploymentTargetConfigured, new { component = "backend", provider = "render" }, "backend target configured on render"),
            (EventTypes.DeploymentTargetFailed, new { component = "backend", reason = "setup_failed" }, "backend target needs attention (setup_failed)"),
            (EventTypes.DatabaseProvisioned, new { environment = "production", provider = "supabase" }, "production database available on supabase"),
            (EventTypes.DatabaseProvisioningFailed, new { environment = "preview", errorCode = "PLATFORM_ERROR" }, "preview database setup failed: PLATFORM_ERROR"),
            (EventTypes.DatabaseExpiring, new { environment = "preview", reason = "paused" }, "preview database is paused"),
            (EventTypes.MigrationRunStarted, new { environment = "preview" }, "preview migration run started"),
            (EventTypes.MigrationRunCompleted, new { environment = "preview", applied = 2 }, "preview migration run applied 2 file(s)"),
        };
        foreach (var (type, payload, _) in events)
        {
            await AddEventAsync(app, type, id, payload);
        }

        var feed = await ProjectApi.ReadAsync<ActivityPageDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/activity?limit=100", UriKind.Relative), Ct));
        using var admin = await ApiSession.AdminAsync(app);
        var log = await ProjectApi.ReadAsync<PagedResult<AdminEventDto>>(await admin.Client.GetAsync(new Uri($"/api/admin/events?projectId={id}&limit=100", UriKind.Relative), Ct));

        foreach (var (type, _, summary) in events)
        {
            Assert.Equal(summary, Assert.Single(feed.Items, i => i.Type == type).Summary);
            Assert.Equal(summary, Assert.Single(log.Items, e => e.Type == type).Summary);
        }
    }

    [Fact]
    public async Task FR155_Changing_a_GitLab_instance_records_GIT_HOST_INSTANCE_CHANGED_without_a_project_or_a_secret()
    {
        var app = App;
        using var admin = await ApiSession.AdminAsync(app);
        var instances = await admin.Client.GetFromJsonAsync<Aide.Api.Features.GitLab.AdminGitLabIntegrationDto>("/api/admin/integrations/gitlab", ApiSession.Json, Ct);
        var gitlabCom = instances!.Instances.Single(i => i.IsGitLabCom);
        const string Secret = "gloas-fr155-secret";

        Assert.False(gitlabCom.HasClientSecret);
        try
        {
            var response = await admin.Client.PatchAsJsonAsync($"/api/admin/integrations/gitlab/{gitlabCom.Id}", new { clientSecret = Secret, enabled = gitlabCom.Enabled }, ApiSession.Json, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var changed = await ProjectApi.WithDbAsync(app, db => db.Events.AsNoTracking()
                .Where(e => e.Type == EventTypes.GitHostInstanceChanged && e.ProjectId == null)
                .OrderByDescending(e => e.CreatedAt)
                .FirstAsync(Ct));
            var payload = LifecycleSeed.Payload(changed.Payload);
            Assert.Equal((gitlabCom.Id, "gitlab", "updated"), (payload.GetProperty("instanceId").GetGuid(), payload.GetProperty("kind").GetString(), payload.GetProperty("change").GetString()));
            Assert.DoesNotContain(Secret, changed.Payload, StringComparison.Ordinal);
        }
        finally
        {
            // gitlab.com's row is shared by the test group: leave it without a secret, as it was.
            await admin.Client.PatchAsJsonAsync($"/api/admin/integrations/gitlab/{gitlabCom.Id}", new { clientSecret = string.Empty }, ApiSession.Json, Ct);
        }
    }
}
