namespace Tevrin.Engine.Domain.Entities;

/// <summary>
/// FR-336, §5, §4.71: a project's schedule for a pipeline that declares <c>trigger: { kind: schedule }</c>
/// (<c>pipeline_schedules</c>). The engine job <c>trigger.fire</c> starts a run at each due time. It belongs to the project
/// and goes with it.
/// </summary>
public sealed class PipelineSchedule
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid ProjectId { get; init; }

    public string PipelineId { get; init; } = string.Empty;

    /// <summary>Five fields: minute, hour, day of month, month, day of week (§4.71).</summary>
    public string Cron { get; set; } = string.Empty;

    /// <summary>The IANA timezone the cron is evaluated in.</summary>
    public string Timezone { get; set; } = string.Empty;

    /// <summary>The inputs each fire starts its run with (JSON object, artifact key → value); <c>{}</c> for none.</summary>
    public string Inputs { get; set; } = "{}";

    public bool Paused { get; set; }

    /// <summary>The next due time (UTC).</summary>
    public DateTimeOffset NextFireAt { get; set; }

    public DateTimeOffset? LastFireAt { get; set; }

    /// <summary>The user who created the schedule; <c>null</c> once that user is deleted.</summary>
    public Guid? CreatedBy { get; init; }
}

/// <summary>
/// FR-337, §5, §4.72: a project's webhook for a pipeline that declares <c>trigger: { kind: webhook, input }</c>
/// (<c>pipeline_webhooks</c>). The secrets are stored encrypted (<c>ISecretProtector</c>) and never returned after creation.
/// A rotated secret stays valid until <see cref="PreviousSecretExpiresAt"/> (24 hours).
/// </summary>
public sealed class PipelineWebhook
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid ProjectId { get; init; }

    public string PipelineId { get; init; } = string.Empty;

    /// <summary>The current signing secret, encrypted.</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>The secret before the last rotation, encrypted; <c>null</c> when never rotated.</summary>
    public string? PreviousSecret { get; set; }

    public DateTimeOffset? PreviousSecretExpiresAt { get; set; }

    /// <summary>The user who created the webhook; <c>null</c> once that user is deleted.</summary>
    public Guid? CreatedBy { get; init; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>): rotations race.</summary>
    public uint RowVersion { get; set; }
}

/// <summary>
/// FR-337, §5, §4.72: one received webhook call (<c>webhook_deliveries</c>), keyed by (<see cref="WebhookId"/>,
/// <see cref="DeliveryId"/>, its <c>X-Tevrin-Delivery</c> id). Kept for 30 days. The same pair seen within 24 hours is a
/// replay (<see cref="WebhookDeliveryRules.IsReplay"/>); an older row of the pair is reused (received_at, status and run
/// updated).
/// </summary>
public sealed class WebhookDelivery
{
    public Guid DeliveryId { get; init; }

    public Guid WebhookId { get; init; }

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>What happened to the call (<see cref="WebhookDeliveryStatuses"/>).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The run the call started or submitted to; <c>null</c> when it was refused.</summary>
    public Guid? RunId { get; set; }
}

/// <summary>FR-337: the stored <see cref="WebhookDelivery.Status"/> values.</summary>
public static class WebhookDeliveryStatuses
{
    /// <summary>A new run was started with the body as its input.</summary>
    public const string Started = "started";

    /// <summary>The body was submitted to the active run (<c>on_active: submit</c>).</summary>
    public const string Submitted = "submitted";

    /// <summary>Refused: the body failed its schema, or a run was active and the trigger says <c>skip</c>.</summary>
    public const string Rejected = "rejected";

    /// <summary>Received and being handled (the claim, before one of the outcomes above).</summary>
    public const string Received = "received";
}

/// <summary>FR-337, §4.72, §5: pure rules for webhook deliveries.</summary>
public static class WebhookDeliveryRules
{
    /// <summary>How long a delivery is kept (§5).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>How long a delivery id counts as seen (§4.72).</summary>
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// A call is a replay when the same (webhook, delivery id) was last received within <see cref="ReplayWindow"/> of
    /// <paramref name="now"/> (<paramref name="lastReceivedAt"/> <c>null</c>: never received).
    /// </summary>
    public static bool IsReplay(DateTimeOffset? lastReceivedAt, DateTimeOffset now) =>
        lastReceivedAt is { } last && now - last < ReplayWindow;
}
