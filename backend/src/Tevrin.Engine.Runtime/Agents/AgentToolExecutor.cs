using System.Text.Json;
using System.Text.RegularExpressions;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// One tool call made during an agent run, as recorded in <c>agent_runs.tool_calls</c>. <see cref="Arguments"/> is
/// secret-redacted (<see cref="SecretRedaction.Redact"/>) and at most <see cref="AgentToolExecutor.MaxLoggedArgumentLength"/>
/// characters.
/// </summary>
public sealed record AgentToolCallRecord(string Tool, string Arguments, bool IsError);

/// <summary>
/// FR-280, FR-295: runs a stage's <see cref="IAgentTool"/>s (resolved by name from DI) in the LLM tool loop. A call to a
/// tool the stage doesn't list, arguments that aren't JSON, or a tool that throws is an error result, never an exception,
/// unless the host's <see cref="AgentToolSession"/> (FR-317, FR-319) says otherwise; the session also refuses calls at its
/// limit, supplies the fallback context and writes the log.
/// </summary>
public sealed class AgentToolExecutor : ILlmToolExecutor
{
    /// <summary>The longest logged arguments text (as 0.6.1's repo tools).</summary>
    public const int MaxLoggedArgumentLength = 2000;

    /// <summary>The longest logged tool name.</summary>
    public const int MaxLoggedToolNameLength = 60;

    private readonly Dictionary<string, IAgentTool> _tools;
    private readonly List<AgentToolCallRecord> _calls = [];
    private readonly AgentToolSession _session;
    private bool _refused;

    public AgentToolExecutor(IEnumerable<IAgentTool> tools, AgentToolSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _session = session ?? AgentToolSession.Default;
        _tools = tools.GroupBy(t => t.Spec.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Definitions = [.. _tools.Values.Select(t => new LlmToolDefinition(t.Spec.Name, t.Spec.Description, t.Spec.InputSchema))];
    }

    /// <summary>
    /// FR-317: no tools for this call (as <see cref="AgentCallOptions.Tools"/>, e.g. no repository snapshot): the call is a
    /// plain completion, unlike <c>null</c>, which means the stage's tools. Stateless, so it is safe to share.
    /// </summary>
    public static ILlmToolExecutor None { get; } = new NoToolExecutor();

    /// <summary>The placeholder logged when redacting the arguments timed out (as the recorded input does).</summary>
    public const string RedactionTimedOut = "[redaction timed out]";

    public IReadOnlyList<LlmToolDefinition> Definitions { get; }

    /// <summary>FR-317: a call was refused by the session, or the session says its limit is reached.</summary>
    public bool LimitReached => _refused || _session.LimitReached;

    /// <summary>Every call made, in order.</summary>
    public IReadOnlyList<AgentToolCallRecord> Calls => _calls;

    public async Task<LlmToolResult> ExecuteAsync(LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ToolResult result;
        if (_session.BeforeCall(toolCall) is { } refusal)
        {
            _refused = true;
            result = refusal with { IsError = true };
        }
        else
        {
            try
            {
                result = _session.AfterCall(toolCall, await InvokeAsync(toolCall, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // FR-317: a tool exception the session rethrows (failing the run) is still in the log.
                Record(toolCall, isError: true);
                throw;
            }
        }

        Record(toolCall, result.IsError);
        return new LlmToolResult(toolCall.Id, result.Content, result.IsError);
    }

    private void Record(LlmToolCall toolCall, bool isError) =>
        _calls.Add(new AgentToolCallRecord(Shorten(toolCall.Name, MaxLoggedToolNameLength), LoggedArguments(toolCall.ArgumentsJson), isError));

    public Task<string?> FallbackContextAsync(CancellationToken cancellationToken) => _session.FallbackContextAsync(cancellationToken);

    /// <summary>The calls as the JSON array stored on the agent run (the session's format).</summary>
    public string CallsJson() => _session.CallsJson(_calls);

    /// <summary>NFR-SEC: arguments as logged: secret-redacted, then capped; <see cref="RedactionTimedOut"/> when redacting
    /// them timed out, so writing the log never fails the run.</summary>
    public static string LoggedArguments(string? argumentsJson) => LoggedArguments(argumentsJson, text => SecretRedaction.Redact(text));

    internal static string LoggedArguments(string? argumentsJson, Func<string, string> redact)
    {
        try
        {
            return Shorten(redact(argumentsJson ?? string.Empty), MaxLoggedArgumentLength);
        }
        catch (RegexMatchTimeoutException)
        {
            return RedactionTimedOut;
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private async Task<ToolResult> InvokeAsync(LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(toolCall.Name, out var tool))
        {
            return _session.UnknownTool(toolCall);
        }

        try
        {
            if (tool is IRawArgumentsAgentTool raw)
            {
                return await raw.InvokeRawAsync(toolCall.ArgumentsJson, cancellationToken);
            }

            JsonDocument args;
            try
            {
                args = JsonDocument.Parse(string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson);
            }
            catch (JsonException)
            {
                return _session.InvalidArguments(toolCall);
            }

            using (args)
            {
                return await tool.InvokeAsync(args.RootElement.Clone(), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // FR-317: rethrown as is, so the run fails as 0.6.1's did (PROVIDER_ERROR, transient by exception type).
            var handled = _session.OnToolException(toolCall, ex);
            if (handled is null)
            {
                throw;
            }

            return handled;
        }
    }

    /// <summary>FR-317: offers no tools and keeps no state (<see cref="None"/>).</summary>
    private sealed class NoToolExecutor : ILlmToolExecutor
    {
        public IReadOnlyList<LlmToolDefinition> Definitions => [];

        public bool LimitReached => false;

        public Task<LlmToolResult> ExecuteAsync(LlmToolCall toolCall, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(toolCall);
            return Task.FromResult(new LlmToolResult(toolCall.Id, $"Unknown tool '{toolCall.Name}'.", IsError: true));
        }

        public Task<string?> FallbackContextAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
