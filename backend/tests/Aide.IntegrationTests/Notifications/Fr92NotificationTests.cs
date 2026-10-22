using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Activity;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Deployments;
using Aide.Api.Features.Notifications;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.IntegrationTests.Live;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Notifications;

/// <summary>
/// FR-92: the v2 notification types (recipients, channels, preference opt-out), the v2 events in the activity feed and the
/// admin event log, and the "preview ready" criterion (live within 2 s, every member notified).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class Fr92NotificationTests(AideApiFactory factory)
{
    private static readonly TimeSpan LiveBudget = TimeSpan.FromSeconds(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NotificationTestApp Test => NotificationTestApp.For(factory);

    private static string SlackUrl() => $"https://hooks.slack.com/services/T0/B0/{Guid.NewGuid():N}";

    private static Task AddEventAsync(WebApplicationFactory<Program> app, string type, Guid? projectId, object payload) =>
        ProjectApi.WithDbAsync(app, async db =>
        {
            db.AddEvent(type, projectId, payload, SystemActor.Name, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        });

    private static async Task MuteAsync(ApiSession session, params string[] types)
    {
        var response = await session.Client.PutAsJsonAsync("/api/me/notification-preferences", new { mutedTypes = types }, ApiSession.Json, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Guid[] Ids(params ApiSession[] sessions) => [.. sessions.Select(s => s.User!.Id).Order()];

    [Fact]
    public async Task FR92_Release_awaiting_merge_notifies_Owners_with_the_PR_link_and_reaches_only_channels_subscribed_to_it()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var subscribed = SlackUrl();
        var releaseFailedOnly = SlackUrl();
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id,
            new { type = "slack", secret = subscribed, events = NotificationScenario.Ev("release_awaiting_merge") },
            new { type = "slack", secret = releaseFailedOnly, events = NotificationScenario.Ev("release_failed") });

        await AddEventAsync(app, EventTypes.ReleaseAwaitingMerge, id, new
        {
            tag = "v0.1.0",
            version = "0.1.0",
            awaitingMerge = true,
            pullRequestUrl = "https://github.com/acme/todo/pull/7",
            reasons = new[] { new { code = "review_required", message = "1 approving review is required." } },
        });
        await NotificationJobs.RunQueuedAsync(app, id);

        var only = Assert.Single(await NotificationJobs.InAppAsync(app, id));
        Assert.Equal((fixture.Owner.User!.Id, "release_awaiting_merge", $"/projects/{id}/release"), (only.UserId, only.Type, only.Link));
        Assert.Contains("https://github.com/acme/todo/pull/7", only.Body, StringComparison.Ordinal);
        Assert.Contains("1 approving review is required.", only.Body, StringComparison.Ordinal);
        var message = Assert.Single(Test.Http.To(subscribed));
        Assert.Contains($"http://localhost:8080/projects/{id}/release", message.Text, StringComparison.Ordinal);
        Assert.Empty(Test.Http.To(releaseFailedOnly));
    }

    [Fact]
    public async Task FR92_Deployment_failed_notifies_Owners_by_email_with_the_version_error_and_a_Redeploy_link()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var ops = ApiSession.NewEmail("ops");
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id,
            new { type = "email", target = ops, events = NotificationScenario.Ev("deployment_failed") });

        await AddEventAsync(app, EventTypes.DeploymentFailed, id, new
        {
            deploymentId = Guid.NewGuid(),
            target = "production",
            status = "error",
            errorCode = "BUILD_FAILED",
            releaseVersion = "1.2.0",
            gitSha = new string('a', 40),
            deploymentFailed = true,
        });
        await NotificationJobs.RunQueuedAsync(app, id);

        var only = Assert.Single(await NotificationJobs.InAppAsync(app, id));
        Assert.Equal((fixture.Owner.User!.Id, "deployment_failed", $"/projects/{id}/release"), (only.UserId, only.Type, only.Link));
        var email = Assert.Single(Test.Email.Sent, m => m.To == ops);
        Assert.Contains("1.2.0", email.Subject, StringComparison.Ordinal);
        Assert.Contains("(BUILD_FAILED)", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("redeploy from the Release page", email.TextBody, StringComparison.Ordinal);
        Assert.Contains($"Open in AIDE: http://localhost:8080/projects/{id}/release", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR92_Preview_ready_notifies_every_member_with_the_url_except_those_who_muted_it()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await MuteAsync(fixture.Viewer, "preview_ready");

        await AddEventAsync(app, EventTypes.DeploymentPreviewReady, id, new
        {
            deploymentId = Guid.NewGuid(),
            target = "preview",
            status = "ready",
            url = "https://todo-abc.vercel.app",
            releaseVersion = "0.1.0",
            previewReady = true,
        });
        await NotificationJobs.RunQueuedAsync(app, id);

        var inApp = await NotificationJobs.InAppAsync(app, id);
        Assert.Equal(Ids(fixture.Owner, fixture.Contributor), inApp.Select(n => n.UserId).Order());
        Assert.All(inApp, n => Assert.Equal(("preview_ready", $"/projects/{id}/review"), (n.Type, n.Link)));
        Assert.Contains("https://todo-abc.vercel.app", inApp[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR92_Llm_binding_cleared_notifies_Owners_with_the_reason()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        using var secondOwner = await ApiSession.RegisterAsync(app);
        await ProjectApi.AddMemberInDbAsync(app, id, secondOwner.User!.Id, ProjectRole.Owner);
        await MuteAsync(secondOwner, "llm_binding_cleared");

        await AddEventAsync(app, EventTypes.LlmBindingCleared, id, new { projectId = id, credentialId = Guid.NewGuid(), boundByUserId = Guid.NewGuid(), provider = "anthropic", reason = "member_removed" });
        await NotificationJobs.RunQueuedAsync(app, id);

        var only = Assert.Single(await NotificationJobs.InAppAsync(app, id));
        Assert.Equal((fixture.Owner.User!.Id, "llm_binding_cleared", $"/projects/{id}/settings/llm"), (only.UserId, only.Type, only.Link));
        Assert.Contains("left the project", only.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR92_Connection_invalid_goes_only_to_the_connection_owner_in_app_and_by_email_never_to_project_channels()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var slack = SlackUrl();
        var ops = ApiSession.NewEmail("ops");
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id,
            new { type = "slack", secret = slack, events = NotificationScenario.Ev("run_failed", "release_failed", "deployment_failed", "llm_binding_cleared") },
            new { type = "email", target = ops, events = NotificationScenario.Ev("run_failed") });
        var owner = fixture.Contributor.User!;

        await AddEventAsync(app, EventTypes.ConnectionInvalid, id, new { credentialId = Guid.NewGuid(), userId = owner.Id, provider = "anthropic", runId = Guid.NewGuid() });
        await NotificationJobs.RunQueuedAsync(app, id);

        var only = Assert.Single(await NotificationJobs.InAppAsync(app, id));
        Assert.Equal((owner.Id, "connection_invalid", "/connections"), (only.UserId, only.Type, only.Link));
        Assert.Equal("Your LLM (Anthropic) connection is invalid", only.Title);
        var delivery = Assert.Single(await NotificationJobs.DeliveriesAsync(app, id));
        Assert.Equal(($"account_email:{owner.Id:D}", NotificationDeliveryStatus.Sent), (delivery.Channel, delivery.Status));
        var email = Assert.Single(Test.Email.Sent, m => m.To == owner.Email);
        Assert.Contains("Open in AIDE: http://localhost:8080/connections", email.TextBody, StringComparison.Ordinal);
        Assert.Empty(Test.Http.To(slack));
        Assert.DoesNotContain(Test.Email.Sent, m => m.To == ops);
    }

    [Fact]
    public async Task FR92_A_muted_connection_invalid_sends_neither_in_app_nor_email()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await MuteAsync(fixture.Owner, "connection_invalid");

        await AddEventAsync(app, EventTypes.ConnectionInvalid, id, new { connectionId = Guid.NewGuid(), userId = fixture.Owner.User!.Id, provider = "vercel" });
        await NotificationJobs.RunQueuedAsync(app, id);

        Assert.Empty(await NotificationJobs.InAppAsync(app, id));
        Assert.Empty(await NotificationJobs.DeliveriesAsync(app, id));
        Assert.DoesNotContain(Test.Email.Sent, m => m.To == fixture.Owner.User!.Email);
    }

    [Fact]
    public async Task FR92_A_GitHub_connection_invalid_outside_any_project_notifies_its_owner()
    {
        var app = Test.Factory;
        using var user = await ApiSession.RegisterAsync(app);
        var userId = user.User!.Id;

        await AddEventAsync(app, EventTypes.ConnectionInvalid, null, new { userId, provider = "github", login = "octo", reason = "refresh_failed" });
        var eventId = await ProjectApi.WithDbAsync(app, db => db.Events.AsNoTracking()
            .Where(e => e.ProjectId == null && e.Type == EventTypes.ConnectionInvalid)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new { e.Id, e.Payload })
            .ToListAsync(Ct));
        var source = eventId.First(e => e.Payload.Contains(userId.ToString(), StringComparison.Ordinal)).Id;
        var jobs = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == null && j.Type == JobTypes.Notification && j.Status == JobStatus.Queued)
            .ToListAsync(Ct));
        var job = Assert.Single(jobs, j => AideJson.Deserialize<NotificationJobPayload>(j.Payload).SourceId == source);
        Assert.Equal(new NotificationJobPayload(null, NotificationType.ConnectionInvalid, source), AideJson.Deserialize<NotificationJobPayload>(job.Payload));

        await AgentJobs.RunAsync(app, job);

        var page = await ProjectApi.ReadAsync<NotificationPageDto>(await user.Client.GetAsync(new Uri("/api/me/notifications", UriKind.Relative), Ct));
        var only = Assert.Single(page.Items);
        Assert.Equal((NotificationType.ConnectionInvalid, (Guid?)null, "/connections", "Your GitHub connection is invalid"), (only.Type, only.ProjectId, only.Link, only.Title));
        Assert.Single(Test.Email.Sent, m => m.To == user.User!.Email);
    }

    [Fact]
    public async Task FR92_Project_channels_accept_the_new_events_but_not_the_personal_connection_invalid()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id,
            new { type = "email", target = ApiSession.NewEmail("ops"), events = NotificationScenario.Ev("release_awaiting_merge", "deployment_failed", "preview_ready", "llm_binding_cleared") });
        var refused = await ProjectApi.PatchAsync(fixture.Owner.Client, $"/api/projects/{id}/settings/notifications", new
        {
            channels = new[] { new { type = "email", target = ApiSession.NewEmail("ops"), events = NotificationScenario.Ev("connection_invalid") } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task FR92_Preferences_mute_any_of_the_new_types_and_default_to_on()
    {
        using var user = await ApiSession.RegisterAsync(factory);
        var defaults = await ProjectApi.ReadAsync<NotificationPreferencesDto>(await user.Client.GetAsync(new Uri("/api/me/notification-preferences", UriKind.Relative), Ct));
        Assert.Empty(defaults.MutedTypes);

        var all = NotificationRules.All.Select(t => t.ToWire()).ToArray();
        var response = await user.Client.PutAsJsonAsync("/api/me/notification-preferences", new { mutedTypes = all }, ApiSession.Json, Ct);
        var updated = await ProjectApi.ReadAsync<NotificationPreferencesDto>(response);

        Assert.Equal(NotificationRules.All.Order(), updated.MutedTypes);
    }

    [Fact]
    public async Task FR92_Every_new_event_is_in_the_activity_feed_and_the_admin_event_log_with_its_category()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var expected = new Dictionary<string, (EventCategory Category, string Summary)>
        {
            [EventTypes.VersionStarted] = (EventCategory.Release, "Version 1.3.0 started"),
            [EventTypes.CodebaseSummaryCreated] = (EventCategory.Artifact, "Codebase summary v1 created by the Repository Analyst"),
            [EventTypes.ArtifactRestored] = (EventCategory.Artifact, "technical_specification v1 restored as draft v3"),
            [EventTypes.ReleaseAwaitingMerge] = (EventCategory.Release, "Release v1.3.0 awaits a merge on GitHub"),
            [EventTypes.ReleaseMergedExternally] = (EventCategory.Release, "Release v1.3.0 merged on GitHub"),
            [EventTypes.DeploymentCreated] = (EventCategory.Release, "preview deployment of aide/x recorded (auto)"),
            [EventTypes.DeploymentUpdated] = (EventCategory.Release, "preview deployment ready"),
            [EventTypes.ConnectionInvalid] = (EventCategory.Run, "Vercel rejected the connection's token; it is marked invalid"),
            [EventTypes.PresetChanged] = (EventCategory.Project, "Preset of version 1.3.0 changed from none to nextjs-supabase"),
        };
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.AddEvent(EventTypes.VersionStarted, id, new { version = "1.3.0" }, SystemActor.Name, now);
            db.AddEvent(EventTypes.CodebaseSummaryCreated, id, new { type = "codebase_summary", version = 1 }, SystemActor.Name, now);
            db.AddEvent(EventTypes.ArtifactRestored, id, new { type = "technical_specification", version = 3, parentVersion = 2, restoredFromVersion = 1 }, SystemActor.Name, now);
            db.AddEvent(EventTypes.ReleaseAwaitingMerge, id, new { tag = "v1.3.0", version = "1.3.0" }, SystemActor.Name, now);
            db.AddEvent(EventTypes.ReleaseMergedExternally, id, new { tag = "v1.3.0", version = "1.3.0" }, SystemActor.Name, now);
            db.AddEvent(EventTypes.DeploymentCreated, id, new { target = "preview", gitRef = "aide/x", trigger = "auto" }, SystemActor.Name, now);
            db.AddEvent(EventTypes.DeploymentUpdated, id, new { target = "preview", status = "ready" }, SystemActor.Name, now);
            db.AddEvent(EventTypes.ConnectionInvalid, id, new { connectionId = Guid.NewGuid(), userId = fixture.Owner.User!.Id, provider = "vercel" }, SystemActor.Name, now);
            db.AddEvent(EventTypes.PresetChanged, id, new { version = "1.3.0", fromPresetId = (string?)null, presetId = "nextjs-supabase" }, SystemActor.Name, now);
            await db.SaveChangesAsync(Ct);
        });

        var feed = await ProjectApi.ReadAsync<ActivityPageDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/activity?limit=100", UriKind.Relative), Ct));
        using var admin = await ApiSession.AdminAsync(factory);
        var log = await ProjectApi.ReadAsync<PagedResult<AdminEventDto>>(await admin.Client.GetAsync(new Uri($"/api/admin/events?projectId={id}&limit=100", UriKind.Relative), Ct));

        foreach (var (type, (category, summary)) in expected)
        {
            var item = Assert.Single(feed.Items, i => i.Type == type);
            Assert.Equal(((EventCategory?)category, summary), (item.Category, item.Summary));
            var logged = Assert.Single(log.Items, e => e.Type == type);
            Assert.Equal(((EventCategory?)category, summary), (logged.Category, logged.Summary));
        }

        var releases = await ProjectApi.ReadAsync<ActivityPageDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/activity?type=release&limit=100", UriKind.Relative), Ct));
        Assert.Equal(
            expected.Where(e => e.Value.Category == EventCategory.Release).Select(e => e.Key).Order(),
            releases.Items.Select(i => i.Type).Order());
    }

    [Fact]
    public async Task FR92_When_a_preview_becomes_READY_viewers_get_it_live_within_2_seconds_and_every_member_is_notified()
    {
        var app = DeploymentHarness.Fake(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await DeploymentHarness.BindAsync(app, id, await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id), fixture.Owner.User!.Id);
        var versionId = await DeploymentHarness.VersionIdAsync(app, id);
        var preview = await DeploymentHarness.ScheduleAsync(app, new NewDeployment(id, versionId, null, DeploymentTarget.Preview, "aide/x", new string('d', 40), DeploymentTrigger.Auto, null));
        var job = await DeploymentHarness.JobOfAsync(app, id, preview.Id);
        await using var viewer = await LiveClient.ConnectAsync(app, fixture.Viewer);
        await viewer.JoinAsync(id);

        // Fake: created (queued) on the first run, building on the second, ready on the third.
        Assert.True(await DeploymentHarness.RunOnceAsync(app, job.Id));
        Assert.True(await DeploymentHarness.RunOnceAsync(app, job.Id));
        Assert.Equal("building", (await viewer.NextOfTypeAsync(EventTypes.DeploymentUpdated, LiveBudget))?.GetProperty("payload").GetProperty("status").GetString());

        var clock = Stopwatch.StartNew();
        Assert.False(await DeploymentHarness.RunOnceAsync(app, job.Id));
        var ready = await viewer.NextOfTypeAsync(EventTypes.DeploymentUpdated, LiveBudget);

        Assert.NotNull(ready);
        Assert.True(clock.Elapsed < LiveBudget, $"took {clock.Elapsed}");
        Assert.Equal(("ready", preview.Id.ToString()), (ready.Value.GetProperty("payload").GetProperty("status").GetString(), ready.Value.GetProperty("payload").GetProperty("deploymentId").GetString()));

        await NotificationJobs.RunQueuedAsync(app, id);
        var notified = (await NotificationJobs.InAppAsync(app, id)).Where(n => n.Type == "preview_ready").ToList();
        Assert.Equal(Ids(fixture.Owner, fixture.Contributor, fixture.Viewer), notified.Select(n => n.UserId).Order());
        Assert.All(notified, n => Assert.Equal($"/projects/{id}/review", n.Link));
    }
}
