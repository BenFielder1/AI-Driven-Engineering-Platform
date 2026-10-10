using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aide.Api.Features.Gates;
using Aide.Api.Features.Lifecycle;
using Aide.Domain.Access;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aide.IntegrationTests.Notifications;

/// <summary>FR-47, FR-49, NFR-SEC: notification fan-out (outbox job), in-app recipients and preferences, channel delivery,
/// per-channel retries, webhook signatures and SSRF refusal.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class NotificationDeliveryTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NotificationTestApp Test => NotificationTestApp.For(factory);

    private static string SlackUrl() => $"https://hooks.slack.com/services/T0/B0/{Guid.NewGuid():N}";

    [Fact]
    public async Task FR47_Slack_configured_for_gate_required_gets_exactly_one_message_when_a_gate_is_created()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var slack = SlackUrl();
        var other = SlackUrl();
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id,
            new { type = "slack", secret = slack, events = NotificationScenario.Ev("gate_required") },
            new { type = "slack", secret = other, events = NotificationScenario.Ev("review_completed") });

        var gate = await NotificationScenario.PendingSpecGateAsync(app, fixture);
        var jobs = await LifecycleSeed.NotificationJobsAsync(app, id);
        var job = Assert.Single(jobs);
        Assert.Equal(new NotificationJobPayload(id, NotificationType.GateRequired, gate.Id), AideJson.Deserialize<NotificationJobPayload>(job.Payload));

        await NotificationJobs.RunQueuedAsync(app, id);

        var message = Assert.Single(Test.Http.To(slack));
        Assert.Empty(Test.Http.To(other));
        using var body = JsonDocument.Parse(message.Body);
        var text = body.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("specification approval", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"http://localhost:8080/projects/{id}", text, StringComparison.Ordinal);
        Assert.Contains("|Approve>", text, StringComparison.Ordinal);
        Assert.Contains("/approvals/", text, StringComparison.Ordinal);

        var delivery = Assert.Single(await NotificationJobs.DeliveriesAsync(app, id));
        Assert.Equal((NotificationDeliveryStatus.Sent, 1, $"gate_required:{gate.Id}"), (delivery.Status, delivery.Attempts, delivery.NotificationKey));
        Assert.NotNull(delivery.SentAt);
    }

    [Fact]
    public async Task FR47_Gate_notifications_go_to_owners_only_and_skip_users_who_muted_the_type()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        using var mutingOwner = await ApiSession.RegisterAsync(app);
        await ProjectApi.AddMemberInDbAsync(app, id, mutingOwner.User!.Id, ProjectRole.Owner);
        var muted = await mutingOwner.Client.PutAsJsonAsync("/api/me/notification-preferences", new { mutedTypes = NotificationScenario.Ev("gate_required") }, ApiSession.Json, Ct);
        Assert.Equal(HttpStatusCode.OK, muted.StatusCode);

        await NotificationScenario.PendingSpecGateAsync(app, fixture);
        await NotificationJobs.RunQueuedAsync(app, id);

        var inApp = await NotificationJobs.InAppAsync(app, id);
        var only = Assert.Single(inApp);
        Assert.Equal((fixture.Owner.User!.Id, "gate_required", $"/projects/{id}"), (only.UserId, only.Type, only.Link));
        Assert.DoesNotContain("approvals", only.Link, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR47_Outcomes_like_a_rollback_notify_every_member()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;

        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.AddEvent(EventTypes.RollbackCompleted, id, new { fromVersion = 5, targetVersion = 3, reason = "Plan was wrong" }, SystemActor.Name, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        });
        await NotificationJobs.RunQueuedAsync(app, id);

        var inApp = await NotificationJobs.InAppAsync(app, id);
        Assert.Equal(
            new[] { fixture.Owner.User!.Id, fixture.Contributor.User!.Id, fixture.Viewer.User!.Id }.Order(),
            inApp.Select(n => n.UserId).Order());
        Assert.All(inApp, n => Assert.Equal("rollback", n.Type));
        Assert.Contains("version 3", inApp[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR47_The_outbox_queues_one_job_per_event_only_when_the_event_commits()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;

        await ProjectApi.WithDbAsync(app, async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.AddEvent(EventTypes.RunFailed, id, new { agent = "spec_generator", errorCode = "PROVIDER_ERROR" }, SystemActor.Name, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
            await transaction.RollbackAsync(Ct);
        });
        Assert.Empty(await LifecycleSeed.NotificationJobsAsync(app, id));

        var saved = await ProjectApi.WithDbAsync(app, async db =>
        {
            var e = db.AddEvent(EventTypes.RunFailed, id, new { agent = "spec_generator", errorCode = "PROVIDER_ERROR" }, SystemActor.Name, DateTimeOffset.UtcNow);
            db.AddEvent(EventTypes.RunStarted, id, new { agent = "spec_generator" }, SystemActor.Name, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
            return e;
        });

        var job = Assert.Single(await LifecycleSeed.NotificationJobsAsync(app, id));
        Assert.Equal(new NotificationJobPayload(id, NotificationType.RunFailed, saved.Id), AideJson.Deserialize<NotificationJobPayload>(job.Payload));
    }

    [Fact]
    public async Task FR47_A_redelivered_notification_job_creates_nothing_twice()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var slack = SlackUrl();
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id, new { type = "slack", secret = slack, events = NotificationScenario.Ev("gate_required") });
        await NotificationScenario.PendingSpecGateAsync(app, fixture);

        var job = Assert.Single(await NotificationJobs.RunQueuedAsync(app, id));
        await AgentJobs.RunAsync(app, job);
        await AgentJobs.RunAsync(app, job);

        Assert.Single(await NotificationJobs.InAppAsync(app, id));
        Assert.Single(await NotificationJobs.DeliveriesAsync(app, id));
        Assert.Single(Test.Http.To(slack));
        Assert.Equal(2, await ProjectApi.WithDbAsync(app, db => db.ApprovalLinks.CountAsync(l => db.ApprovalGates.Any(g => g.Id == l.GateId && g.ProjectId == id), Ct)));
    }

    [Fact]
    public async Task FR49_When_Slack_succeeds_and_email_fails_only_email_is_retried()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var slack = SlackUrl();
        var email = ApiSession.NewEmail("ops");
        Test.Email.FailNext(email, 1);
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id,
            new { type = "slack", secret = slack, events = NotificationScenario.Ev("gate_required") },
            new { type = "email", target = email, events = NotificationScenario.Ev("gate_required") });

        var before = DateTimeOffset.UtcNow;
        await NotificationScenario.PendingSpecGateAsync(app, fixture);
        await NotificationJobs.RunQueuedAsync(app, id);

        var deliveries = await NotificationJobs.DeliveriesAsync(app, id);
        var emailDelivery = Assert.Single(deliveries, d => d.Channel.StartsWith("email:", StringComparison.Ordinal));
        var slackDelivery = Assert.Single(deliveries, d => d.Channel.StartsWith("slack:", StringComparison.Ordinal));
        Assert.Equal(NotificationDeliveryStatus.Sent, slackDelivery.Status);
        Assert.Equal((NotificationDeliveryStatus.Pending, 1, "SMTP error (451)."), (emailDelivery.Status, emailDelivery.Attempts, emailDelivery.LastError));
        Assert.InRange(emailDelivery.NextAttemptAt!.Value, before.AddSeconds(59), DateTimeOffset.UtcNow.AddMinutes(1).AddSeconds(1));
        Assert.DoesNotContain(Test.Email.Sent, m => m.To == email);

        // Not due yet: nothing is attempted.
        await NotificationJobs.RetryDueDeliveriesAsync(app);
        Assert.DoesNotContain(Test.Email.Sent, m => m.To == email);

        await NotificationJobs.MakeDueAsync(app, id);
        await NotificationJobs.RetryDueDeliveriesAsync(app);

        Assert.Single(Test.Email.Sent, m => m.To == email);
        Assert.Single(Test.Http.To(slack));
        deliveries = await NotificationJobs.DeliveriesAsync(app, id);
        Assert.All(deliveries, d => Assert.Equal(NotificationDeliveryStatus.Sent, d.Status));
        Assert.Equal(2, deliveries.Single(d => d.Channel.StartsWith("email:", StringComparison.Ordinal)).Attempts);
        var sent = Test.Email.Sent.Single(m => m.To == email);
        Assert.Contains("Approve: http://localhost:8080/approvals/", sent.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR49_A_channel_that_keeps_failing_is_failed_after_five_attempts()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var slack = SlackUrl();
        Test.Http.Script(slack, Enumerable.Repeat(HttpStatusCode.ServiceUnavailable, 10).ToArray());
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id, new { type = "slack", secret = slack, events = NotificationScenario.Ev("run_failed") });
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.AddEvent(EventTypes.RunFailed, id, new { agent = "plan_generator", errorCode = "PROVIDER_ERROR" }, SystemActor.Name, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        });

        await NotificationJobs.RunQueuedAsync(app, id);
        for (var i = 0; i < 6; i++)
        {
            await NotificationJobs.MakeDueAsync(app, id);
            await NotificationJobs.RetryDueDeliveriesAsync(app);
        }

        var delivery = Assert.Single(await NotificationJobs.DeliveriesAsync(app, id));
        Assert.Equal((NotificationDeliveryStatus.Failed, DeliveryPolicy.MaxAttempts, (DateTimeOffset?)null), (delivery.Status, delivery.Attempts, delivery.NextAttemptAt));
        Assert.Contains("HTTP 503", delivery.LastError, StringComparison.Ordinal);
        Assert.Equal(DeliveryPolicy.MaxAttempts, Test.Http.To(slack).Count);
    }

    [Fact]
    public async Task FR47_Without_SMTP_email_is_skipped_with_a_warning_and_other_channels_still_send()
    {
        var test = NotificationTestApp.For(factory, smtpConfigured: false);
        var app = test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var teams = $"https://contoso.webhook.office.com/webhookb2/{Guid.NewGuid():N}";
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id,
            new { type = "email", target = ApiSession.NewEmail("ops"), events = NotificationScenario.Ev("rollback") },
            new { type = "teams", secret = teams, events = NotificationScenario.Ev("rollback") });
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.AddEvent(EventTypes.RollbackCompleted, id, new { fromVersion = 4, targetVersion = 2 }, SystemActor.Name, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        });

        await NotificationJobs.RunQueuedAsync(app, id);

        var deliveries = await NotificationJobs.DeliveriesAsync(app, id);
        Assert.Equal(NotificationDeliveryStatus.Skipped, deliveries.Single(d => d.Channel.StartsWith("email:", StringComparison.Ordinal)).Status);
        Assert.Equal(NotificationDeliveryStatus.Sent, deliveries.Single(d => d.Channel.StartsWith("teams:", StringComparison.Ordinal)).Status);
        var card = Assert.Single(test.Http.To(teams));
        Assert.Contains("application/vnd.microsoft.card.adaptive", card.Text, StringComparison.Ordinal);
        Assert.Empty(test.Email.Sent);
        Assert.Contains(factory.Logs.Entries, l => l.Level == LogLevel.Warning && l.Message.Contains("SMTP is not configured", StringComparison.Ordinal));

        // A skipped delivery is never retried.
        await NotificationJobs.MakeDueAsync(app, id);
        Assert.Equal(0, await ProjectApi.WithDbAsync(app, db => db.NotificationDeliveries.CountAsync(d => d.ProjectId == id && d.Status == NotificationDeliveryStatus.Pending, Ct)));
    }

    [Fact]
    public async Task FR47_Webhook_bodies_are_HMAC_signed_with_the_channel_secret()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var url = $"https://hooks.example.com/aide/{Guid.NewGuid():N}";
        const string secret = "webhook-signing-secret";
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id, new { type = "webhook", target = url, secret, events = NotificationScenario.Ev("budget_exceeded") });
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.AddEvent(EventTypes.BudgetExceeded, id, new { agent = "code_generator", tokenBudget = 10, tokensUsed = 12 }, SystemActor.Name, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        });

        await NotificationJobs.RunQueuedAsync(app, id);

        var request = Assert.Single(Test.Http.To(url));
        var signature = request.Headers[WebhookSignature.HeaderName];
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), request.Body));
        Assert.Equal(expected, signature);
        Assert.True(WebhookSignature.Verify(secret, request.Body, signature));
        Assert.False(WebhookSignature.Verify("another-secret", request.Body, signature));

        var delivery = Assert.Single(await NotificationJobs.DeliveriesAsync(app, id));
        Assert.Equal(delivery.Id.ToString("D"), request.Headers["Idempotency-Key"]);
        Assert.Equal(delivery.Id.ToString("D"), request.Headers[WebhookSignature.DeliveryHeaderName]);
        Assert.True(long.TryParse(request.Headers[WebhookSignature.TimestampHeaderName], out _));
        Assert.Equal("budget_exceeded", request.Headers[WebhookSignature.EventHeaderName]);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(("budget_exceeded", id), (body.RootElement.GetProperty("type").GetString(), body.RootElement.GetProperty("project").GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task NFR_SEC_A_webhook_without_a_usable_signing_secret_fails_and_is_never_sent_unsigned()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var url = $"https://hooks.example.com/aide/{Guid.NewGuid():N}";
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id, new { type = "webhook", target = url, secret = "webhook-signing-secret", events = NotificationScenario.Ev("run_failed") });
        // A stored channel whose key is gone (e.g. written before keys were required); settings no longer accept one.
        await ProjectApi.WithDbAsync(app, async db =>
        {
            var settings = await db.ProjectNotificationSettings.SingleAsync(s => s.ProjectId == id, Ct);
            settings.Channels = [.. settings.Channels.Select(c => c with { SecretEncrypted = null })];
            db.AddEvent(EventTypes.RunFailed, id, new { agent = "plan_generator", errorCode = "PROVIDER_ERROR" }, SystemActor.Name, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        });

        await NotificationJobs.RunQueuedAsync(app, id);

        Assert.Empty(Test.Http.To(url));
        var delivery = Assert.Single(await NotificationJobs.DeliveriesAsync(app, id));
        Assert.Equal((NotificationDeliveryStatus.Failed, 1, "The webhook has no usable signing secret."), (delivery.Status, delivery.Attempts, delivery.LastError));
    }

    [Fact]
    public async Task FR49_A_gate_notification_retry_is_skipped_once_the_gate_was_decided()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var slack = SlackUrl();
        Test.Http.Script(slack, HttpStatusCode.ServiceUnavailable);
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id, new { type = "slack", secret = slack, events = NotificationScenario.Ev("gate_required") });
        var gate = await NotificationScenario.PendingSpecGateAsync(app, fixture);
        await NotificationJobs.RunQueuedAsync(app, id);
        Assert.Equal(NotificationDeliveryStatus.Pending, Assert.Single(await NotificationJobs.DeliveriesAsync(app, id)).Status);

        await ProjectApi.WithDbAsync(app, db => db.ApprovalGates.Where(g => g.Id == gate.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(g => g.Status, GateStatus.Approved), Ct));
        await NotificationJobs.MakeDueAsync(app, id);
        await NotificationJobs.RetryDueDeliveriesAsync(app);

        var delivery = Assert.Single(await NotificationJobs.DeliveriesAsync(app, id));
        Assert.Equal((NotificationDeliveryStatus.Skipped, 2, "The gate is no longer pending."), (delivery.Status, delivery.Attempts, delivery.LastError));
        Assert.Single(Test.Http.To(slack)); // only the first, failed attempt was sent
    }

    [Fact]
    public async Task FR47_Repeated_budget_refusals_notify_owners_once_until_the_budget_changes()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var slack = SlackUrl();
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id, new { type = "slack", secret = slack, events = NotificationScenario.Ev("budget_exceeded") });
        await ProjectApi.WithDbAsync(app, db => db.Projects.Where(p => p.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.TokenBudget, (long?)1_000L).SetProperty(p => p.TokensUsed, 5_000L), Ct));

        for (var i = 0; i < 3; i++)
        {
            await Problems.AssertAsync(await StartRefinementAsync(fixture.Contributor.Client, id), HttpStatusCode.Conflict, ErrorCodes.BudgetExceeded);
        }

        await NotificationJobs.RunQueuedAsync(app, id);

        // Every refusal is audited; only the first notifies.
        Assert.Equal(3, (await LifecycleSeed.EventsAsync(app, id, EventTypes.BudgetExceeded)).Count);
        Assert.Single(await NotificationJobs.InAppAsync(app, id), n => n.Type == "budget_exceeded");
        Assert.Single(Test.Http.To(slack));

        // A new budget is a new budget state: its first refusal notifies again, later ones don't.
        var patched = await ProjectApi.PatchAsync(fixture.Owner.Client, fixture.Path, new { tokenBudget = 2_000L });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        for (var i = 0; i < 2; i++)
        {
            await Problems.AssertAsync(await StartRefinementAsync(fixture.Contributor.Client, id), HttpStatusCode.Conflict, ErrorCodes.BudgetExceeded);
        }

        await NotificationJobs.RunQueuedAsync(app, id);

        Assert.Equal(2, (await NotificationJobs.InAppAsync(app, id)).Count(n => n.Type == "budget_exceeded"));
        Assert.Equal(2, Test.Http.To(slack).Count);
    }

    private static Task<HttpResponseMessage> StartRefinementAsync(HttpClient client, Guid projectId) =>
        client.PostAsJsonAsync($"/api/projects/{projectId}/refinement/run", new { }, Ct);

    [Theory]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://10.1.2.3/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[::ffff:127.0.0.1]/hook")]
    public async Task NFR_SEC_Webhook_targets_resolving_to_private_addresses_are_refused_at_send_time(string url)
    {
        // The real SSRF-guarded handler: the base factory has no fake channel endpoints. Nothing leaves the machine.
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id, new { type = "webhook", target = url, secret = "webhook-signing-secret", events = NotificationScenario.Ev("release_failed") });
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            db.AddEvent(EventTypes.ReleaseFailed, id, new { errorCode = "MERGE_CONFLICT" }, SystemActor.Name, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        });

        await NotificationJobs.RunQueuedAsync(factory, id);

        var delivery = Assert.Single(await NotificationJobs.DeliveriesAsync(factory, id));
        Assert.Equal((NotificationDeliveryStatus.Failed, 1), (delivery.Status, delivery.Attempts));
        Assert.Contains("may not reach", delivery.LastError, StringComparison.Ordinal);
        // The in-app notification is unaffected by the channel failure.
        Assert.Single(await NotificationJobs.InAppAsync(factory, id));
    }

    [Fact]
    public async Task NFR_SEC_Slack_webhook_urls_are_never_logged()
    {
        var app = Test.Factory;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var slack = SlackUrl();
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id, new { type = "slack", secret = slack, events = NotificationScenario.Ev("gate_required") });
        await NotificationScenario.PendingSpecGateAsync(app, fixture);
        await NotificationJobs.RunQueuedAsync(app, id);

        Assert.Single(Test.Http.To(slack));
        var secretPart = new Uri(slack).AbsolutePath;
        Assert.DoesNotContain(factory.Logs.Entries, l => l.AllText.Contains(secretPart, StringComparison.Ordinal));
    }
}

