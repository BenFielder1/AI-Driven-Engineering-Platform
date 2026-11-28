using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Api.Features.Live;

/// <summary>FR-363: a branch stage execution a save entered or left (its project when the unit of work tracks the run).</summary>
public sealed record BranchLiveChange(Guid? ProjectId, Guid RunId, Guid ExecutionId, string StageId, string Branch, DateTimeOffset? ExitedAt, string? Outcome);

/// <summary>FR-363: a map execution whose items a save opened or ended.</summary>
public sealed record MapLiveChange(Guid? ProjectId, Guid RunId, Guid MapExecutionId);

/// <summary>FR-363: what one committed unit of work changed in branches and maps.</summary>
public sealed record ConstructLiveChanges(IReadOnlyList<BranchLiveChange> Branches, IReadOnlyList<MapLiveChange> Maps)
{
    public bool IsEmpty => Branches.Count == 0 && Maps.Count == 0;
}

/// <summary>What <see cref="MapProgressThrottle.Decide"/> says to do with a map's progress change.</summary>
public enum MapProgressSend
{
    /// <summary>Send now: nothing was sent within the interval.</summary>
    Now,

    /// <summary>Send once the interval since the last send has passed (with the counts read then).</summary>
    Trailing,

    /// <summary>A trailing send is already scheduled; it will read these counts too.</summary>
    Skip,
}

/// <summary>
/// FR-363: <c>mapProgress</c> is throttled per map execution: at most one send per <see cref="Interval"/>, and a change
/// inside the interval schedules one trailing send at its end, which reads the counts then. So a 1,000-item map sends at
/// most one message a second, and the last item's end is always followed by a message with the final counts.
/// </summary>
public static class MapProgressThrottle
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>The send for a change at <paramref name="now"/>, and (for <see cref="MapProgressSend.Trailing"/>) its delay.</summary>
    public static (MapProgressSend Send, TimeSpan Delay) Decide(DateTimeOffset? lastSentAt, bool trailingScheduled, DateTimeOffset now, TimeSpan interval)
    {
        if (trailingScheduled)
        {
            return (MapProgressSend.Skip, TimeSpan.Zero);
        }

        if (lastSentAt is not { } last || now - last >= interval)
        {
            return (MapProgressSend.Now, TimeSpan.Zero);
        }

        return (MapProgressSend.Trailing, last + interval - now);
    }

    /// <summary>Whether every item of a map has ended (so its throttle state can go).</summary>
    public static bool Settled(PipelineMapProgressDto progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return progress.Total > 0 && progress.Completed + progress.Failed >= progress.Total;
    }
}

/// <summary>
/// FR-363: runs work one at a time per key, in the order it was asked for; different keys run concurrently. A failed
/// piece of work fails only its own call, and the next one still runs. Keys with nothing running are dropped.
/// </summary>
public sealed class KeyedSerialiser
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Task> _tails = [];

    /// <summary>How many keys have work running or waiting.</summary>
    public int ActiveKeys
    {
        get
        {
            lock (_lock)
            {
                return _tails.Count;
            }
        }
    }

    public async Task RunAsync(Guid key, Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_lock)
        {
            previous = _tails.GetValueOrDefault(key) ?? Task.CompletedTask;
            _tails[key] = done.Task;
        }

        try
        {
            // The tails only ever complete successfully (they are this method's own completion sources).
            await previous;
            await work();
        }
        finally
        {
            lock (_lock)
            {
                if (_tails.TryGetValue(key, out var tail) && tail == done.Task)
                {
                    _tails.Remove(key);
                }
            }

            done.SetResult();
        }
    }
}

