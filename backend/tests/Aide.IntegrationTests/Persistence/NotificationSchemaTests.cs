using Aide.Domain.Access;
using Aide.Domain.Entities;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aide.IntegrationTests.Persistence;

/// <summary>Notification persistence (NotificationDetails migration): preferences, per-user dedupe key, delivery backoff.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class NotificationSchemaTests(AideApiFactory factory)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR47_muted_types_round_trip_as_snake_case_text_array()
    {
        var user = NewUser();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            db.Users.Add(user);
            db.UserNotificationPreferences.Add(new UserNotificationPreferences
            {
                UserId = user.Id,
                MutedTypes = [NotificationType.GateReminder, NotificationType.ArtifactReady],
                UpdatedAt = Now,
            });
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            var prefs = await db.UserNotificationPreferences.SingleAsync(p => p.UserId == user.Id, Ct);
            Assert.Equal([NotificationType.GateReminder, NotificationType.ArtifactReady], prefs.MutedTypes);

            var raw = await db.Database
                .SqlQuery<string>($"SELECT array_to_string(muted_types, ',') AS \"Value\" FROM user_notification_preferences WHERE user_id = {user.Id}")
                .SingleAsync(Ct);
            Assert.Equal("gate_reminder,artifact_ready", raw);

            // Change tracking sees in-place list edits.
            prefs.MutedTypes.Add(NotificationType.Rollback);
            await db.SaveChangesAsync(Ct);
            db.ChangeTracker.Clear();
            Assert.Contains(NotificationType.Rollback, (await db.UserNotificationPreferences.SingleAsync(p => p.UserId == user.Id, Ct)).MutedTypes);

            // Deleting the user cascades to their preferences.
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == user.Id, Ct));
            await db.SaveChangesAsync(Ct);
            Assert.False(await db.UserNotificationPreferences.AnyAsync(p => p.UserId == user.Id, Ct));
        }
    }

    [Fact]
    public async Task FR49_notification_key_is_unique_per_user_when_set()
    {
        var user = NewUser();
        var other = NewUser();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        db.Users.AddRange(user, other);
        db.Notifications.AddRange(
            NewNotification(user.Id, "gate_required:1"),
            NewNotification(other.Id, "gate_required:1"),
            NewNotification(user.Id, null),
            NewNotification(user.Id, null));
        await db.SaveChangesAsync(Ct);

        db.ChangeTracker.Clear();
        db.Notifications.Add(NewNotification(user.Id, "gate_required:1"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
        Assert.Equal("ux_notifications_user_id_notification_key", pg.ConstraintName);
    }

    [Fact]
    public async Task FR49_delivery_next_attempt_at_round_trips()
    {
        var delivery = new NotificationDelivery
        {
            NotificationKey = $"run_failed:{Guid.NewGuid()}",
            Channel = "slack",
            Status = NotificationDeliveryStatus.Pending,
            Attempts = 2,
            LastError = "HTTP 500",
            NextAttemptAt = Now.AddMinutes(4),
            CreatedAt = Now,
        };
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            db.NotificationDeliveries.Add(delivery);
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            var loaded = await db.NotificationDeliveries.SingleAsync(d => d.Id == delivery.Id, Ct);
            Assert.Equal(Now.AddMinutes(4), loaded.NextAttemptAt);
        }
    }

    private static Notification NewNotification(Guid userId, string? key) => new()
    {
        UserId = userId,
        Type = "gate_required",
        Title = "Gate required",
        Body = "Spec approval is pending.",
        Link = "/projects/x/gates",
        NotificationKey = key,
        CreatedAt = Now,
    };

    private static AppUser NewUser()
    {
        var email = $"user-{Guid.NewGuid():N}@example.com";
        return new AppUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            Name = "Test User",
            SystemRole = SystemRole.User,
            CreatedAt = Now,
        };
    }
}
