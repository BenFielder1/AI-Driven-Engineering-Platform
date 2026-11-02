using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Notifications;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Notifications;

/// <summary>
/// FR-193: <c>stack_profile_ready</c> (Owners, one per draft, or a failed Stack Analyst) and <c>tracker_import_failed</c> (Owners and
/// the importer), through the outbox, the fan-out and project channels; FR-165: the 14-day expiry warning of Jira and Linear
/// connections, once per date, on the personal <c>connection_invalid</c> rules.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class Fr193NotificationTests(AideApiFactory factory)
{
    private static readonly string[] Backend = ["backend"];
    private static readonly string[] NewTypes = ["stack_profile_ready", "tracker_import_failed"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NotificationTestApp Test => NotificationTestApp.For(factory);

    private static string SlackUrl() => $"https://hooks.slack.com/services/T0/B0/{Guid.NewGuid():N}";

    private static Guid[] Ids(params ApiSession[] sessions) => [.. sessions.Select(s => s.User!.Id).Order()];

    private static Task AddEventsAsync(WebApplicationFactory<Program> app, Guid? projectId, params (string Type, object Payload)[] events) =>
        ProjectApi.WithDbAsync(app, async db =>
        {
            foreach (var (type, payload) in events)
            {
                db.AddEvent(type, projectId, payload, SystemActor.Name, DateTimeOffset.UtcNow);
            }

            await db.SaveChangesAsync(Ct);
        });

    [Fact]
    public async Task FR193_Given_a_draft_profile_Owners_get_one_notification_with_a_link_to_the_Analysis_page()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var subscribed = SlackUrl();
        var artifactReadyOnly = SlackUrl();
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id,
            new { type = "slack", secret = subscribed, events = NotificationScenario.Ev("stack_profile_ready") },
            new { type = "slack", secret = artifactReadyOnly, events = NotificationScenario.Ev("artifact_ready") });
        var artifactId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        // As the Stack Analyst's draft writes them: ARTIFACT_CREATED and STACK_PROFILE_CREATED in one save.
        await AddEventsAsync(app, id,
            (EventTypes.ArtifactCreated, new { artifactId, type = "stack_profile", version = 1, parentVersion = (int?)null, author = "agent", agentRunId = runId }),
            (EventTypes.StackProfileCreated, new { artifactId, version = 1, releaseVersionId = Guid.NewGuid(), agentRunId = runId, unresolvedLayers = Backend }));
        Assert.Single(await LifecycleSeed.NotificationJobsAsync(app, id));
        await NotificationJobs.RunQueuedAsync(app, id);

        var only = Assert.Single(await NotificationJobs.InAppAsync(app, id));
        Assert.Equal((fixture.Owner.User!.Id, "stack_profile_ready", $"/projects/{id}/analysis"), (only.UserId, only.Type, only.Link));
        Assert.Contains("stack profile v1", only.Title, StringComparison.Ordinal);
        Assert.Single(Test.Http.To(subscribed));
        Assert.Empty(Test.Http.To(artifactReadyOnly));
    }

    [Fact]
    public async Task FR193_A_failed_Stack_Analyst_run_notifies_stack_profile_ready_and_not_run_failed()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var runFailedOnly = SlackUrl();
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id,
            new { type = "slack", secret = runFailedOnly, events = NotificationScenario.Ev("run_failed") });

        await AddEventsAsync(app, id, (EventTypes.RunFailed, new { runId = Guid.NewGuid(), agent = "stack_analyst", errorCode = ErrorCodes.StackProfileInvalid, attempts = 1, deadLettered = false }));
        await NotificationJobs.RunQueuedAsync(app, id);

        var only = Assert.Single(await NotificationJobs.InAppAsync(app, id));
        Assert.Equal((fixture.Owner.User!.Id, "stack_profile_ready", $"/projects/{id}/analysis"), (only.UserId, only.Type, only.Link));
        Assert.Contains("Stack Analyst run failed", only.Title, StringComparison.Ordinal);
        Assert.Contains($"({ErrorCodes.StackProfileInvalid})", only.Body, StringComparison.Ordinal);
        Assert.Empty(Test.Http.To(runFailedOnly));
    }

    [Fact]
    public async Task FR193_A_failed_import_notifies_the_Owners_and_the_importer_but_not_other_members()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;

        await AddEventsAsync(app, id, (EventTypes.TrackerImportFailed, new
        {
            importId = Guid.NewGuid(),
            provider = "jira",
            errorCode = ErrorCodes.TrackerImportFailed,
            releaseVersionId = Guid.NewGuid(),
            importedBy = fixture.Contributor.User!.Id,
        }));
        await NotificationJobs.RunQueuedAsync(app, id);

        var inApp = await NotificationJobs.InAppAsync(app, id);
        Assert.Equal(Ids(fixture.Owner, fixture.Contributor), inApp.Select(n => n.UserId).Order());
        Assert.All(inApp, n => Assert.Equal(("tracker_import_failed", $"/projects/{id}/refinement"), (n.Type, n.Link)));
        Assert.Contains("import from Jira failed", inApp[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR193_A_failed_import_by_someone_who_left_notifies_only_the_Owners()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        using var outsider = await ApiSession.RegisterAsync(app);

        await AddEventsAsync(app, id, (EventTypes.TrackerImportFailed, new { importId = Guid.NewGuid(), provider = "linear", errorCode = ErrorCodes.TrackerNotConnected, importedBy = outsider.User!.Id }));
        await NotificationJobs.RunQueuedAsync(app, id);

        var only = Assert.Single(await NotificationJobs.InAppAsync(app, id));
        Assert.Equal(fixture.Owner.User!.Id, only.UserId);
    }

    [Fact]
    public async Task FR193_Project_channels_accept_stack_profile_ready_and_tracker_import_failed()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await ProjectApi.PatchAsync(fixture.Owner.Client, $"/api/projects/{fixture.Project.Id}/settings/notifications", new
        {
            channels = new[] { new { type = "email", target = ApiSession.NewEmail("ops"), events = NotificationScenario.Ev("stack_profile_ready", "tracker_import_failed") } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void FR193_The_channel_event_cap_is_the_number_of_selectable_events()
    {
        var events = Enum.GetValues<Aide.Api.Features.Projects.NotificationEvent>();
        Assert.Equal(Aide.Api.Features.Projects.ProjectLimits.MaxNotificationEvents, events.Length);
        Assert.Equal(Enumerable.Range(0, events.Length), events.Select(e => (int)e));
        Assert.Equal(NotificationRules.ChannelTypes.Select(t => t.ToWire()).Order(), events.Select(e => e.ToWire()).Order());
    }

    [Fact]
    public async Task FR193_A_channel_can_subscribe_to_every_selectable_event()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var every = NotificationRules.ChannelTypes.Select(t => t.ToWire()).ToArray();
        Assert.Equal(21, every.Length);

        var response = await ProjectApi.PatchAsync(fixture.Owner.Client, $"/api/projects/{fixture.Project.Id}/settings/notifications", new
        {
            channels = new[] { new { type = "email", target = ApiSession.NewEmail("ops"), events = NotificationScenario.Ev(every) } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FR193_Preferences_can_mute_the_new_types()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        var response = await user.Client.PutAsJsonAsync("/api/me/notification-preferences", new { mutedTypes = NewTypes }, ApiSession.Json, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- FR-165: the expiry warning ---

    private static Task<Guid> SeedTrackerConnectionAsync(WebApplicationFactory<Program> app, Guid userId, PlatformProvider provider, DateTimeOffset? expiresAt, PlatformConnectionStatus status = PlatformConnectionStatus.Valid) =>
        ProjectApi.WithDbAsync(app, async db =>
        {
            var connection = new PlatformConnection
            {
                UserId = userId,
                Provider = provider,
                Label = "Work",
                SecretEncrypted = "not-a-real-ciphertext",
                Last4 = "ab12",
                ExpiresAt = expiresAt,
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.PlatformConnections.Add(connection);
            await db.SaveChangesAsync(Ct);
            return connection.Id;
        });

    private static Task<List<AuditEvent>> ExpiringEventsAsync(WebApplicationFactory<Program> app, Guid connectionId) =>
        ProjectApi.WithDbAsync(app, async db => (await db.Events.AsNoTracking()
                .Where(e => e.ProjectId == null && e.Type == EventTypes.ConnectionExpiring)
                .ToListAsync(Ct))
            .Where(e => e.Payload.Contains(connectionId.ToString(), StringComparison.Ordinal))
            .ToList());

    [Fact]
    public async Task FR165_A_Jira_token_14_days_from_its_expiry_warns_its_owner_once_in_app_and_by_email()
    {
        var app = Test.Factory;
        using var user = await ApiSession.RegisterAsync(app);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(10);
        var connectionId = await SeedTrackerConnectionAsync(app, user.User!.Id, PlatformProvider.Jira, expiresAt);
        var sweeper = app.Services.GetRequiredService<NotificationSweeper>();

        await sweeper.RunConnectionExpirySweepAsync(Ct);
        await sweeper.RunConnectionExpirySweepAsync(Ct);

        var warning = Assert.Single(await ExpiringEventsAsync(app, connectionId));
        Assert.DoesNotContain("Work", warning.Payload, StringComparison.Ordinal);
        var job = Assert.Single(await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == null && j.Type == JobTypes.Notification && j.Status == JobStatus.Queued)
            .ToListAsync(Ct)), j => AideJson.Deserialize<NotificationJobPayload>(j.Payload).SourceId == warning.Id);
        Assert.Equal(NotificationType.ConnectionInvalid, AideJson.Deserialize<NotificationJobPayload>(job.Payload).Type);

        await AgentJobs.RunAsync(app, job);

        var only = Assert.Single(await ProjectApi.WithDbAsync(app, db => db.Notifications.AsNoTracking().Where(n => n.UserId == user.User!.Id).ToListAsync(Ct)));
        Assert.Equal(("connection_invalid", "/connections"), (only.Type, only.Link));
        Assert.StartsWith("Your Jira connection expires on ", only.Title, StringComparison.Ordinal);
        Assert.Single(Test.Email.Sent, m => m.To == user.User!.Email);
    }

    [Fact]
    public async Task FR165_No_warning_far_from_the_expiry_for_an_invalid_connection_or_a_platform_token()
    {
        var app = Test.Factory;
        using var user = await ApiSession.RegisterAsync(app);
        var far = await SeedTrackerConnectionAsync(app, user.User!.Id, PlatformProvider.Linear, DateTimeOffset.UtcNow.AddDays(20));
        var invalid = await SeedTrackerConnectionAsync(app, user.User!.Id, PlatformProvider.Jira, DateTimeOffset.UtcNow.AddDays(3), PlatformConnectionStatus.Invalid);
        var vercel = await SeedTrackerConnectionAsync(app, user.User!.Id, PlatformProvider.Vercel, DateTimeOffset.UtcNow.AddDays(3));

        await app.Services.GetRequiredService<NotificationSweeper>().RunConnectionExpirySweepAsync(Ct);

        Assert.Empty(await ExpiringEventsAsync(app, far));
        Assert.Empty(await ExpiringEventsAsync(app, invalid));
        Assert.Empty(await ExpiringEventsAsync(app, vercel));
    }

    [Fact]
    public async Task FR165_A_new_expiry_date_warns_again()
    {
        var app = Test.Factory;
        using var user = await ApiSession.RegisterAsync(app);
        var connectionId = await SeedTrackerConnectionAsync(app, user.User!.Id, PlatformProvider.Linear, DateTimeOffset.UtcNow.AddDays(5));
        var sweeper = app.Services.GetRequiredService<NotificationSweeper>();
        await sweeper.RunConnectionExpirySweepAsync(Ct);

        await ProjectApi.WithDbAsync(app, db => db.PlatformConnections.Where(c => c.Id == connectionId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, DateTimeOffset.UtcNow.AddDays(12)), Ct));
        await sweeper.RunConnectionExpirySweepAsync(Ct);
        await sweeper.RunConnectionExpirySweepAsync(Ct);

        Assert.Equal(2, (await ExpiringEventsAsync(app, connectionId)).Count);
    }
}
