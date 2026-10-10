using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Aide.Api.Errors;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aide.Api.Features.Notifications;

/// <summary>
/// FR-47, §2: the caller's in-app notification centre (§6 <c>/api/me/notifications</c>) and in-app notification
/// preferences. Every route is scoped to the signed-in user; another user's notification is 404.
/// </summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me")
            .WithTags("Notifications")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/notifications", ListMyNotifications)
            .WithName("ListMyNotifications")
            .WithSummary("The caller's notifications, newest first, with the total unread count. unreadOnly=true lists unread ones only; load more with before = the previous page's nextBefore. limit defaults to 50 (max 100).")
            .Produces<NotificationPageDto>()
            .ProducesValidationProblem()
            .WithResponseDescriptions((StatusCodes.Status400BadRequest, "VALIDATION: limit out of range or before is not a nextBefore value."));

        group.MapGet("/notifications/unread-count", GetUnreadNotificationCount)
            .WithName("GetUnreadNotificationCount")
            .WithSummary("The number of the caller's unread notifications.")
            .Produces<UnreadNotificationCountDto>();

        group.MapPatch("/notifications/{id:guid}/read", MarkNotificationRead)
            .WithName("MarkNotificationRead")
            .WithSummary("Mark one of the caller's notifications as read (idempotent: an already read notification keeps its readAt).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions(
                (StatusCodes.Status204NoContent, "The notification is read."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: no such notification for the caller."));

        group.MapPost("/notifications/read-all", MarkAllNotificationsRead)
            .WithName("MarkAllNotificationsRead")
            .WithSummary("Mark all the caller's unread notifications as read.")
            .Produces(StatusCodes.Status204NoContent)
            .WithResponseDescriptions((StatusCodes.Status204NoContent, "Every notification of the caller is read."));

        group.MapGet("/notification-preferences", GetNotificationPreferences)
            .WithName("GetNotificationPreferences")
            .WithSummary("The caller's in-app notification preferences (muted types). Project channels are project settings.")
            .Produces<NotificationPreferencesDto>();

        group.MapPut("/notification-preferences", UpdateNotificationPreferences)
            .WithName("UpdateNotificationPreferences")
            .WithSummary("Replace the caller's muted notification types. Muted types are not added to the caller's notification centre; already created notifications are kept.")
            .Produces<NotificationPreferencesDto>()
            .ProducesValidationProblem()
            .WithResponseDescriptions((StatusCodes.Status400BadRequest, "VALIDATION: an unknown type or too many entries."));

        return app;
    }

    private static async Task<Results<Ok<NotificationPageDto>, ValidationProblem, ProblemHttpResult>> ListMyNotifications(
        ICurrentUser currentUser,
        AideDbContext db,
        CancellationToken cancellationToken,
        bool unreadOnly = false,
        [MaxLength(NotificationLimits.CursorMaxLength)] string? before = null,
        [Range(1, NotificationLimits.MaxLimit)] int limit = NotificationLimits.DefaultLimit)
    {
        var userId = currentUser.RequiredUserId;
        NotificationCursor? cursor = null;
        if (before is not null)
        {
            if (NotificationCursor.TryDecode(before) is not { } decoded)
            {
                return ApiProblems.Validation(new Dictionary<string, string[]>
                {
                    ["before"] = ["before must be a nextBefore value from a previous page."],
                });
            }

            cursor = decoded;
        }

        var query = cursor is { } c
            ? db.Notifications.FromSql($"SELECT * FROM notifications WHERE user_id = {userId} AND (created_at, id) < ({c.CreatedAt}, {c.Id})")
            : db.Notifications.Where(n => n.UserId == userId);
        query = query.AsNoTracking();
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        var rows = await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var page = rows.Take(limit).ToList();
        var projectIds = page.Where(n => n.ProjectId is not null).Select(n => n.ProjectId!.Value).Distinct().ToList();
        var names = await db.Projects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        var items = page
            .Where(n => EnumStrings.TryParse<NotificationType>(n.Type, out _))
            .Select(n => new NotificationDto(
                n.Id,
                n.ProjectId,
                n.ProjectId is { } pid ? names.GetValueOrDefault(pid) : null,
                EnumStrings.Parse<NotificationType>(n.Type),
                n.Title,
                n.Body,
                n.Link,
                n.ReadAt,
                n.CreatedAt))
            .ToList();
        var unread = await UnreadCountAsync(db, userId, cancellationToken);
        var next = rows.Count > limit ? new NotificationCursor(page[^1].CreatedAt, page[^1].Id).Encode() : null;
        return TypedResults.Ok(new NotificationPageDto(items, unread, next));
    }

    private static async Task<Results<Ok<UnreadNotificationCountDto>, ProblemHttpResult>> GetUnreadNotificationCount(
        ICurrentUser currentUser,
        AideDbContext db,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(new UnreadNotificationCountDto(await UnreadCountAsync(db, currentUser.RequiredUserId, cancellationToken)));

    private static async Task<Results<NoContent, ProblemHttpResult>> MarkNotificationRead(
        Guid id,
        ICurrentUser currentUser,
        AideDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequiredUserId;
        DateTimeOffset? now = time.GetUtcNow();
        var updated = await db.Notifications
            .Where(n => n.Id == id && n.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, n => n.ReadAt ?? now), cancellationToken);
        return updated == 0 ? ApiProblems.NotFound("The notification was not found.") : TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> MarkAllNotificationsRead(
        ICurrentUser currentUser,
        AideDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequiredUserId;
        DateTimeOffset? now = time.GetUtcNow();
        await db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<NotificationPreferencesDto>, ProblemHttpResult>> GetNotificationPreferences(
        ICurrentUser currentUser,
        AideDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequiredUserId;
        var preferences = await db.UserNotificationPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        return TypedResults.Ok(new NotificationPreferencesDto(Ordered(preferences?.MutedTypes ?? [])));
    }

    private static async Task<Results<Ok<NotificationPreferencesDto>, ValidationProblem, ProblemHttpResult>> UpdateNotificationPreferences(
        UpdateNotificationPreferencesRequest request,
        ICurrentUser currentUser,
        AideDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MutedTypes.Any(t => !Enum.IsDefined(t)))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]>
            {
                ["mutedTypes"] = ["Unknown notification type."],
            });
        }

        var userId = currentUser.RequiredUserId;
        var muted = Ordered(request.MutedTypes);
        var now = time.GetUtcNow();
        var preferences = await db.UserNotificationPreferences.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (preferences is null)
        {
            db.UserNotificationPreferences.Add(new UserNotificationPreferences { UserId = userId, MutedTypes = [.. muted], UpdatedAt = now });
        }
        else
        {
            preferences.MutedTypes = [.. muted];
            preferences.UpdatedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent first save of the same user's preferences: last writer wins.
            db.ChangeTracker.Clear();
            var saved = await db.UserNotificationPreferences.FirstAsync(p => p.UserId == userId, cancellationToken);
            saved.MutedTypes = [.. muted];
            saved.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(new NotificationPreferencesDto(muted));
    }

    private static Task<int> UnreadCountAsync(AideDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken);

    private static List<NotificationType> Ordered(IEnumerable<NotificationType> types) => [.. types.Distinct().Order()];
}

/// <summary>Opaque keyset cursor: base64url of "&lt;UTC ticks&gt;.&lt;notification id&gt;".</summary>
internal sealed record NotificationCursor(DateTimeOffset CreatedAt, Guid Id)
{
    public string Encode() =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}.{Id:N}"));

    public static NotificationCursor? TryDecode(string value)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
            var parts = text.Split('.');
            return parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks
                && Guid.TryParseExact(parts[1], "N", out var id)
                    ? new NotificationCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id)
                    : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
