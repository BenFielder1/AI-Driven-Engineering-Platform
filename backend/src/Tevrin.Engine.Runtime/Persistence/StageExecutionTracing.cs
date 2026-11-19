using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Runtime.Persistence;

/// <summary>
/// §8: the stage executions a unit of work closes, traced (<see cref="EngineTelemetry.TraceStageExecution"/>) only once
/// they are committed: after a successful save, or, inside an explicit transaction, when it commits. A failed save or a
/// rolled-back transaction traces nothing.
/// </summary>
public sealed class StageSpanBuffer
{
    private List<StageExecution> _saving = [];
    private List<StageExecution> _awaitingCommit = [];

    /// <summary>The executions the starting save closes.</summary>
    public void Collect(IEnumerable<StageExecution> closing)
    {
        ArgumentNullException.ThrowIfNull(closing);
        _saving = [.. closing];
    }

    /// <summary>The save succeeded: trace now, or hold until <see cref="Committed"/> while a transaction is open.</summary>
    public void Saved(bool inTransaction)
    {
        var saved = _saving;
        _saving = [];
        if (inTransaction)
        {
            _awaitingCommit.AddRange(saved);
            return;
        }

        Trace(saved);
    }

    public void Failed() => _saving = [];

    public void Committed()
    {
        var committed = _awaitingCommit;
        _awaitingCommit = [];
        Trace(committed);
    }

    /// <summary>A transaction started, rolled back or failed: nothing held is traced.</summary>
    public void ResetTransaction() => _awaitingCommit = [];

    private static void Trace(List<StageExecution> executions)
    {
        foreach (var execution in executions)
        {
            EngineTelemetry.TraceStageExecution(execution);
        }
    }
}

/// <summary>
/// §8: one span per stage execution, emitted when the save (or transaction) that closes it commits. Register it after the
/// interceptor that writes stage executions, so it sees the rows that save closes. Singleton; per-context state lives in a
/// weak table, released with the context.
/// </summary>
public sealed class StageExecutionTracingInterceptor : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly ConditionalWeakTable<DbContext, StageSpanBuffer> _buffers = [];

    /// <summary>The executions a save closes: added already closed, or with <c>exited_at</c> set in this unit of work.</summary>
    public static IEnumerable<StageExecution> Closing(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ChangeTracker.Entries<StageExecution>()
            .Where(e => e.Entity.ExitedAt is not null
                && (e.State == EntityState.Added || (e.State == EntityState.Modified && e.Property(x => x.ExitedAt).IsModified)))
            .Select(e => e.Entity);
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
        Saved(eventData?.Context);
        return result;
    }

    public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Saved(eventData?.Context);
        return ValueTask.FromResult(result);
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => Buffer(eventData?.Context)?.Failed();

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Buffer(eventData?.Context)?.Failed();
        return Task.CompletedTask;
    }

    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Buffer(eventData?.Context)?.ResetTransaction();
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        Buffer(eventData?.Context)?.ResetTransaction();
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Buffer(eventData?.Context)?.Committed();

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Buffer(eventData?.Context)?.Committed();
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Buffer(eventData?.Context)?.ResetTransaction();

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Buffer(eventData?.Context)?.ResetTransaction();
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => Buffer(eventData?.Context)?.ResetTransaction();

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Buffer(eventData?.Context)?.ResetTransaction();
        return Task.CompletedTask;
    }

    private void Collect(DbContext? context)
    {
        if (context is not null)
        {
            _buffers.GetOrCreateValue(context).Collect(Closing(context));
        }
    }

    private void Saved(DbContext? context)
    {
        if (context is not null)
        {
            Buffer(context)?.Saved(context.Database.CurrentTransaction is not null);
        }
    }

    private StageSpanBuffer? Buffer(DbContext? context) =>
        context is not null && _buffers.TryGetValue(context, out var buffer) ? buffer : null;
}