/// <summary>
/// FR-363: after a committed save, sends <c>stageBranchUpdated</c> for each branch stage execution it entered or left, and
/// a throttled <c>mapProgress</c> for each map whose items it opened or ended, to the run's project group. Payloads are ids,
/// state and counts only. Nothing is read for a project nobody is viewing; a failure is logged and never fails the write.
/// The SDLC has no branches or maps, so it sends nothing new.
/// </summary>
public sealed partial class PipelineConstructLivePublisher(
    IServiceScopeFactory scopes,
    IHubContext<ProjectsHub> hub,
    ProjectViewerRegistry viewers,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<PipelineConstructLivePublisher> logger)
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, MapState> _maps = [];
    private readonly KeyedSerialiser _sends = new();

    public async Task PublishAsync(ConstructLiveChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.IsEmpty || !viewers.HasAnyViewers())
        {
            return;
        }

        try
        {
            var projects = await ProjectsOfAsync(changes, cancellationToken);
            foreach (var branch in changes.Branches)
            {
                if (projects.TryGetValue(branch.RunId, out var projectId) && viewers.HasViewers(projectId))
                {
                    var message = new PipelineStageBranchUpdatedMessage(
                        projectId, branch.RunId, branch.ExecutionId, branch.StageId, branch.Branch, branch.ExitedAt, branch.Outcome);
                    await hub.Clients.Group(ProjectsHub.GroupFor(projectId)).SendAsync(ProjectsHub.StageBranchUpdatedMethod, message, cancellationToken);
                }
            }

            foreach (var map in changes.Maps.DistinctBy(m => m.MapExecutionId))
            {
                if (projects.TryGetValue(map.RunId, out var projectId) && viewers.HasViewers(projectId))
                {
                    await SignalMapAsync(map.MapExecutionId, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            // After a committed save nothing may escape, a cancellation included: the write has already succeeded.
            LogPushFailed(logger, ex);
        }
    }

    /// <summary>The project of each changed run: from the unit of work when it tracked the run, else one query.</summary>
    private async Task<Dictionary<Guid, Guid>> ProjectsOfAsync(ConstructLiveChanges changes, CancellationToken cancellationToken)
    {
        var known = changes.Branches.Select(b => (b.RunId, b.ProjectId))
            .Concat(changes.Maps.Select(m => (m.RunId, m.ProjectId)))
            .ToList();
        var projects = known.Where(k => k.ProjectId is not null).DistinctBy(k => k.RunId).ToDictionary(k => k.RunId, k => k.ProjectId!.Value);
        var missing = known.Select(k => k.RunId).Where(id => !projects.ContainsKey(id)).Distinct().ToList();
        if (missing.Count > 0)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IEngineDbContext>();
            foreach (var run in await db.PipelineRuns.AsNoTracking().Where(r => missing.Contains(r.Id)).Select(r => new { r.Id, r.ProjectId }).ToListAsync(cancellationToken))
            {
                projects[run.Id] = run.ProjectId;
            }
        }

        return projects;
    }

    private async Task SignalMapAsync(Guid mapId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        (MapProgressSend Send, TimeSpan Delay) decision;
        lock (_lock)
        {
            Sweep(now);
            var state = _maps.GetValueOrDefault(mapId);
            decision = MapProgressThrottle.Decide(state?.LastSentAt, state?.TrailingScheduled ?? false, now, MapProgressThrottle.Interval);
            if (decision.Send == MapProgressSend.Now)
            {
                _maps[mapId] = new MapState(now, TrailingScheduled: false);
            }
            else if (decision.Send == MapProgressSend.Trailing)
            {
                _maps[mapId] = state! with { TrailingScheduled = true };
            }
        }

        if (decision.Send == MapProgressSend.Now)
        {
            await SendMapAsync(mapId, cancellationToken);
        }
        else if (decision.Send == MapProgressSend.Trailing)
        {
            _ = TrailingAsync(mapId, decision.Delay);
        }
    }

    private async Task TrailingAsync(Guid mapId, TimeSpan delay)
    {
        var stopping = lifetime.ApplicationStopping;
        try
        {
            await Task.Delay(delay, time, stopping);
            lock (_lock)
            {
                _maps[mapId] = new MapState(time.GetUtcNow(), TrailingScheduled: false);
            }

            await SendMapAsync(mapId, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Shutting down: the clients reconnect and refetch.
        }
        catch (Exception ex)
        {
            LogPushFailed(logger, ex);
        }
    }

    /// <summary>
    /// Reads the map's counts and sends them, one map at a time (<see cref="KeyedSerialiser"/>): a send never overlaps
    /// another of the same map, so a later read is always sent after an earlier one and the final counts arrive last.
    /// </summary>
    private Task SendMapAsync(Guid mapId, CancellationToken cancellationToken) =>
        _sends.RunAsync(mapId, () => ReadAndSendMapAsync(mapId, cancellationToken));

    private async Task ReadAndSendMapAsync(Guid mapId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<PipelineRunReader>();
        if (await reader.MapProgressMessageAsync(mapId, cancellationToken) is not { } message)
        {
            return;
        }

        if (MapProgressThrottle.Settled(message.Progress))
        {
            lock (_lock)
            {
                if (_maps.TryGetValue(mapId, out var state) && !state.TrailingScheduled)
                {
                    _maps.Remove(mapId);
                }
            }
        }

        if (viewers.HasViewers(message.ProjectId))
        {
            await hub.Clients.Group(ProjectsHub.GroupFor(message.ProjectId)).SendAsync(ProjectsHub.MapProgressMethod, message, cancellationToken);
        }
    }

    /// <summary>Drops the state of maps not sent for a while (a map that stopped without settling, e.g. a cancelled run).</summary>
    private void Sweep(DateTimeOffset now)
    {
        foreach (var id in _maps.Where(m => !m.Value.TrailingScheduled && now - m.Value.LastSentAt > StaleAfter).Select(m => m.Key).ToList())
        {
            _maps.Remove(id);
        }
    }

    private sealed record MapState(DateTimeOffset LastSentAt, bool TrailingScheduled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline branch or map live update could not be pushed")]
    private static partial void LogPushFailed(ILogger logger, Exception exception);
}

/// <summary>
/// FR-363: collects the branch and map item stage executions a save enters or leaves, and hands them to
/// <see cref="PipelineConstructLivePublisher"/> once committed: after a successful save, or, inside an explicit
/// transaction, when it commits. A failed save or a rolled-back transaction sends nothing. Singleton; per-context state
/// lives in a weak table, released with the context.
/// </summary>
public sealed class PipelineConstructLiveInterceptor(PipelineConstructLivePublisher publisher) : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly ConditionalWeakTable<DbContext, State> _states = [];

    /// <summary>The branch and map item changes of the starting save: rows added, or with <c>exited_at</c> set in it.</summary>
    public static ConstructLiveChanges Changes(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var branches = new List<BranchLiveChange>();
        var maps = new List<MapLiveChange>();
        Dictionary<Guid, Guid>? runs = null;
        foreach (var entry in context.ChangeTracker.Entries<StageExecution>())
        {
            var e = entry.Entity;
            var changed = entry.State == EntityState.Added
                || (entry.State == EntityState.Modified && entry.Property(x => x.ExitedAt).IsModified);
            if (!changed || (e.ParentExecutionId is null && e.Branch is null))
            {
                continue;
            }

            runs ??= context.ChangeTracker.Entries<PipelineRun>().GroupBy(r => r.Entity.Id).ToDictionary(g => g.Key, g => g.First().Entity.ProjectId);
            var projectId = runs.TryGetValue(e.PipelineRunId, out var known) ? known : (Guid?)null;
            if (e.ParentExecutionId is { } parent)
            {
                maps.Add(new MapLiveChange(projectId, e.PipelineRunId, parent));
            }
            else
            {
                branches.Add(new BranchLiveChange(projectId, e.PipelineRunId, e.Id, e.StageId, e.Branch!, e.ExitedAt, e.Outcome));
            }
        }

        return new ConstructLiveChanges(branches, [.. maps.DistinctBy(m => m.MapExecutionId)]);
    }

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData?.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData?.Context);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (Saved(eventData?.Context) is { } changes)
        {
            _ = publisher.PublishAsync(changes, CancellationToken.None);
        }

        return result;
    }

    public async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (Saved(eventData?.Context) is { } changes)
        {
            await publisher.PublishAsync(changes, CancellationToken.None);
        }

        return result;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => StateOf(eventData?.Context)?.Saving.Clear();

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        StateOf(eventData?.Context)?.Saving.Clear();
        return Task.CompletedTask;
    }

    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        StateOf(eventData?.Context)?.AwaitingCommit.Clear();
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        StateOf(eventData?.Context)?.AwaitingCommit.Clear();
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (Committed(eventData?.Context) is { } changes)
        {
            _ = publisher.PublishAsync(changes, CancellationToken.None);
        }
    }

    public async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (Committed(eventData?.Context) is { } changes)
        {
            await publisher.PublishAsync(changes, CancellationToken.None);
        }
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => StateOf(eventData?.Context)?.AwaitingCommit.Clear();

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        StateOf(eventData?.Context)?.AwaitingCommit.Clear();
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => StateOf(eventData?.Context)?.AwaitingCommit.Clear();

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        StateOf(eventData?.Context)?.AwaitingCommit.Clear();
        return Task.CompletedTask;
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var changes = Changes(context);
        if (changes.IsEmpty && !_states.TryGetValue(context, out _))
        {
            return;
        }

        var state = _states.GetOrCreateValue(context);
        state.Saving.Clear();
        if (!changes.IsEmpty)
        {
            state.Saving.Add(changes);
        }
    }

    /// <summary>After a successful save: the changes to publish now, or none while an explicit transaction is open.</summary>
    private ConstructLiveChanges? Saved(DbContext? context)
    {
        if (context is null || StateOf(context) is not { Saving.Count: > 0 } state)
        {
            return null;
        }

        var saved = state.Saving.ToList();
        state.Saving.Clear();
        if (context.Database.CurrentTransaction is not null)
        {
            state.AwaitingCommit.AddRange(saved);
            return null;
        }

        return Merge(saved);
    }

    private ConstructLiveChanges? Committed(DbContext? context)
    {
        if (StateOf(context) is not { AwaitingCommit.Count: > 0 } state)
        {
            return null;
        }

        var committed = state.AwaitingCommit.ToList();
        state.AwaitingCommit.Clear();
        return Merge(committed);
    }

    private static ConstructLiveChanges Merge(List<ConstructLiveChanges> all) =>
        all.Count == 1 ? all[0] : new ConstructLiveChanges([.. all.SelectMany(c => c.Branches)], [.. all.SelectMany(c => c.Maps).DistinctBy(m => m.MapExecutionId)]);

    private State? StateOf(DbContext? context) => context is not null && _states.TryGetValue(context, out var state) ? state : null;

    private sealed class State
    {
        public List<ConstructLiveChanges> Saving { get; } = [];

        public List<ConstructLiveChanges> AwaitingCommit { get; } = [];
    }
}
