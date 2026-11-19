using System.Text.Json;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>One tool call made during an agent run, as recorded in <c>agent_runs.tool_calls</c>.</summary>
public sealed record AgentToolCallRecord(string Tool, string Arguments, bool IsError);

/// <summary>
/// FR-280, FR-295: runs a stage's <see cref="IAgentTool"/>s (resolved by name from DI) in the LLM tool loop. A call to a
/// tool the stage doesn't list, arguments that aren't JSON, or a tool that throws is an error result, never an exception.
/// </summary>
public sealed class AgentToolExecutor : ILlmToolExecutor
{
    private readonly Dictionary<string, IAgentTool> _tools;
    private readonly List<AgentToolCallRecord> _calls = [];

    public AgentToolExecutor(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.GroupBy(t => t.Spec.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Definitions = [.. _tools.Values.Select(t => new LlmToolDefinition(t.Spec.Name, t.Spec.Description, t.Spec.InputSchema))];
    }

    public IReadOnlyList<LlmToolDefinition> Definitions { get; }

    public bool LimitReached => false;

    /// <summary>Every call made, in order.</summary>
    public IReadOnlyList<AgentToolCallRecord> Calls => _calls;

    public async Task<LlmToolResult> ExecuteAsync(LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        var result = await InvokeAsync(toolCall, cancellationToken);
        _calls.Add(new AgentToolCallRecord(toolCall.Name, toolCall.ArgumentsJson, result.IsError));
        return new LlmToolResult(toolCall.Id, result.Content, result.IsError);
    }

    public Task<string?> FallbackContextAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>The calls as the JSON array stored on the agent run.</summary>
    public string CallsJson() => JsonSerializer.Serialize(_calls, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<ToolResult> InvokeAsync(LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(toolCall.Name, out var tool))
        {
            return new ToolResult($"Unknown tool '{toolCall.Name}'.", IsError: true);
        }

        JsonDocument args;
        try
        {
            args = JsonDocument.Parse(string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson);
        }
        catch (JsonException)
        {
            return new ToolResult("The arguments are not valid JSON.", IsError: true);
        }

        using (args)
        {
            try
            {
                return await tool.InvokeAsync(args.RootElement.Clone(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ToolResult($"The tool '{toolCall.Name}' failed.", IsError: true);
            }
        }
    }
}
