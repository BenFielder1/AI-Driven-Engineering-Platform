using System.Data.Common;
using System.Runtime.CompilerServices;
using Aide.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aide.Infrastructure.Persistence;

/// <summary>
/// Receives project events (rows of <c>events</c> with a project id) once they are committed (FR-46). Implemented by the
/// API's live-update hub; the default does nothing (CLI, design-time tools).
/// </summary>
public interface IProjectEventPublisher
{
    /// <summary>Called after the transaction that saved <paramref name="events"/> committed. Must not throw.</summary>
    Task PublishAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken);
}

/// <summary>The default publisher: nothing listens.</summary>
public sealed class NullProjectEventPublisher : IProjectEventPublisher
{
    public Task PublishAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// FR-46: hands every committed project event to the <see cref="IProjectEventPublisher"/>, whoever wrote it (an endpoint,
/// the job worker, the seed). Events added with <see cref="EventLog.AddEvent"/> are collected when <c>SaveChanges</c>
/// starts and published when it succeeds — or, inside an explicit transaction, when that transaction commits. Nothing is
/// published for a failed save or a rolled-back transaction, so a push always describes committed state.
/// </summary>
/// <remarks>Singleton; per-context state lives in a weak table, so it is released with the context.</remarks>
public sealed class ProjectEventPublishingInterceptor(IProjectEventPublisher publisher) : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly ConditionalWeakTable<DbContext, State> _states = [];

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (Saved(eventData.Context) is { Count: > 0 } events)
        {
            // Synchronous saves aren't used for event-writing paths; publish without blocking the caller.
            _ = publisher.PublishAsync(events, CancellationToken.None);
        }

        return result;
    }

    public async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (Saved(eventData.Context) is { Count: > 0 } events)
        {
            await publisher.PublishAsync(events, CancellationToken.None);
        }

        return result;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => Discard(eventData.Context);

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        ResetTransaction(eventData.Context);
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        ResetTransaction(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (Committed(eventData.Context) is { Count: > 0 } events)
        {
            _ = publisher.PublishAsync(events, CancellationToken.None);
        }
    }

    public async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (Committed(eventData.Context) is { Count: > 0 } events)
        {
            await publisher.PublishAsync(events, CancellationToken.None);
        }
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => ResetTransaction(eventData.Context);

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ResetTransaction(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => ResetTransaction(eventData.Context);

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ResetTransaction(eventData.Context);
        return Task.CompletedTask;
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var state = _states.GetOrCreateValue(context);
        state.Saving =
        [
            .. context.ChangeTracker.Entries<AuditEvent>()
                .Where(e => e.State == EntityState.Added && e.Entity.ProjectId is not null)
                .Select(e => e.Entity),
        ];
    }

    /// <summary>After a successful save: the events to publish now, or none if an explicit transaction is still open.</summary>
    private List<AuditEvent>? Saved(DbContext? context)
    {
        if (context is null || !_states.TryGetValue(context, out var state) || state.Saving.Count == 0)
        {
            return null;
        }

        var saved = state.Saving;
        state.Saving = [];
        if (context.Database.CurrentTransaction is not null)
        {
            state.AwaitingCommit.AddRange(saved);
            return null;
        }

        return saved;
    }

    private List<AuditEvent>? Committed(DbContext? context)
    {
        if (context is null || !_states.TryGetValue(context, out var state) || state.AwaitingCommit.Count == 0)
        {
            return null;
        }

        var committed = state.AwaitingCommit;
        state.AwaitingCommit = [];
        return committed;
    }

    private void Discard(DbContext? context)
    {
        if (context is not null && _states.TryGetValue(context, out var state))
        {
            state.Saving = [];
        }
    }

    private void ResetTransaction(DbContext? context)
    {
        if (context is not null && _states.TryGetValue(context, out var state))
        {
            state.AwaitingCommit = [];
        }
    }

    private sealed class State
    {
        public List<AuditEvent> Saving { get; set; } = [];

        public List<AuditEvent> AwaitingCommit { get; set; } = [];
    }
}
