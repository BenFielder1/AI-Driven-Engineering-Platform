namespace Aide.Domain.Entities;

/// <summary>An in-app notification (FR-47).</summary>
public sealed class Notification
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public Guid? ProjectId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Link { get; set; } = string.Empty;

    public DateTimeOffset? ReadAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Per-channel delivery record, unique per (notification key, channel) (FR-49).</summary>
public sealed class NotificationDelivery
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid? ProjectId { get; set; }

    /// <summary>Idempotency key of the logical notification (e.g. <c>gate_required:&lt;gate id&gt;</c>).</summary>
    public string NotificationKey { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    public NotificationDeliveryStatus Status { get; set; } = NotificationDeliveryStatus.Pending;

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>A single-use one-click gate decision link (FR-48, D14). Only the nonce hash is stored.</summary>
public sealed class ApprovalLink
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid GateId { get; set; }

    public ApprovalLinkDecision Decision { get; set; }

    public string NonceHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
