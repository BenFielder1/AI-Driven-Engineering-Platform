using System.Data.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aide.Api.Jobs;

/// <summary>
/// The work-queue side of §4.9 (D1, NFR-REL). The worker keeps up to <see cref="JobOptions.Concurrency"/> jobs in flight and,
/// on every poll (each <see cref="JobOptions.PollInterval"/>, or as soon as a running job finishes and frees a slot):
/// <list type="number">
/// <item>re-queues <c>running</c> jobs whose lease expired (the lost attempt counts: <see cref="JobPolicy.OnLeaseExpired"/>);</item>
/// <item>claims runnable <c>queued</c> jobs into the free slots with <c>FOR UPDATE SKIP LOCKED</c>, only of types that have a
/// registered <see cref="IJobHandler"/> (other types stay queued), so one long job never idles the other slots;</item>
/// <item>runs each in its own DI scope with JobId/ProjectId log scopes, renewing its lease (<c>locked_at</c>) every
/// <see cref="LeaseRenewalInterval"/> while the handler runs. A renewal that finds the lease gone (reaped or claimed
/// elsewhere) cancels the handler, and its outcome is not recorded;</item>
/// <item>records the outcome: success, or retry with backoff / dead letter / failure per <see cref="JobPolicy.OnFailure"/>.
/// On shutdown, interrupted jobs are handed back without counting the attempt.</item>
/// </list>
/// Time comes from the injected <see cref="TimeProvider"/>; <see cref="RunOnceAsync"/> lets tests drive it deterministically.
/// </summary>
public sealed partial class JobWorker : BackgroundService
{
    public const int MaxErrorLength = 2000;
    private const int ReapBatchSize = 100;

    private readonly IServiceScopeFactory _scopes;
    private readonly JobOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<JobWorker> _logger;
    private readonly Dictionary<string, JobHandlerRegistration> _handlers;

    public JobWorker(
        IServiceScopeFactory scopes,
        IEnumerable<JobHandlerRegistration> registrations,
        IOptions<JobOptions> options,
        TimeProvider time,
        ILogger<JobWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(options);
        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _handlers = new Dictionary<string, JobHandlerRegistration>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            if (!_handlers.TryAdd(registration.JobType, registration))
            {
                throw new InvalidOperationException($"More than one handler is registered for job type '{registration.JobType}'.");
            }
        }

