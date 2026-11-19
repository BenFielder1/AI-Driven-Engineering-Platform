namespace Tevrin.Engine.Runtime.Llm;

/// <summary>
/// FR-297: notes when the first streamed output of an LLM call arrives, for an agent run's <c>latency_ms</c>, without
/// changing the provider abstraction or the request. The caller starts a probe around the call; streamed provider calls
/// (<see cref="LlmStream"/>) mark it at the first event that carries output (text, a refusal or a tool call). A call that
/// isn't streamed (the Fake provider, or a server that answers with a whole JSON body) never marks it.
/// </summary>
public sealed class LlmFirstTokenProbe : IDisposable
{
    private static readonly AsyncLocal<LlmFirstTokenProbe?> Active = new();

    private readonly TimeProvider _time;
    private readonly LlmFirstTokenProbe? _previous;
    private long? _firstToken;

    private LlmFirstTokenProbe(TimeProvider time)
    {
        _time = time;
        _previous = Active.Value;
    }

    /// <summary>The <see cref="TimeProvider.GetTimestamp"/> of the first streamed output, or null.</summary>
    public long? FirstTokenTimestamp => _firstToken;

    /// <summary>Starts a probe for the calls made from this async flow until it is disposed.</summary>
    public static LlmFirstTokenProbe Start(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var probe = new LlmFirstTokenProbe(time);
        Active.Value = probe;
        return probe;
    }

    /// <summary>Milliseconds from <paramref name="started"/> (a <see cref="TimeProvider.GetTimestamp"/>) to the first streamed
    /// output, or null when nothing was streamed.</summary>
    public long? LatencyMs(long started) =>
        FirstTokenTimestamp is { } first ? Math.Max(0L, (long)_time.GetElapsedTime(started, first).TotalMilliseconds) : null;

    /// <summary>Marks the active probe (first call only).</summary>
    internal static void MarkFirstToken()
    {
        if (Active.Value is { } probe && probe._firstToken is null)
        {
            probe._firstToken = probe._time.GetTimestamp();
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(Active.Value, this))
        {
            Active.Value = _previous;
        }
    }
}
