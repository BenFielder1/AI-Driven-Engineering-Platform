using Aide.Domain.Notifications;

namespace Aide.Domain.Entities;

/// <summary>A user's in-app notification preferences (§2, FR-47): the notification types muted in their notification centre.</summary>
public sealed class UserNotificationPreferences
{
    public Guid UserId { get; set; }

    /// <summary>Muted types, stored as a <c>text[]</c> of snake_case wire values. Empty mutes nothing.</summary>
    public List<NotificationType> MutedTypes { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}
