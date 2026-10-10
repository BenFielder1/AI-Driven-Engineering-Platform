using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Notifications;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Notifications;
using Aide.IntegrationTests.Support;

namespace Aide.IntegrationTests.Notifications;

/// <summary>FR-47, §2: the caller's notification centre and in-app preferences (<c>/api/me/notifications</c>).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class NotificationEndpointsTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR47_The_notification_centre_lists_newest_first_with_the_unread_count_and_pages_with_before()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var user = fixture.Owner.User!.Id;
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        await SeedAsync(user, fixture.Project.Id, [.. Enumerable.Range(0, 5).Select(i => start.AddSeconds(i))], readFirst: 2);
        await SeedAsync(fixture.Contributor.User!.Id, fixture.Project.Id, [start]);

        var first = await ProjectApi.ReadAsync<NotificationPageDto>(await fixture.Owner.Client.GetAsync("/api/me/notifications?limit=3", Ct));
        Assert.Equal(3, first.Items.Count);
        Assert.Equal(3, first.UnreadCount);
        Assert.Equal(["n4", "n3", "n2"], first.Items.Select(n => n.Title));
        Assert.Equal(fixture.Project.Name, first.Items[0].ProjectName);
        Assert.Equal(NotificationType.GateRequired, first.Items[0].Type);
        Assert.NotNull(first.NextBefore);

        var second = await ProjectApi.ReadAsync<NotificationPageDto>(await fixture.Owner.Client.GetAsync($"/api/me/notifications?limit=3&before={first.NextBefore}", Ct));
        Assert.Equal(["n1", "n0"], second.Items.Select(n => n.Title));
        Assert.Null(second.NextBefore);

        var unread = await ProjectApi.ReadAsync<NotificationPageDto>(await fixture.Owner.Client.GetAsync("/api/me/notifications?unreadOnly=true", Ct));
        Assert.Equal(["n4", "n3", "n2"], unread.Items.Select(n => n.Title));

        var count = await ProjectApi.ReadAsync<UnreadNotificationCountDto>(await fixture.Owner.Client.GetAsync("/api/me/notifications/unread-count", Ct));
        Assert.Equal(3, count.UnreadCount);
    }

    [Fact]
    public async Task FR47_A_bad_cursor_or_limit_is_a_validation_error()
    {
        using var session = await ApiSession.RegisterAsync(factory);
        await Problems.AssertAsync(await session.Client.GetAsync("/api/me/notifications?before=nope", Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await session.Client.GetAsync("/api/me/notifications?limit=0", Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR47_Marking_read_is_per_user_and_idempotent_and_read_all_clears_the_count()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var owner = fixture.Owner.User!.Id;
        var ids = await SeedAsync(owner, fixture.Project.Id, [DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1)]);
        var others = await SeedAsync(fixture.Contributor.User!.Id, fixture.Project.Id, [DateTimeOffset.UtcNow]);

        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Owner.Client.PatchAsync($"/api/me/notifications/{ids[0]}/read", null, Ct)).StatusCode);
        var firstRead = await ProjectApi.WithDbAsync(factory, db => Task.FromResult(db.Notifications.Single(n => n.Id == ids[0]).ReadAt));
        Assert.NotNull(firstRead);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Owner.Client.PatchAsync($"/api/me/notifications/{ids[0]}/read", null, Ct)).StatusCode);
        Assert.Equal(firstRead, await ProjectApi.WithDbAsync(factory, db => Task.FromResult(db.Notifications.Single(n => n.Id == ids[0]).ReadAt)));

        // Another user's notification is not found.
        await Problems.AssertAsync(await fixture.Owner.Client.PatchAsync($"/api/me/notifications/{others[0]}/read", null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);

        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Owner.Client.PostAsync("/api/me/notifications/read-all", null, Ct)).StatusCode);
        Assert.Equal(0, (await ProjectApi.ReadAsync<UnreadNotificationCountDto>(await fixture.Owner.Client.GetAsync("/api/me/notifications/unread-count", Ct))).UnreadCount);
        Assert.Equal(1, (await ProjectApi.ReadAsync<UnreadNotificationCountDto>(await fixture.Contributor.Client.GetAsync("/api/me/notifications/unread-count", Ct))).UnreadCount);
    }

    [Fact]
    public async Task S2_Notification_preferences_round_trip_and_reject_unknown_types()
    {
        using var session = await ApiSession.RegisterAsync(factory);

        var initial = await ProjectApi.ReadAsync<NotificationPreferencesDto>(await session.Client.GetAsync("/api/me/notification-preferences", Ct));
        Assert.Empty(initial.MutedTypes);

        var updated = await ProjectApi.ReadAsync<NotificationPreferencesDto>(await session.Client.PutAsJsonAsync(
            "/api/me/notification-preferences", new { mutedTypes = NotificationScenario.Ev("review_completed", "gate_reminder", "review_completed") }, Ct));
        Assert.Equal([NotificationType.GateReminder, NotificationType.ReviewCompleted], updated.MutedTypes);
        var read = await ProjectApi.ReadAsync<NotificationPreferencesDto>(await session.Client.GetAsync("/api/me/notification-preferences", Ct));
        Assert.Equal(updated.MutedTypes, read.MutedTypes);

        var cleared = await ProjectApi.ReadAsync<NotificationPreferencesDto>(await session.Client.PutAsJsonAsync("/api/me/notification-preferences", new { mutedTypes = Array.Empty<string>() }, Ct));
        Assert.Empty(cleared.MutedTypes);

        var bad = await session.Client.PutAsJsonAsync("/api/me/notification-preferences", new { mutedTypes = NotificationScenario.Ev("nope") }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task FR47_The_notification_centre_needs_a_signed_in_user()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me/notifications", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me/notification-preferences", Ct)).StatusCode);
    }

    private Task<List<Guid>> SeedAsync(Guid userId, Guid projectId, IReadOnlyList<DateTimeOffset> times, int readFirst = 0) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var rows = times.Select((t, i) => new Notification
            {
                UserId = userId,
                ProjectId = projectId,
                Type = NotificationType.GateRequired.ToWire(),
                Title = $"n{i}",
                Body = "body",
                Link = $"/projects/{projectId}",
                CreatedAt = t,
                ReadAt = i < readFirst ? t : null,
            }).ToList();
            db.Notifications.AddRange(rows);
            await db.SaveChangesAsync(Ct);
            return rows.Select(r => r.Id).ToList();
        });
}
