using Aide.Domain.Common;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aide.IntegrationTests.Notifications;

/// <summary>FR-15: reminders after 24 hours, overdue after 72 hours, nothing decided automatically.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class GateTimerTests(AideApiFactory factory)
{
    private WebApplicationFactory<Program> App => NotificationTestApp.For(factory).Factory;

    [Fact]
    public async Task FR15_A_gate_pending_for_more_than_24_hours_gets_one_reminder()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var id = fixture.Project.Id;
        var gate = await LifecycleSeed.AddGateAsync(App, id, GateType.PreRelease, createdAt: DateTimeOffset.UtcNow.AddHours(-25));

        await NotificationJobs.RunGateTimersAsync(App);
        await NotificationJobs.RunGateTimersAsync(App);

        var stored = await NotificationScenario.GateAsync(App, gate.Id);
        Assert.NotNull(stored.RemindedAt);
        Assert.False(stored.Overdue);
        var job = Assert.Single(await LifecycleSeed.NotificationJobsAsync(App, id));
        Assert.Equal(new NotificationJobPayload(id, NotificationType.GateReminder, gate.Id), AideJson.Deserialize<NotificationJobPayload>(job.Payload));

        await NotificationJobs.RunQueuedAsync(App, id);
        var reminder = Assert.Single(await NotificationJobs.InAppAsync(App, id));
        Assert.Equal((fixture.Owner.User!.Id, "gate_reminder"), (reminder.UserId, reminder.Type));
        Assert.StartsWith("Reminder", reminder.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR15_A_gate_pending_for_more_than_72_hours_is_overdue_with_an_event_and_a_notification_and_stays_pending()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var id = fixture.Project.Id;
        var gate = await LifecycleSeed.AddGateAsync(App, id, GateType.SpecApproval, createdAt: DateTimeOffset.UtcNow.AddHours(-73));
        var phase = (await LifecycleSeed.ProjectAsync(App, id)).CurrentPhase;

        await NotificationJobs.RunGateTimersAsync(App);
        await NotificationJobs.RunGateTimersAsync(App);

        var stored = await NotificationScenario.GateAsync(App, gate.Id);
        Assert.True(stored.Overdue);
        Assert.Equal(GateStatus.Pending, stored.Status);
        Assert.Null(stored.DecidedAt);
        Assert.Equal(phase, (await LifecycleSeed.ProjectAsync(App, id)).CurrentPhase);
        Assert.Single(await LifecycleSeed.EventsAsync(App, id, EventTypes.GateOverdue));

        var job = Assert.Single(await LifecycleSeed.NotificationJobsAsync(App, id));
        Assert.Equal(new NotificationJobPayload(id, NotificationType.GateOverdue, gate.Id), AideJson.Deserialize<NotificationJobPayload>(job.Payload));
        await NotificationJobs.RunQueuedAsync(App, id);
        var overdue = Assert.Single(await NotificationJobs.InAppAsync(App, id));
        Assert.Equal("gate_overdue", overdue.Type);
    }

    [Fact]
    public async Task FR15_Many_already_reminded_gates_dont_starve_a_newer_gate_of_its_reminder()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var id = fixture.Project.Id;
        var now = DateTimeOffset.UtcNow;
        // More reminded-but-not-yet-overdue gates than one sweep batch (200), all older than the gate that needs a reminder
        // (one pending gate per project and type, so each sits in its own throwaway project).
        var fillers = await ProjectApi.WithDbAsync(App, async db =>
        {
            var projects = Enumerable.Range(0, 250)
                .Select(_ => new Aide.Domain.Entities.Project { Name = "Filler", Slug = $"filler-{Guid.NewGuid():N}", Idea = "Idea", CreatedAt = now, UpdatedAt = now })
                .ToList();
            db.Projects.AddRange(projects);
            db.ApprovalGates.AddRange(projects.Select((p, i) => new Aide.Domain.Entities.ApprovalGate
            {
                ProjectId = p.Id,
                Type = GateType.PreRelease,
                CreatedAt = now.AddHours(-60).AddSeconds(-i),
                RemindedAt = now.AddHours(-30),
            }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return projects.Select(p => p.Id).ToList();
        });
        var fresh = await LifecycleSeed.AddGateAsync(App, id, GateType.PlanApproval, createdAt: now.AddHours(-25));

        try
        {
            await NotificationJobs.RunGateTimersAsync(App);

            Assert.NotNull((await NotificationScenario.GateAsync(App, fresh.Id)).RemindedAt);
            var job = Assert.Single(await LifecycleSeed.NotificationJobsAsync(App, id));
            Assert.Equal(new NotificationJobPayload(id, NotificationType.GateReminder, fresh.Id), AideJson.Deserialize<NotificationJobPayload>(job.Payload));
        }
        finally
        {
            // Leave nothing pending for other tests' sweeps.
            await ProjectApi.WithDbAsync(App, db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteDeleteAsync(
                System.Linq.Queryable.Where(db.Projects, p => fillers.Contains(p.Id)), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task FR15_Recent_and_decided_gates_are_left_alone()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var id = fixture.Project.Id;
        var recent = await LifecycleSeed.AddGateAsync(App, id, GateType.PlanApproval, createdAt: DateTimeOffset.UtcNow.AddHours(-23));
        var decided = await LifecycleSeed.AddGateAsync(App, id, GateType.PreRelease, createdAt: DateTimeOffset.UtcNow.AddHours(-100));
        await ProjectApi.WithDbAsync(App, async db =>
        {
            var gate = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.ApprovalGates, g => g.Id == decided.Id, TestContext.Current.CancellationToken);
            gate.Status = GateStatus.Approved;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        await NotificationJobs.RunGateTimersAsync(App);

        Assert.Null((await NotificationScenario.GateAsync(App, recent.Id)).RemindedAt);
        var stored = await NotificationScenario.GateAsync(App, decided.Id);
        Assert.Equal((false, (DateTimeOffset?)null), (stored.Overdue, stored.RemindedAt));
        Assert.Empty(await LifecycleSeed.NotificationJobsAsync(App, id));
        Assert.Equal(Phase.Refinement, (await LifecycleSeed.ProjectAsync(App, id)).CurrentPhase);
    }
}
