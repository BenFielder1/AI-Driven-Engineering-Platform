using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// FR-358–FR-360 (M71b): a pack registering only tools, one per tier rule, each recording into <see cref="ToolRunLog"/>
/// when it runs, so a test can prove a refused tool never ran. <see cref="Peek"/> is <c>write</c>: a pinned definition
/// that listed it under <c>max_tier: read</c> stands for a run pinned before the pack raised its tier.
/// </summary>
internal sealed class TierToolsPack : IPipelinePack
{
    public const string Scan = "tiers.scan";
    public const string Peek = "tiers.peek";
    public const string Wipe = "tiers.wipe";
    public const string Send = "tiers.send";

    public string Id => "tiers";

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddTool(Scan, ToolTier.Read);
        registry.AddTool(Peek, ToolTier.Write);
        registry.AddTool(Wipe, ToolTier.Destructive);
        registry.AddTool(Send, ToolTier.Read, external: true);
    }

    /// <summary>Registers the pack, its tools and the shared <paramref name="log"/>.</summary>
    public static void AddTo(IServiceCollection services, ToolRunLog log)
    {
        services.AddPipelinePack(new TierToolsPack());
        services.AddSingleton(log);
        services.AddPipelineTool<ScanTool>(Scan);
        services.AddPipelineTool<PeekTool>(Peek);
        services.AddPipelineTool<WipeTool>(Wipe);
        services.AddPipelineTool<SendTool>(Send);
    }
}

/// <summary>The names of the tools that ran, in order.</summary>
internal sealed class ToolRunLog
{
    private readonly ConcurrentQueue<string> _ran = new();

    public IReadOnlyList<string> Ran => [.. _ran];

    public void Add(string tool) => _ran.Enqueue(tool);
}

/// <summary>A tool that records that it ran and answers <c>ran &lt;name&gt;</c>.</summary>
internal abstract class RecordingTool(string name, ToolRunLog log) : IAgentTool
{
    public ToolSpec Spec { get; } = new(name, "Records that it ran.", JsonDocument.Parse("""{"type":"object"}""").RootElement);

    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken cancellationToken)
    {
        log.Add(Spec.Name);
        return Task.FromResult(new ToolResult("ran " + Spec.Name));
    }
}

internal sealed class ScanTool(ToolRunLog log) : RecordingTool("scan", log);

internal sealed class PeekTool(ToolRunLog log) : RecordingTool("peek", log);

internal sealed class WipeTool(ToolRunLog log) : RecordingTool("wipe", log);

internal sealed class SendTool(ToolRunLog log) : RecordingTool("send", log);

/// <summary>A logger keeping each formatted message, to check what a refusal logs.</summary>
internal sealed class ListLogger : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
