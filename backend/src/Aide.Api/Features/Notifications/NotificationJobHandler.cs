using System.Text.Json;
using Aide.Api.Features.Approvals;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Notifications;

/// <summary>
/// FR-47 fan-out of one logical notification (<c>&lt;type&gt;:&lt;source&gt;</c>), idempotent under redelivery:
/// <list type="number">
/// <item>gate notifications of a gate that is no longer pending are dropped (it was decided before the job ran);</item>
/// <item>a <c>gate_required</c> spec, plan or pre-release gate gets its one-click link pair (FR-48);</item>
/// <item>each recipient (<see cref="NotificationRules.Recipients"/>) who hasn't muted the type (§2) gets one in-app
/// notification — unique per (user, key), so a redelivered job adds nothing;</item>
/// <item>each project channel subscribed to the type gets one delivery row — unique per (key, channel);</item>
/// <item>a personal notification (FR-92 <c>connection_invalid</c>) goes to its one user instead: in-app, plus one email
/// delivery to their account address — no project channel, and nothing when they muted the type.</item>
/// </list>
/// all in one transaction; then the new deliveries get their first attempt. Delivery failures are recorded on the
/// deliveries and retried by the sweep (FR-49); they never fail the job.
/// </summary>
public sealed partial class NotificationJobHandler(
    AideDbContext db,
    NotificationComposer composer,
    ApprovalLinkService approvalLinks,
    NotificationDispatcher dispatcher,
    TimeProvider time,
    ILogger<NotificationJobHandler> logger) : IJobHandler
{
    private const int FirstAttemptBatch = 50;

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        NotificationJobPayload payload;
        try
        {
            payload = AideJson.Deserialize<NotificationJobPayload>(job.Payload);
        }
        catch (JsonException ex)
        {
            throw new JobFailedException($"Unreadable notification payload: {ex.Message}", transient: false);
        }

        var key = NotificationRules.Key(payload.Type, payload.SourceId);
        var composed = await composer.ComposeAsync(payload.ProjectId, payload.Type, payload.SourceId, cancellationToken);
        if (composed is null)
        {
            LogSkipped(logger, key, "its project, gate or event no longer exists");
            return;
        }

        if (composed.Gate is { Status: not GateStatus.Pending })
        {
            LogSkipped(logger, key, "the gate is no longer pending");
            return;
        }

        if (payload.Type == NotificationType.BudgetExceeded && payload.ProjectId is { } budgetProject
            && !await IsFirstRefusalOfBudgetAsync(budgetProject, payload.SourceId, cancellationToken))
        {
            LogSkipped(logger, key, "the used-up token budget was already reported and hasn't changed since");
            return;
        }

        var now = time.GetUtcNow();
        if (payload.Type == NotificationType.GateRequired && composed.Gate is { } gate)
        {
            await approvalLinks.EnsureLinksAsync(gate, now, cancellationToken);
        }

        var added = await AddInAppAsync(composed, now, cancellationToken);
        var deliveries = await AddDeliveriesAsync(composed, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        LogFannedOut(logger, key, added, deliveries);

        try
        {
            await dispatcher.DeliverDueAsync(key, FirstAttemptBatch, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The deliveries are saved; the sweep retries them.
            LogFirstAttemptFailed(logger, ex, key);
        }
    }

    /// <summary>
    /// <see cref="NotificationRules.NotifiesBudgetExceeded"/> for the <c>BUDGET_EXCEEDED</c> event <paramref name="eventId"/>:
    /// finds the project's previous refusal (events are ordered by time, then by their time-ordered v7 id) and the token
    /// budget changes (<c>PROJECT_UPDATED</c> with <c>tokenBudget</c> in <c>changes</c>) since it.
    /// </summary>
    private async Task<bool> IsFirstRefusalOfBudgetAsync(Guid projectId, Guid eventId, CancellationToken cancellationToken)
    {
        var at = await db.Events.AsNoTracking().Where(e => e.Id == eventId).Select(e => (DateTimeOffset?)e.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (at is not { } current)
        {
            return true;
        }

        var earlier = await db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Type == EventTypes.BudgetExceeded && e.Id != eventId && e.CreatedAt <= current)
            .OrderByDescending(e => e.CreatedAt)
            .Take(20)
            .Select(e => new { e.Id, e.CreatedAt })
            .ToListAsync(cancellationToken);
        var previous = earlier.FirstOrDefault(e => e.CreatedAt < current || e.Id.CompareTo(eventId) < 0);
        if (previous is null)
        {
            return true;
        }

        var updates = await db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Type == EventTypes.ProjectUpdated && e.CreatedAt >= previous.CreatedAt && e.CreatedAt <= current)
            .Select(e => new { e.CreatedAt, e.Payload })
            .ToListAsync(cancellationToken);
        var budgetChanges = updates.Where(u => ChangesTokenBudget(u.Payload)).Select(u => u.CreatedAt);
        return NotificationRules.NotifiesBudgetExceeded(previous.CreatedAt, budgetChanges);
    }

    /// <summary>Whether a <c>PROJECT_UPDATED</c> payload lists <c>tokenBudget</c> in its <c>changes</c>.</summary>
    private static bool ChangesTokenBudget(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("changes", out var changes)
                && changes.ValueKind == JsonValueKind.Array
                && changes.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "tokenBudget");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Who gets the notification: the project's Owners or members, or the one user of a personal notification.</summary>
    private async Task<List<Guid>> RecipientsAsync(ComposedNotification composed, CancellationToken cancellationToken)
    {
        var scope = NotificationRules.Recipients(composed.Type);
        if (scope == RecipientScope.ConnectionOwner)
        {
            return composed.RecipientUserId is { } userId && await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId, cancellationToken)
                ? [userId]
                : [];
        }

        return await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == composed.ProjectId && (scope == RecipientScope.AllMembers || m.Role == ProjectRole.Owner))
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The recipients who haven't muted the type (§2). Muted types are a text[] converted as a whole, so they're filtered in memory.</summary>
    private async Task<List<Guid>> WantingAsync(NotificationType type, List<Guid> recipients, CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
        {
            return recipients;
        }

        var muted = (await db.UserNotificationPreferences.AsNoTracking()
                .Where(p => recipients.Contains(p.UserId))
                .ToListAsync(cancellationToken))
            .ToDictionary(p => p.UserId, p => p.MutedTypes);
        return [.. recipients.Where(userId => NotificationRules.Wants(type, muted.GetValueOrDefault(userId)))];
    }

    private async Task<int> AddInAppAsync(ComposedNotification composed, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var recipients = await WantingAsync(composed.Type, await RecipientsAsync(composed, cancellationToken), cancellationToken);
        if (recipients.Count == 0)
        {
            return 0;
        }

        var key = composed.Key;
        var already = (await db.Notifications.AsNoTracking()
                .Where(n => n.NotificationKey == key && recipients.Contains(n.UserId))
                .Select(n => n.UserId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var added = 0;
        foreach (var userId in recipients)
        {
            if (already.Contains(userId))
            {
                continue;
            }

            db.Notifications.Add(new Notification
            {
                UserId = userId,
                ProjectId = composed.ProjectId,
                Type = composed.Type.ToWire(),
                Title = composed.Text.Title,
                Body = composed.Text.Body,
                Link = composed.LinkPath,
                NotificationKey = key,
                CreatedAt = now,
            });
            added++;
        }

        return added;
    }

    private async Task<int> AddDeliveriesAsync(ComposedNotification composed, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<string> wanted;
        if (NotificationRules.ChannelEvent(composed.Type) is not { } channelEvent)
        {
            // Personal (FR-92): the user's account email, unless they muted the type.
            var recipients = await WantingAsync(composed.Type, await RecipientsAsync(composed, cancellationToken), cancellationToken);
            wanted = [.. recipients.Select(NotificationDispatcher.AccountEmailChannel)];
        }
        else
        {
            var configured = composed.ProjectId is { } projectId
                ? await db.ProjectNotificationSettings.AsNoTracking()
                    .Where(s => s.ProjectId == projectId)
                    .Select(s => s.Channels)
                    .FirstOrDefaultAsync(cancellationToken) ?? []
                : [];
            wanted = configured
                .Where(c => c.Events.Contains(channelEvent, StringComparer.Ordinal))
                .Select(NotificationDispatcher.ChannelName)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        if (wanted.Count == 0)
        {
            return 0;
        }

        var key = composed.Key;
        var existing = (await db.NotificationDeliveries.AsNoTracking()
                .Where(d => d.NotificationKey == key)
                .Select(d => d.Channel)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var channel in wanted.Where(c => !existing.Contains(c)))
        {
            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                ProjectId = composed.ProjectId,
                NotificationKey = key,
                Channel = channel,
                Status = NotificationDeliveryStatus.Pending,
                NextAttemptAt = now,
                CreatedAt = now,
            });
            added++;
        }

        return added;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {NotificationKey} skipped: {Reason}.")]
    private static partial void LogSkipped(ILogger logger, string notificationKey, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {NotificationKey}: {InApp} in-app notification(s), {Deliveries} channel delivery(ies) added.")]
    private static partial void LogFannedOut(ILogger logger, string notificationKey, int inApp, int deliveries);

    [LoggerMessage(Level = LogLevel.Warning, Message = "First delivery attempt of notification {NotificationKey} failed; the sweep retries it.")]
    private static partial void LogFirstAttemptFailed(ILogger logger, Exception exception, string notificationKey);
}
