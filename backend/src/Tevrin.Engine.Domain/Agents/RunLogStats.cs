namespace Tevrin.Engine.Domain.Agents;

/// <summary>FR-297: a pipeline run's totals over its agent runs: cost (sum of <c>cost_estimate</c>) and tokens.</summary>
public sealed record RunTotals(int AgentRuns, decimal Cost, long InputTokens, long OutputTokens)
{
    public static RunTotals None { get; } = new(0, 0m, 0, 0);

    public long Tokens => InputTokens + OutputTokens;
}

/// <summary>FR-298: the sums of one (agent key, prompt version) group of agent runs, as aggregated by the database.
/// <see cref="Finished"/> counts the succeeded and failed runs (queued, running and cancelled runs have no result).</summary>
public sealed record PromptRunSums(
    string Agent,
    string PromptVersion,
    int Runs,
    int Finished,
    int Failed,
    long InputTokens,
    long OutputTokens,
    decimal Cost,
    int LatencyRuns,
    long LatencyMs,
    long DurationMs);

/// <summary>FR-298: the prompt comparison row for one (agent key, prompt version): run count, averages per run, and the
/// failure rate (failed / finished, where finished = succeeded + failed; 0 when none finished). <see cref="AvgLatencyMs"/> averages only runs that recorded a latency; null when none did.</summary>
public sealed record PromptStats(
    string Agent,
    string PromptVersion,
    int Runs,
    double AvgInputTokens,
    double AvgOutputTokens,
    decimal AvgCost,
    double? AvgLatencyMs,
    double AvgDurationMs,
    double FailureRate);

/// <summary>FR-298: pure math for the prompt comparison.</summary>
public static class RunLogStats
{
    /// <summary>Decimal places kept on the average cost.</summary>
    public const int CostDecimals = 6;

    /// <summary>The averages and failure rate of <paramref name="sums"/>; a group without runs averages to zero.</summary>
    public static PromptStats Summarise(PromptRunSums sums)
    {
        ArgumentNullException.ThrowIfNull(sums);
        var runs = sums.Runs;
        return new PromptStats(
            sums.Agent,
            sums.PromptVersion,
            runs,
            Average(sums.InputTokens, runs),
            Average(sums.OutputTokens, runs),
            runs == 0 ? 0m : Math.Round(sums.Cost / runs, CostDecimals, MidpointRounding.AwayFromZero),
            sums.LatencyRuns == 0 ? null : Average(sums.LatencyMs, sums.LatencyRuns),
            Average(sums.DurationMs, runs),
            sums.Finished == 0 ? 0d : (double)sums.Failed / sums.Finished);
    }

    /// <summary>Groups sorted by agent key, then prompt version (ordinal).</summary>
    public static IReadOnlyList<PromptStats> Summarise(IEnumerable<PromptRunSums> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return
        [
            .. groups
                .Select(Summarise)
                .OrderBy(s => s.Agent, StringComparer.Ordinal)
                .ThenBy(s => s.PromptVersion, StringComparer.Ordinal),
        ];
    }

    private static double Average(long total, int count) => count == 0 ? 0d : (double)total / count;
}
