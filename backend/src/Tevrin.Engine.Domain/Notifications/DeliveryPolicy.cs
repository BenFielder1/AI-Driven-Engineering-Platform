using Aide.Domain.Entities;

namespace Aide.Domain.Notifications;

/// <summary>How one delivery attempt to a channel ended.</summary>
public enum DeliveryAttemptResult
{
    Sent = 0,

    /// <summary>Worth retrying: timeout, connection error, 5xx, 429.</summary>
    TransientFailure = 1,

    /// <summary>A retry can't fix it: 4xx, refused target address, missing channel secret, redirect.</summary>
    PermanentFailure = 2,

    /// <summary>The channel can't send in this deployment (email without SMTP settings).</summary>
    Skipped = 3,
}

/// <summary>The state a delivery row moves to after an attempt.</summary>
public sealed record DeliveryNextState(NotificationDeliveryStatus Status, DateTimeOffset? NextAttemptAt);

/// <summary>
/// FR-49: per-channel retry with backoff. Each (notification, channel) pair is its own delivery: a channel that succeeded is
/// never sent again, a failing one is retried after 1 min, 5 min, 30 min and 2 h, and fails after <see cref="MaxAttempts"/>.
/// </summary>
public static class DeliveryPolicy
{
    public const int MaxAttempts = 5;

    /// <summary>
    /// A claimed attempt holds the delivery for this long (its <c>next_attempt_at</c> moves this far ahead), so a second
    /// dispatcher doesn't send it at the same time; an attempt cut short by a crash is retried after it.
    /// </summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The longest one channel send may take. Configured channel timeouts (SMTP, HTTP) are validated against it at start,
    /// and the dispatcher cancels any send that runs past it, so an attempt always ends well before its
    /// <see cref="ClaimLease"/> runs out and another dispatcher could claim and re-send it.
    /// </summary>
    public static readonly TimeSpan MaxSendTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Whether a configured channel timeout is usable: positive and at most <see cref="MaxSendTimeout"/>.</summary>
    public static bool IsValidSendTimeout(TimeSpan timeout) => timeout > TimeSpan.Zero && timeout <= MaxSendTimeout;

    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
    ];

    /// <summary>The wait before attempt <paramref name="attemptsSoFar"/> + 1 (1-based attempts already made).</summary>
    public static TimeSpan BackoffAfter(int attemptsSoFar) =>
        Backoff[Math.Clamp(attemptsSoFar, 1, Backoff.Length) - 1];

    /// <summary>The delivery's state after its <paramref name="attempts"/>-th attempt ended with <paramref name="result"/>.</summary>
    public static DeliveryNextState After(DeliveryAttemptResult result, int attempts, DateTimeOffset now) => result switch
    {
        DeliveryAttemptResult.Sent => new(NotificationDeliveryStatus.Sent, null),
        DeliveryAttemptResult.Skipped => new(NotificationDeliveryStatus.Skipped, null),
        DeliveryAttemptResult.TransientFailure when attempts < MaxAttempts => new(NotificationDeliveryStatus.Pending, now + BackoffAfter(attempts)),
        _ => new(NotificationDeliveryStatus.Failed, null),
    };

    /// <summary>Classifies an HTTP response status of a webhook-style channel.</summary>
    public static DeliveryAttemptResult ForHttpStatus(int status) => status switch
    {
        >= 200 and < 300 => DeliveryAttemptResult.Sent,
        408 or 425 or 429 => DeliveryAttemptResult.TransientFailure,
        >= 500 => DeliveryAttemptResult.TransientFailure,
        _ => DeliveryAttemptResult.PermanentFailure,
    };
}
