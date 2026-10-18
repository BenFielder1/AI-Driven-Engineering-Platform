using Aide.Api.Features.Approvals;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aide.Api.Features.Notifications;

/// <summary>
/// FR-49: sends due channel deliveries. Each <c>notification_deliveries</c> row is one (notification, channel) pair with its
/// own attempts and backoff (<see cref="DeliveryPolicy"/>), so a channel that succeeded is never sent again when another
/// fails. A row is claimed with a conditional update (attempts + 1, <c>next_attempt_at</c> pushed past the claim lease)
/// before sending, so two dispatchers (the job and the sweep, or two pods) never send it at the same time.
/// </summary>
public sealed partial class NotificationDispatcher(
    AideDbContext db,
    NotificationComposer composer,
    ApprovalLinkService approvalLinks,
    IEnumerable<INotificationChannelSender> senders,
    IOptions<AppOptions> app,
    TimeProvider time,
    ILogger<NotificationDispatcher> logger)
{
    /// <summary>The <c>channel</c> column value of a project channel: <c>&lt;type&gt;:&lt;channel id&gt;</c>.</summary>
    public static string ChannelName(NotificationChannelConfig channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return $"{channel.Type.ToWire()}:{channel.Id:D}";
    }

    /// <summary>FR-92: the <c>channel</c> column value of a personal notification's email to the user's account address.</summary>
    public const string AccountEmailPrefix = "account_email:";

    public static string AccountEmailChannel(Guid userId) => $"{AccountEmailPrefix}{userId:D}";

    /// <summary>Attempts up to <paramref name="max"/> due deliveries (of <paramref name="key"/> only, if given); returns how many were attempted.</summary>
    public async Task<int> DeliverDueAsync(string? key, int max, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var query = db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.Status == NotificationDeliveryStatus.Pending && d.NextAttemptAt != null && d.NextAttemptAt <= now);
        if (key is not null)
        {
            query = query.Where(d => d.NotificationKey == key);
        }

        var due = await query.OrderBy(d => d.NextAttemptAt).Take(max).ToListAsync(cancellationToken);
        var messages = new Dictionary<string, ComposedNotification?>(StringComparer.Ordinal);
        var channels = new Dictionary<Guid, IReadOnlyList<NotificationChannelConfig>>();
        var attempted = 0;
        foreach (var delivery in due)
        {
            var claimedAt = time.GetUtcNow();
            var leaseEnd = claimedAt + DeliveryPolicy.ClaimLease;
            var claimed = await db.NotificationDeliveries
                .Where(d => d.Id == delivery.Id && d.Status == NotificationDeliveryStatus.Pending && d.NextAttemptAt <= claimedAt)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Attempts, d => d.Attempts + 1).SetProperty(d => d.NextAttemptAt, leaseEnd), cancellationToken);
            if (claimed == 0)
            {
                continue; // another dispatcher has it
            }

            attempted++;
            var attempts = delivery.Attempts + 1;
            var outcome = await AttemptAsync(delivery, messages, channels, claimedAt, cancellationToken);
            var finishedAt = time.GetUtcNow();
            var next = DeliveryPolicy.After(outcome.Result, attempts, finishedAt);
            DateTimeOffset? sentAt = outcome.Result == DeliveryAttemptResult.Sent ? finishedAt : null;
            var error = outcome.Error is { Length: > 1000 } tooLong ? tooLong[..1000] : outcome.Error;
            await db.NotificationDeliveries
                .Where(d => d.Id == delivery.Id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(d => d.Status, next.Status)
                        .SetProperty(d => d.NextAttemptAt, next.NextAttemptAt)
                        .SetProperty(d => d.LastError, error)
                        .SetProperty(d => d.SentAt, sentAt),
                    CancellationToken.None);
            LogAttempt(logger, delivery.NotificationKey, delivery.Channel, attempts, outcome.Result, next.Status, error);
        }

        return attempted;
    }

    private async Task<ChannelSendResult> AttemptAsync(
        NotificationDelivery delivery,
        Dictionary<string, ComposedNotification?> messages,
        Dictionary<Guid, IReadOnlyList<NotificationChannelConfig>> channels,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!NotificationRules.TryParseKey(delivery.NotificationKey, out var type, out var sourceId))
        {
            return new ChannelSendResult(DeliveryAttemptResult.PermanentFailure, "Unreadable notification key.");
        }

        NotificationChannelConfig? channel;
        if (delivery.Channel.StartsWith(AccountEmailPrefix, StringComparison.Ordinal))
        {
            channel = await AccountEmailAsync(delivery.Channel, cancellationToken);
            if (channel is null)
            {
                return new ChannelSendResult(DeliveryAttemptResult.Skipped, "The user has no email address.");
            }
        }
        else
        {
            if (delivery.ProjectId is not { } projectId)
            {
                return new ChannelSendResult(DeliveryAttemptResult.PermanentFailure, "Unreadable notification key.");
            }

            if (!channels.TryGetValue(projectId, out var configured))
            {
                configured = await db.ProjectNotificationSettings.AsNoTracking()
                    .Where(s => s.ProjectId == projectId)
                    .Select(s => s.Channels)
                    .FirstOrDefaultAsync(cancellationToken) ?? [];
                channels[projectId] = configured;
            }

            channel = configured.FirstOrDefault(c => ChannelName(c) == delivery.Channel);
            if (channel is null)
            {
                return new ChannelSendResult(DeliveryAttemptResult.Skipped, "The channel was removed from the project settings.");
            }
        }

        if (!messages.TryGetValue(delivery.NotificationKey, out var composed))
        {
            composed = await composer.ComposeAsync(delivery.ProjectId, type, sourceId, cancellationToken);
            messages[delivery.NotificationKey] = composed;
        }

        if (composed is null)
        {
            return new ChannelSendResult(DeliveryAttemptResult.Skipped, "What the notification was about no longer exists.");
        }

        if (composed.Gate is { Status: not GateStatus.Pending })
        {
            // A retry of gate_required / reminder / overdue after the gate was decided or cancelled would ask for a
            // decision that is no longer needed.
            return new ChannelSendResult(DeliveryAttemptResult.Skipped, "The gate is no longer pending.");
        }

        var urls = composed.Gate is { } gate ? await approvalLinks.UrlsAsync(gate, now, cancellationToken) : null;
        var message = new ChannelMessage(
            delivery.Id,
            delivery.NotificationKey,
            type,
            composed.ProjectId,
            composed.ProjectName,
            composed.Text.Title,
            composed.Text.Body,
            app.Value.Absolute(composed.LinkPath),
            urls?.ApproveUrl,
            urls?.RejectUrl,
            now);
        var sender = senders.FirstOrDefault(s => s.Type == channel.Type);
        if (sender is null)
        {
            return new ChannelSendResult(DeliveryAttemptResult.PermanentFailure, "No sender for this channel type.");
        }

        // Backstop for every channel: a send never outlives MaxSendTimeout, so it ends well inside the claim lease and no
        // other dispatcher can claim and re-send the delivery while it is in flight (FR-49).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DeliveryPolicy.MaxSendTimeout);
        try
        {
            return await sender.SendAsync(channel, message, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ChannelSendResult(DeliveryAttemptResult.TransientFailure, "The send timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A delivery failure never escapes: it is recorded on the delivery and retried with backoff.
            return new ChannelSendResult(DeliveryAttemptResult.TransientFailure, $"Unexpected {ex.GetType().Name}.");
        }
    }

    /// <summary>The email channel of a personal delivery: the user's account address, or <c>null</c> without one.</summary>
    private async Task<NotificationChannelConfig?> AccountEmailAsync(string channelName, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(channelName[AccountEmailPrefix.Length..], "D", out var userId))
        {
            return null;
        }

        var email = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(email)
            ? null
            : new NotificationChannelConfig(userId, NotificationChannelType.Email, email, null, []);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {NotificationKey} via {Channel}: attempt {Attempt} {Result} → {Status} {Error}")]
    private static partial void LogAttempt(ILogger logger, string notificationKey, string channel, int attempt, DeliveryAttemptResult result, NotificationDeliveryStatus status, string? error);
}
