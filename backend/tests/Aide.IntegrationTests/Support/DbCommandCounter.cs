using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aide.IntegrationTests.Support;

/// <summary>Counts the database commands a host executes (NFR-PERF2: fixed query counts).</summary>
public sealed class DbCommandCounter : DbCommandInterceptor
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>Latency percentiles of repeated requests (NFR-PERF2).</summary>
public static class Latency
{
    /// <summary>The nearest-rank <paramref name="percentile"/> (0-100) of <paramref name="samples"/>.</summary>
    public static TimeSpan Percentile(IReadOnlyCollection<TimeSpan> samples, double percentile)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var ordered = samples.Order().ToList();
        var rank = (int)Math.Ceiling(percentile / 100 * ordered.Count);
        return ordered[Math.Clamp(rank - 1, 0, ordered.Count - 1)];
    }

    /// <summary>Times <paramref name="count"/> sequential calls of <paramref name="call"/>.</summary>
    public static async Task<List<TimeSpan>> SampleAsync(int count, Func<Task> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var samples = new List<TimeSpan>(count);
        for (var i = 0; i < count; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await call();
            samples.Add(watch.Elapsed);
        }

        return samples;
    }
}
