using System.Text.Json;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>Wraps the Fake provider and records every plain and tool-turn request (FR-317 tests).</summary>
internal sealed class RecordingToolProvider(FakeLlmProvider inner, bool supportsTools = true) : ILlmProvider, IToolCallingLlmProvider
{
    public LlmProvider Kind => LlmProvider.Fake;

    public List<LlmRequest> Plain { get; } = [];

    public List<LlmToolRequest> Turns { get; } = [];

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Plain.Add(request);
        return inner.CompleteAsync(request, cancellationToken);
    }

    public Task<bool> SupportsToolsAsync(LlmRequest request, CancellationToken cancellationToken) => Task.FromResult(supportsTools);

    public Task<LlmToolTurn> CompleteTurnAsync(LlmToolRequest toolRequest, CancellationToken cancellationToken)
    {
        Turns.Add(toolRequest);
        return inner.CompleteTurnAsync(toolRequest, cancellationToken);
    }
}

/// <summary>An <see cref="AgentToolSession"/> whose every hook is configurable; counts the calls that ran.</summary>
internal sealed class ConfigurableToolSession : AgentToolSession
{
    public int? RefuseFromCall { get; init; }

    public string RefusalText { get; init; } = "LIMIT: Answer now with what you have.";

    public string? Fallback { get; init; }

    public bool Rethrow { get; init; }

    public string? LogJson { get; init; }

    public int Started { get; private set; }

    public int Ran { get; private set; }

    public override ToolResult? BeforeCall(LlmToolCall toolCall) =>
        ++Started >= RefuseFromCall ? new ToolResult(RefusalText) : null;

    public override ToolResult AfterCall(LlmToolCall toolCall, ToolResult result)
    {
        Ran++;
        return result;
    }

    public override ToolResult UnknownTool(LlmToolCall toolCall) => new($"INVALID_ARGUMENT: Unknown tool '{toolCall.Name}'. Use lookup.", IsError: true);

    public override ToolResult InvalidArguments(LlmToolCall toolCall) => new("INVALID_ARGUMENT: The arguments must be a JSON object.", IsError: true);

    public override ToolResult? OnToolException(LlmToolCall toolCall, Exception exception) =>
        Rethrow ? null : base.OnToolException(toolCall, exception);

    public override Task<string?> FallbackContextAsync(CancellationToken cancellationToken) => Task.FromResult(Fallback);

    public override string CallsJson(IReadOnlyList<AgentToolCallRecord> calls) => LogJson ?? base.CallsJson(calls);
}

/// <summary>An extension that sets the call's tool options and transforms the prompt (FR-317, FR-319).</summary>
internal sealed class ToolOptionsExtension : AgentStageExtension
{
    public override string PipelineId => "fixture-review";

    public AgentToolSession? Session { get; init; }

    public ILlmToolExecutor? Tools { get; init; }

    public Func<RenderedPrompt, RenderedPrompt>? Transform { get; init; }

    public override Task<AgentCallOptions> CallOptionsAsync(AgentAttempt attempt, CancellationToken cancellationToken) =>
        Task.FromResult(new AgentCallOptions { ToolSession = Session, Tools = Tools });

    public override Task<RenderedPrompt> TransformPromptAsync(AgentAttempt attempt, RenderedPrompt prompt, CancellationToken cancellationToken) =>
        Task.FromResult(Transform is null ? prompt : Transform(prompt));
}

/// <summary>A tool named <c>lookup</c> that throws.</summary>
internal sealed class ThrowingTool : IAgentTool
{
    public ToolSpec Spec { get; } = new("lookup", "Throws.", JsonDocument.Parse("""{"type":"object"}""").RootElement);

    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("tool broke");
}

/// <summary>A tool that takes the raw arguments text and records it.</summary>
internal sealed class RawTool : IRawArgumentsAgentTool
{
    public ToolSpec Spec { get; } = new("raw", "Raw arguments.", JsonDocument.Parse("""{"type":"object"}""").RootElement);

    public List<string> Received { get; } = [];

    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Parsed arguments must not be used for a raw tool.");

    public Task<ToolResult> InvokeRawAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        Received.Add(argumentsJson);
        return Task.FromResult(new ToolResult("INVALID_ARGUMENT: 'path' is required.", IsError: true));
    }
}
