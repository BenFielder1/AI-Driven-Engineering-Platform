using System.Text.Json;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// FR-317, FR-319: the host's side of one call's <see cref="AgentToolExecutor"/>, passed in
/// <see cref="AgentCallOptions.ToolSession"/> (e.g. the SDLC's repo tool budget and log). Every default is the engine's
/// own behaviour. One call runs its tools in order; a session is not shared between calls.
/// </summary>
public class AgentToolSession
{
    /// <summary>The engine's behaviour.</summary>
    public static AgentToolSession Default { get; } = new();

    /// <summary>
    /// Before a call runs: a refusal (e.g. <c>TOOL_LIMIT</c>) is the call's error result, the tool isn't invoked, and the
    /// executor reports <see cref="ILlmToolExecutor.LimitReached"/> so the next turn must answer without tools
    /// (<see cref="LlmToolLoop"/>). Default: none.
    /// </summary>
    public virtual ToolResult? BeforeCall(LlmToolCall toolCall) => null;

    /// <summary>
    /// After a call that ran (or failed), the result sent to the model, e.g. cut to the remaining byte budget (default:
    /// unchanged). Not called for a refused call.
    /// </summary>
    public virtual ToolResult AfterCall(LlmToolCall toolCall, ToolResult result) => result;

    /// <summary>Whether the session's limit is reached (checked with refusals, before every turn). Default: <c>false</c>.</summary>
    public virtual bool LimitReached => false;

    /// <summary>The result of a call to a tool the stage doesn't offer.</summary>
    public virtual ToolResult UnknownTool(LlmToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        return new ToolResult($"Unknown tool '{toolCall.Name}'.", IsError: true);
    }

    /// <summary>
    /// The result of arguments that aren't JSON, for a tool that takes parsed arguments (a <see cref="IRawArgumentsAgentTool"/>
    /// gets the raw text instead).
    /// </summary>
    public virtual ToolResult InvalidArguments(LlmToolCall toolCall) => new("The arguments are not valid JSON.", IsError: true);

    /// <summary>
    /// A tool threw: the error result sent to the model (default), or <c>null</c> to rethrow, which fails the run with
    /// <c>PROVIDER_ERROR</c> through <see cref="AgentStageExtension.OnCallFailedAsync"/> (as 0.6.1 did).
    /// </summary>
    public virtual ToolResult? OnToolException(LlmToolCall toolCall, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        return new ToolResult($"The tool '{toolCall.Name}' failed.", IsError: true);
    }

    /// <summary>Context for a provider that can't call tools (e.g. a repository listing), or <c>null</c> (default).</summary>
    public virtual Task<string?> FallbackContextAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>The run's <c>tool_calls</c> JSON (default: the engine's records as web JSON).</summary>
    public virtual string CallsJson(IReadOnlyList<AgentToolCallRecord> calls) => JsonSerializer.Serialize(calls, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// FR-317: a tool that validates its own raw arguments (e.g. 0.6.1's <c>INVALID_ARGUMENT</c> text for malformed JSON),
/// so the executor passes the model's text through without parsing it.
/// </summary>
public interface IRawArgumentsAgentTool : IAgentTool
{
    Task<ToolResult> InvokeRawAsync(string argumentsJson, CancellationToken cancellationToken);
}