/// <summary>Shared setup for notification tests.</summary>
internal static class NotificationScenario
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A string list (channel events, muted types) for request bodies.</summary>
    public static string[] Ev(params string[] values) => values;

    public static async Task ConfigureChannelsAsync(HttpClient owner, Guid projectId, params object[] channels)
    {
        var response = await ProjectApi.PatchAsync(owner, $"/api/projects/{projectId}/settings/notifications", new { channels });
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
    }

    /// <summary>Advisory mode in specification with an approved spec: a writer's advance creates a pending spec_approval gate.</summary>
    public static async Task<GateDto> PendingSpecGateAsync(WebApplicationFactory<Program> app, ProjectFixture fixture)
    {
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Specification, 2, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 2);
        var result = await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));
        Assert.Equal(GateType.SpecApproval, result.Gate!.Type);
        return result.Gate;
    }

    public static Task<List<ApprovalLink>> LinksAsync(WebApplicationFactory<Program> app, Guid gateId) =>
        ProjectApi.WithDbAsync(app, db => db.ApprovalLinks.AsNoTracking().Where(l => l.GateId == gateId).OrderBy(l => l.Decision).ToListAsync(Ct));

    public static Task<ApprovalGate> GateAsync(WebApplicationFactory<Program> app, Guid gateId) =>
        ProjectApi.WithDbAsync(app, db => db.ApprovalGates.AsNoTracking().SingleAsync(g => g.Id == gateId, Ct));
}
