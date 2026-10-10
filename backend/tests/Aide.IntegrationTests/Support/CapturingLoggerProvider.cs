using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Aide.IntegrationTests.Support;

/// <summary>One captured log entry: the rendered message, its structured values and the active scope values.</summary>
public sealed record CapturedLog(
    string Category,
    LogLevel Level,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> State,
    IReadOnlyList<KeyValuePair<string, object?>> Scopes,
    Exception? Exception)
{
    /// <summary>Everything that would reach a log sink, as one string (for "never logged" assertions).</summary>
    public string AllText =>
        string.Join(" | ", [Message, .. State.Select(kv => $"{kv.Key}={kv.Value}"), .. Scopes.Select(kv => $"{kv.Key}={kv.Value}"), Exception?.ToString() ?? string.Empty]);
}

/// <summary>Log entries captured from every host built by a factory (and its derived factories).</summary>
public sealed class LogSink
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public IReadOnlyList<CapturedLog> Entries => [.. _entries];

    internal void Add(CapturedLog entry) => _entries.Enqueue(entry);
}

/// <summary>Captures every log entry the app writes, with scopes (NFR-OBS / NFR-SEC log assertions). One instance per
/// host, because each host's logger factory hands its providers its own scope provider.</summary>
public sealed class CapturingLoggerProvider(LogSink sink) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    private readonly LogSink _sink = sink;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<KeyValuePair<string, object?>>();
            provider._scopes.ForEachScope(
                (scope, list) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        list.AddRange(pairs);
                    }
                    else if (scope is IEnumerable<KeyValuePair<string, object>> objects)
                    {
                        list.AddRange(objects.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)));
                    }
                    else if (scope is not null)
                    {
                        list.Add(new KeyValuePair<string, object?>("Scope", scope.ToString()));
                    }
                },
                scopes);

            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            provider._sink.Add(new CapturedLog(category, logLevel, formatter(state, exception), [.. values], scopes, exception));
        }
    }
}
