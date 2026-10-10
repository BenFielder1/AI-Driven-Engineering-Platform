using System.ComponentModel.DataAnnotations;
using Aide.Domain.Notifications;

namespace Aide.Api.Features.Notifications;

/// <summary>
/// An in-app notification of the caller (FR-47). <c>projectId</c> and <c>projectName</c> are <c>null</c> for
/// notifications not tied to a project (or whose project was deleted). <c>link</c> is a relative app page path (e.g.
/// <c>/projects/&lt;id&gt;</c>, <c>/projects/&lt;id&gt;/runs</c> or <c>/projects/&lt;id&gt;/artifacts/&lt;type&gt;</c>); it never
/// carries an approval token (one-click approval links go only to project channels). <c>readAt</c> is <c>null</c> while unread.
/// </summary>
public sealed record NotificationDto(
    Guid Id,
    Guid? ProjectId,
    string? ProjectName,
    NotificationType Type,
    string Title,
    string Body,
    string Link,
    DateTimeOffset? ReadAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// A page of the caller's notifications, newest first. <c>unreadCount</c> counts all the caller's unread notifications
/// (not just this page). Load more with <c>before</c> = <c>nextBefore</c>; <c>null</c> on the last page.
/// </summary>
public sealed record NotificationPageDto(IReadOnlyList<NotificationDto> Items, int UnreadCount, string? NextBefore);

/// <summary>The number of the caller's unread notifications (the bell badge).</summary>
public sealed record UnreadNotificationCountDto(int UnreadCount);

/// <summary>
/// The caller's in-app notification preferences (§2): notification types they don't want in their notification centre.
/// Project channels (email, Slack, Teams, webhook) are configured per project by Owners, not here.
/// </summary>
public sealed record NotificationPreferencesDto(IReadOnlyList<NotificationType> MutedTypes);

/// <summary>Replaces the caller's muted notification types. An empty list mutes nothing.</summary>
public sealed record UpdateNotificationPreferencesRequest
{
    [MaxLength(NotificationLimits.MaxMutedTypes)]
    public required IReadOnlyList<NotificationType> MutedTypes { get; init; }
}

/// <summary>Request limits of the notifications feature.</summary>
public static class NotificationLimits
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;
    public const int CursorMaxLength = 200;
    public const int MaxMutedTypes = 11;
}
