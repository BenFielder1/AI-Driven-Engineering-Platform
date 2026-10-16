using Aide.Domain.Common;
using Aide.Domain.Gates;

namespace Aide.Domain.Notifications;

/// <summary>Who receives an in-app notification of a project (FR-47).</summary>
public enum RecipientScope
{
    /// <summary>The project's Owners: they decide gates, approve artifacts and act on failures.</summary>
    Owners = 0,

    /// <summary>Every member (Owner, Contributor, Viewer): outcomes everyone on the project wants to know about.</summary>
    AllMembers = 1,
}

/// <summary>
/// Payload of a <c>notification</c> job (FR-47, FR-15): the logical notification <c>&lt;type&gt;:&lt;sourceId&gt;</c> of a
/// project. The source is the gate for gate notifications (required, reminder, overdue) and the audit event otherwise.
/// </summary>
public sealed record NotificationJobPayload(Guid ProjectId, NotificationType Type, Guid SourceId);

/// <summary>FR-47, FR-15, §2: which events notify, whom, through which channel subscription, and under which key.</summary>
public static class NotificationRules
{
    /// <summary>The notification an audit event raises, or <c>null</c> when it raises none.</summary>
    public static NotificationType? ForEvent(string eventType) => eventType switch
    {
        EventTypes.GateCreated => NotificationType.GateRequired,
        EventTypes.GateOverdue => NotificationType.GateOverdue,
        EventTypes.ArtifactCreated or EventTypes.ArtifactRestored => NotificationType.ArtifactReady,
        EventTypes.StepBlocked => NotificationType.StepBlocked,
        EventTypes.RunFailed => NotificationType.RunFailed,
        EventTypes.BudgetExceeded => NotificationType.BudgetExceeded,
        EventTypes.ReviewCompleted => NotificationType.ReviewCompleted,
        EventTypes.ReleaseCompleted => NotificationType.ReleaseCompleted,
        EventTypes.ReleaseFailed => NotificationType.ReleaseFailed,
        EventTypes.RollbackCompleted => NotificationType.Rollback,
        _ => null,
    };

    /// <summary>Gate notifications are keyed by the gate (one per gate), the others by the event that raised them.</summary>
    public static bool IsAboutGate(NotificationType type) =>
        type is NotificationType.GateRequired or NotificationType.GateReminder or NotificationType.GateOverdue;

    /// <summary>
    /// Recipients: Owners for what needs an Owner's action (gates, drafts to approve, blocked steps, failed runs, budget,
    /// failed releases); every member for outcomes (review completed, release completed, rollback).
    /// </summary>
    public static RecipientScope Recipients(NotificationType type) => type switch
    {
        NotificationType.ReviewCompleted or NotificationType.ReleaseCompleted or NotificationType.Rollback => RecipientScope.AllMembers,
        _ => RecipientScope.Owners,
    };

    /// <summary>
    /// The channel event (a project channel's <c>events</c> entry) that subscribes to <paramref name="type"/>. Channels have no
    /// separate reminder event: a gate reminder goes to the channels subscribed to <c>gate_required</c>.
    /// </summary>
    public static string ChannelEvent(NotificationType type) =>
        type == NotificationType.GateReminder ? NotificationType.GateRequired.ToWire() : type.ToWire();

    /// <summary>The idempotency key of a logical notification: <c>&lt;type&gt;:&lt;source id&gt;</c>.</summary>
    public static string Key(NotificationType type, Guid sourceId) => $"{type.ToWire()}:{sourceId:D}";

    public static bool TryParseKey(string? key, out NotificationType type, out Guid sourceId)
    {
        type = default;
        sourceId = Guid.Empty;
        var separator = key?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        return separator > 0
            && EnumStrings.TryParse(key![..separator], out type)
            && Guid.TryParseExact(key[(separator + 1)..], "D", out sourceId);
    }

    /// <summary>Whether the user's preferences (§2) let <paramref name="type"/> into their notification centre.</summary>
    public static bool Wants(NotificationType type, IReadOnlyCollection<NotificationType>? mutedTypes) =>
        mutedTypes is null || !mutedTypes.Contains(type);

    /// <summary>
    /// FR-48: one-click approve/reject links exist only for spec, plan and pre-release gates. An escalation needs a decision
    /// (continue, rollback with a target, accept), so its notification links to the project page.
    /// </summary>
    public static bool OffersApprovalLinks(GateType type) =>
        type is GateType.SpecApproval or GateType.PlanApproval or GateType.PreRelease;

    /// <summary>The app page a notification opens (a relative path; never an approval token).</summary>
    public static string LinkPath(NotificationType type, Guid projectId, string? artifactType = null) => type switch
    {
        NotificationType.ArtifactReady when !string.IsNullOrEmpty(artifactType) => $"/projects/{projectId:D}/artifacts/{Uri.EscapeDataString(artifactType)}",
        NotificationType.StepBlocked => $"/projects/{projectId:D}/execution",
        NotificationType.RunFailed or NotificationType.BudgetExceeded => $"/projects/{projectId:D}/runs",
        NotificationType.ReviewCompleted => $"/projects/{projectId:D}/review",
        _ => $"/projects/{projectId:D}",
    };

    /// <summary>
    /// Budget notifications are deduplicated per budget state: every refused start writes a <c>BUDGET_EXCEEDED</c> event
    /// (the audit trail), but only the first refusal since the project's token budget was last set notifies. A refusal
    /// notifies when there is no earlier refusal (<paramref name="previousRefusalAt"/> is <c>null</c>) or the budget was
    /// changed (raised, lowered or cleared) at or after the previous refusal. Used tokens are never reset, so the budget
    /// value is the only thing that changes the budget state.
    /// </summary>
    public static bool NotifiesBudgetExceeded(DateTimeOffset? previousRefusalAt, IEnumerable<DateTimeOffset> budgetChangesAt)
    {
        ArgumentNullException.ThrowIfNull(budgetChangesAt);
        return previousRefusalAt is not { } previous || budgetChangesAt.Any(changedAt => changedAt >= previous);
    }

    /// <summary>The notification types, for validating preference updates.</summary>
    public static IReadOnlyList<NotificationType> All { get; } = Enum.GetValues<NotificationType>();
}
