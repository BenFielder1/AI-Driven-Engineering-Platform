using System.Collections;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.UnitTests.Agents;

/// <summary>
/// The engine's tables for the SDLC agent stage harness, never connected (as the engine tests' offline context):
/// <see cref="PipelineDefinitions"/> is an empty in-memory table, so a pinned hash that is neither registered nor cached
/// resolves to "not stored" at once (<c>DEFINITION_UNAVAILABLE</c>) instead of reaching for a database. Every other table
/// is a placeholder the harness never queries (its store, run data and context are in memory).
/// </summary>
internal sealed class OfflineEngineDbContext : DbContext, IEngineDbContext
{
    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<AuditEvent> Events => Set<AuditEvent>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    public DbSet<ApprovalLink> ApprovalLinks => Set<ApprovalLink>();

    public DbSet<PipelineDefinitionRecord> PipelineDefinitions { get; } = new EmptyDbSet<PipelineDefinitionRecord>();

    public DbSet<PipelineRun> PipelineRuns => Set<PipelineRun>();

    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();

    public DbSet<StageExecution> StageExecutions => Set<StageExecution>();

    public DbSet<Artifact> Artifacts => Set<Artifact>();

    public DbSet<ApprovalGate> ApprovalGates => Set<ApprovalGate>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.ApplyTevrinEngineConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyTevrinEngineModel();
}

/// <summary>An always-empty table that EF's async LINQ operators (<c>FirstOrDefaultAsync</c>, …) can query in memory.</summary>
internal sealed class EmptyDbSet<T> : DbSet<T>, IQueryable<T>, IAsyncEnumerable<T>
    where T : class
{
    private readonly IQueryable<T> _rows = Array.Empty<T>().AsQueryable();

    public override IEntityType EntityType => throw new NotSupportedException("An in-memory table has no model.");

    Type IQueryable.ElementType => _rows.ElementType;

    Expression IQueryable.Expression => _rows.Expression;

    IQueryProvider IQueryable.Provider => new AsyncQueryProvider(_rows.Provider);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _rows.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _rows.GetEnumerator();

    IAsyncEnumerator<T> IAsyncEnumerable<T>.GetAsyncEnumerator(CancellationToken cancellationToken) => new AsyncRows<T>(_rows).GetAsyncEnumerator(cancellationToken);
}

/// <summary>Runs queries in memory, and their async forms synchronously.</summary>
internal sealed class AsyncQueryProvider(IQueryProvider inner) : IAsyncQueryProvider
{
    public IQueryable CreateQuery(Expression expression) => inner.CreateQuery(expression);

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new AsyncRows<TElement>(inner.CreateQuery<TElement>(expression));

    public object? Execute(Expression expression) => inner.Execute(expression);

    public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        // TResult is Task<X>: run the query for X and wrap it.
        var resultType = typeof(TResult).GetGenericArguments()[0];
        var value = typeof(IQueryProvider).GetMethods()
            .Single(m => m.Name == nameof(IQueryProvider.Execute) && m.IsGenericMethod)
            .MakeGenericMethod(resultType)
            .Invoke(inner, [expression]);
        return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [value])!;
    }
}

/// <summary>A query over in-memory rows that EF's async operators accept.</summary>
internal sealed class AsyncRows<T>(IQueryable<T> rows) : IQueryable<T>, IAsyncEnumerable<T>
{
    public Type ElementType => rows.ElementType;

    public Expression Expression => rows.Expression;

    public IQueryProvider Provider => new AsyncQueryProvider(rows.Provider);

    public IEnumerator<T> GetEnumerator() => rows.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }

        await Task.CompletedTask;
    }
}