        WorkerId = $"{Environment.MachineName}/{Guid.NewGuid():N}";
    }

    /// <summary>Recorded in <c>jobs.locked_by</c> for the jobs this worker claims.</summary>
    public string WorkerId { get; }

    /// <summary>Job types this worker claims.</summary>
    public IReadOnlyCollection<string> HandledTypes => _handlers.Keys;

    /// <summary>How often a running job's lease is renewed: a third of the lease, so two renewals can fail before it expires.</summary>
    public TimeSpan LeaseRenewalInterval => _options.LeaseDuration / 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(_logger);
            return;
        }

        if (_handlers.Count == 0)
        {
            LogNoHandlers(_logger);
            return;
        }

        var handledTypes = string.Join(", ", _handlers.Keys.Order(StringComparer.Ordinal));
        LogStarting(_logger, WorkerId, handledTypes, _options.Concurrency);
        var inFlight = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                inFlight.RemoveAll(t => t.IsCompleted);
                var started = 0;
                try
                {
                    var tasks = await PollAsync(_options.Concurrency - inFlight.Count, stoppingToken);
                    inFlight.AddRange(tasks);
                    started = tasks.Count;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException or TimeoutException)
                {
                    // The database may be unavailable or not migrated yet; try again on the next poll.
                    LogPollFailed(_logger, ex);
                }

                if (started > 0 && inFlight.Count < _options.Concurrency)
                {
                    continue; // work was claimed and slots are still free: more may be waiting
                }

                // Wait for the next poll, or for a running job to finish and free a slot.
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var delay = Task.Delay(_options.PollInterval, _time, delayCts.Token);
                await Task.WhenAny([.. inFlight, delay]);
                await delayCts.CancelAsync();
            }
        }
        finally
        {
            // Shutdown cancels the handlers; wait for them to hand their jobs back.
            await Task.WhenAll(inFlight);
        }
    }

    /// <summary>
    /// One poll that waits for what it claimed: re-queue expired leases, claim up to <see cref="JobOptions.Concurrency"/>
    /// jobs and run them to completion. Returns the number of jobs run.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var started = await PollAsync(_options.Concurrency, cancellationToken);
        await Task.WhenAll(started);
        return started.Count;
    }

    /// <summary>Re-queues expired leases, then claims up to <paramref name="freeSlots"/> jobs and starts them (not awaited).</summary>
    private async Task<List<Task>> PollAsync(int freeSlots, CancellationToken cancellationToken)
    {
        if (_handlers.Count == 0)
        {
            return [];
        }

        var types = _handlers.Keys.ToArray();
        await RequeueExpiredAsync(types, cancellationToken);
        if (freeSlots <= 0)
        {
            return [];
        }

        var claimed = await ClaimAsync(types, freeSlots, cancellationToken);
        return [.. claimed.Select(job => Task.Run(() => RunAsync(job, cancellationToken), CancellationToken.None))];
    }

    private async Task RequeueExpiredAsync(string[] types, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        var now = _time.GetUtcNow();
        var cutoff = now - _options.LeaseDuration;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var expired = await db.Jobs
            .FromSql($"""
                SELECT * FROM jobs
                WHERE status = 'running' AND locked_at <= {cutoff} AND type = ANY({types})
                ORDER BY locked_at
                LIMIT {ReapBatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
        foreach (var job in expired)
        {
            var outcome = JobPolicy.OnLeaseExpired(job.Attempts, now);
            using var logScope = BeginJobScope(job);
            LogLeaseExpired(_logger, job.LockedBy, job.Attempts, outcome.Status);
            job.Status = outcome.Status;
            job.RunAfter = outcome.RunAfter ?? job.RunAfter;
            job.LastError = $"The lease of worker {job.LockedBy} expired before the job finished.";
            job.LockedBy = null;
            job.LockedAt = null;
            job.CompletedAt = outcome.Status == JobStatus.Queued ? null : now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<List<Job>> ClaimAsync(string[] types, int limit, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        var now = _time.GetUtcNow();
        var workerId = WorkerId;

        // One statement (its own transaction): lock the next runnable rows other workers haven't locked, mark them running.
        return await db.Jobs
            .FromSql($"""
                WITH next AS (
                    SELECT id FROM jobs
                    WHERE status = 'queued' AND run_after <= {now} AND type = ANY({types})
                    ORDER BY run_after, id
                    LIMIT {limit}
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE jobs AS j
                SET status = 'running', attempts = j.attempts + 1, locked_by = {workerId}, locked_at = {now}
                FROM next
                WHERE j.id = next.id
                RETURNING j.*
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    private async Task RunAsync(Job job, CancellationToken stoppingToken)
    {
        using var logScope = BeginJobScope(job);
        LogRunning(_logger, job.Type, job.Attempts);

        using var leaseLost = new CancellationTokenSource();
        using var handlerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, leaseLost.Token);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = RenewLeaseAsync(job.Id, leaseLost, heartbeatStop.Token);

        var succeeded = false;
        var released = false;
        var transient = true;
        string? error = null;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var handler = _handlers[job.Type].Factory(scope.ServiceProvider);
            await handler.HandleAsync(new JobExecution(job.Id, job.Type, job.Payload, job.ProjectId, job.Attempts), handlerCts.Token);
            succeeded = true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: hand the job back without counting the interrupted attempt.
            released = true;
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            // The lease was lost (reaped or claimed elsewhere): the outcome is no longer ours to record.
        }
        catch (JobFailedException ex)
        {
            transient = ex.Transient;
            error = ex.ErrorCode is null ? ex.Message : $"{ex.ErrorCode}: {ex.Message}";
            LogHandlerFailed(_logger, ex, transient);
        }
#pragma warning disable CA1031 // A handler bug must become a recorded, retried job failure, not crash the worker.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            LogHandlerFailed(_logger, ex, transient);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
        }

        if (leaseLost.IsCancellationRequested && !released)
        {
            LogLeaseLost(_logger);
            return;
        }

        try
        {
            await CompleteAsync(job, succeeded, released, transient, error);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException or TimeoutException)
        {
            // The job stays running until its lease expires; then the reaper re-queues it.
            LogCompleteFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Renews the job's lease every <see cref="LeaseRenewalInterval"/> while its handler runs, only while this worker still
    /// holds it (<c>status = running AND locked_by = this worker</c>). A renewal that finds the lease gone cancels
    /// <paramref name="leaseLost"/>, and with it the handler.
    /// </summary>
    private async Task RenewLeaseAsync(Guid jobId, CancellationTokenSource leaseLost, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(LeaseRenewalInterval, _time, stop);
                int renewed;
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
                    var now = _time.GetUtcNow();
                    renewed = await db.Jobs
                        .Where(j => j.Id == jobId && j.Status == JobStatus.Running && j.LockedBy == WorkerId)
                        .ExecuteUpdateAsync(s => s.SetProperty(j => j.LockedAt, now), stop);
                }
                catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException or TimeoutException)
                {
                    // Try again on the next renewal; the lease outlasts two missed renewals.
                    LogRenewFailed(_logger, ex);
                    continue;
                }

                if (renewed == 0)
                {
                    await leaseLost.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The handler finished.
        }
    }

    private async Task CompleteAsync(Job job, bool succeeded, bool released, bool transient, string? error)
    {
        // The outcome is recorded even while shutting down, so the job isn't left running until its lease expires.
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        var now = _time.GetUtcNow();
        var mine = db.Jobs.Where(j => j.Id == job.Id && j.Status == JobStatus.Running && j.LockedBy == WorkerId);

        int updated;
        if (succeeded)
        {
            updated = await mine.ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, JobStatus.Succeeded)
                    .SetProperty(j => j.CompletedAt, now)
                    .SetProperty(j => j.LastError, (string?)null)
                    .SetProperty(j => j.LockedBy, (string?)null)
                    .SetProperty(j => j.LockedAt, (DateTimeOffset?)null),
                CancellationToken.None);
            LogSucceeded(_logger);
        }
        else if (released)
        {
            updated = await mine.ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, JobStatus.Queued)
                    .SetProperty(j => j.Attempts, Math.Max(0, job.Attempts - 1))
                    .SetProperty(j => j.RunAfter, now)
                    .SetProperty(j => j.LockedBy, (string?)null)
                    .SetProperty(j => j.LockedAt, (DateTimeOffset?)null),
                CancellationToken.None);
        }
        else
        {
            var outcome = JobPolicy.OnFailure(job.Attempts, transient, now);
            var lastError = Truncate(error);
            var completedAt = outcome.Status == JobStatus.Queued ? (DateTimeOffset?)null : now;
            updated = await mine.ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, outcome.Status)
                    .SetProperty(j => j.RunAfter, outcome.RunAfter ?? job.RunAfter)
                    .SetProperty(j => j.LastError, lastError)
                    .SetProperty(j => j.CompletedAt, completedAt)
                    .SetProperty(j => j.LockedBy, (string?)null)
                    .SetProperty(j => j.LockedAt, (DateTimeOffset?)null),
                CancellationToken.None);
            if (outcome.IsDeadLetter)
            {
                LogDeadLettered(_logger, job.Attempts);
            }
            else if (outcome.Status == JobStatus.Queued)
            {
                LogRetryScheduled(_logger, job.Attempts, outcome.RunAfter);
            }
            else
            {
                LogFailedPermanently(_logger, job.Attempts);
            }
        }

        if (updated == 0)
        {
            LogLeaseLost(_logger);
        }
    }

    private IDisposable? BeginJobScope(Job job) => _logger.BeginScope(new Dictionary<string, object?>
    {
        ["JobId"] = job.Id,
        ["JobType"] = job.Type,
        ["ProjectId"] = job.ProjectId,
    });

    private static string? Truncate(string? value) =>
        value is { Length: > MaxErrorLength } ? value[..MaxErrorLength] : value;

    [LoggerMessage(Level = LogLevel.Information, Message = "Job worker disabled (Jobs:Enabled=false)")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "No job handlers are registered; queued jobs wait until a handler for their type exists")]
    private static partial void LogNoHandlers(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job worker {WorkerId} started for {JobTypes} (concurrency {Concurrency})")]
    private static partial void LogStarting(ILogger logger, string workerId, string jobTypes, int concurrency);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job poll failed; retrying on the next poll")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Running {JobType} job (attempt {Attempt})")]
    private static partial void LogRunning(ILogger logger, string jobType, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job succeeded")]
    private static partial void LogSucceeded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job handler failed (transient: {Transient})")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, bool transient);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job attempt {Attempt} failed; retry scheduled at {RunAfter}")]
    private static partial void LogRetryScheduled(ILogger logger, int attempt, DateTimeOffset? runAfter);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job dead-lettered after {Attempts} attempts")]
    private static partial void LogDeadLettered(ILogger logger, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job failed permanently on attempt {Attempts} (non-transient)")]
    private static partial void LogFailedPermanently(ILogger logger, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job lease of worker {LockedBy} expired after {Attempts} attempts; now {Status}")]
    private static partial void LogLeaseExpired(ILogger logger, string? lockedBy, int attempts, JobStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job outcome not recorded: the lease expired and the job was re-queued or claimed elsewhere")]
    private static partial void LogLeaseLost(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job lease renewal failed; retrying on the next renewal")]
    private static partial void LogRenewFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording the job outcome failed; the job is retried once its lease expires")]
    private static partial void LogCompleteFailed(ILogger logger, Exception exception);
}
