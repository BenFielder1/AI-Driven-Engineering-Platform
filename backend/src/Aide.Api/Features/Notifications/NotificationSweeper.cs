using System.Data.Common;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aide.Api.Features.Notifications;

/// <summary>
/// FR-15: pending gates get a reminder after 24 hours and are flagged <c>overdue</c> (with a <c>GATE_OVERDUE</c> event and its
/// notification) after 72 hours (<see cref="GateTimers"/>). Nothing is ever decided automatically. Each change is a
/// conditional update in its own transaction with its follow-up (the reminder job, or the event whose outbox job notifies),
/// so concurrent sweeps on several pods act once.
/// </summary>
public sealed partial class GateTimerService(AideDbContext db, JobEnqueuer jobs, TimeProvider time, ILogger<GateTimerService> logger)
{
    private const int BatchSize = 200;

    /// <summary>Applies due reminders and overdue flags; returns how many gates changed.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var reminderCutoff = now - GateTimers.ReminderAfter;
        var overdueCutoff = now - GateTimers.OverdueAfter;
        // Only gates with something due: not yet reminded, or old enough to be overdue. A reminded gate waiting for its
        // 72-hour mark would otherwise fill the batch and starve newer gates of their reminders.
        var candidates = await db.ApprovalGates.AsNoTracking()
            .Where(g => g.Status == GateStatus.Pending && !g.Overdue && g.CreatedAt <= reminderCutoff
                && (g.RemindedAt == null || g.CreatedAt <= overdueCutoff))
            .OrderBy(g => g.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var gate in candidates)
        {
            var actions = GateTimers.Evaluate(gate.Status, gate.CreatedAt, now, gate.RemindedAt, gate.Overdue);
            if (actions.MarkOverdue && await MarkOverdueAsync(gate, now, cancellationToken))
            {
                changed++;
            }
            else if (actions.SendReminder && await RemindAsync(gate, now, cancellationToken))
            {
                changed++;
            }
        }

        return changed;
    }

    private async Task<bool> MarkOverdueAsync(ApprovalGate gate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.ApprovalGates
            .Where(g => g.Id == gate.Id && g.Status == GateStatus.Pending && !g.Overdue)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Overdue, true), cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        // The outbox interceptor queues the gate_overdue notification job with this event.
        db.AddEvent(EventTypes.GateOverdue, gate.ProjectId, new { gateId = gate.Id, type = gate.Type, createdAt = gate.CreatedAt }, SystemActor.Name, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogOverdue(logger, gate.ProjectId, gate.Id, gate.Type);
        return true;
    }

    private async Task<bool> RemindAsync(ApprovalGate gate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.ApprovalGates
            .Where(g => g.Id == gate.Id && g.Status == GateStatus.Pending && !g.Overdue && g.RemindedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RemindedAt, now), cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        await jobs.EnqueueAsync(JobTypes.Notification, new NotificationJobPayload(gate.ProjectId, NotificationType.GateReminder, gate.Id), gate.ProjectId, cancellationToken, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogReminded(logger, gate.ProjectId, gate.Id, gate.Type);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Gate {GateId} ({GateType}) of project {ProjectId} is overdue (72 h).")]
    private static partial void LogOverdue(ILogger logger, Guid projectId, Guid gateId, GateType gateType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Gate {GateId} ({GateType}) of project {ProjectId}: 24-hour reminder queued.")]
    private static partial void LogReminded(ILogger logger, Guid projectId, Guid gateId, GateType gateType);
}

/// <summary>
/// The notification background loop: every <see cref="NotificationOptions.DeliverySweepInterval"/> it retries due channel
/// deliveries (FR-49), every <see cref="NotificationOptions.GateSweepInterval"/> it runs the gate timers (FR-15), and every
/// <see cref="NotificationOptions.DatabaseExpirySweepInterval"/> it queues the database expiry sweep job (FR-155).
/// Tests turn the loop off (<c>Notifications:SweepEnabled=false</c>) and call the steps directly.
/// </summary>
public sealed partial class NotificationSweeper(
    IServiceScopeFactory scopes,
    IOptions<NotificationOptions> options,
    TimeProvider time,
    ILogger<NotificationSweeper> logger) : BackgroundService
{
    private readonly NotificationOptions _options = options.Value;

    /// <summary>One retry pass over due deliveries; returns how many were attempted.</summary>
    public async Task<int> RetryDueDeliveriesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>()
            .DeliverDueAsync(null, _options.SweepBatchSize, cancellationToken);
    }

    /// <summary>One gate-timer pass; returns how many gates changed.</summary>
    public async Task<int> RunGateTimersAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GateTimerService>().RunAsync(cancellationToken);
    }

    /// <summary>
    /// FR-155: queues the <c>database_expiry_sweep</c> job unless one is already waiting or running. Provider calls happen in
    /// the job, on the worker's queue, so a slow platform never holds up this loop. Returns whether one was queued.
    /// </summary>
    public async Task<bool> QueueDatabaseExpirySweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        if (await db.Jobs.AsNoTracking().AnyAsync(j => j.Type == JobTypes.DatabaseExpirySweep && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), cancellationToken))
        {
            return false;
        }

        scope.ServiceProvider.GetRequiredService<JobEnqueuer>().Enqueue(JobTypes.DatabaseExpirySweep, new { }, null, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.SweepEnabled)
        {
            LogDisabled(logger);
            return;
        }

        DateTimeOffset? lastGateSweep = null;
        DateTimeOffset? lastDatabaseSweep = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RetryDueDeliveriesAsync(stoppingToken);
                var now = time.GetUtcNow();
                if (lastGateSweep is null || now - lastGateSweep >= _options.GateSweepInterval)
                {
                    await RunGateTimersAsync(stoppingToken);
                    lastGateSweep = now;
                }

                if (lastDatabaseSweep is null || now - lastDatabaseSweep >= _options.DatabaseExpirySweepInterval)
                {
                    await QueueDatabaseExpirySweepAsync(stoppingToken);
                    lastDatabaseSweep = now;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException or TimeoutException)
            {
                // The database may be unavailable or not migrated yet; try again on the next pass.
                LogSweepFailed(logger, ex);
            }

            try
            {
                await Task.Delay(_options.DeliverySweepInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification sweep is disabled (Notifications:SweepEnabled=false).")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification sweep failed; retrying on the next pass.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
