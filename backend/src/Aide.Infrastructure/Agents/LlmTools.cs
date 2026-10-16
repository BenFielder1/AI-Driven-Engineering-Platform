using System.Text.Json;

namespace Aide.Infrastructure.Agents;

/// <summary>A tool the model may call (FR-71): its name, what it does and its arguments as a JSON Schema object.</summary>
public sealed record LlmToolDefinition(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// One tool call the model made: the provider's call id (generated when the provider has none), the tool name and its
/// arguments as raw JSON text (an object, or whatever the model sent; the tool validates it).
/// </summary>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>The result sent back for <see cref="CallId"/>: text for the model, and whether the call failed.</summary>
public sealed record LlmToolResult(string CallId, string Content, bool IsError);

/// <summary>One completed model turn of a tool loop: what the model said, the calls it made and their results.</summary>
public sealed record LlmToolExchange(string AssistantText, IReadOnlyList<LlmToolCall> Calls, IReadOnlyList<LlmToolResult> Results);

/// <summary>
/// A tool-use request: the plain <see cref="Request"/> (system prompt, first messages, model, key), the tools, and the
/// exchanges so far, which each provider renders in its own shape after <see cref="LlmRequest.Messages"/>.
/// <see cref="ForceFinalAnswer"/> asks for an answer without further tool calls (Anthropic/OpenAI <c>tool_choice: none</c>;
/// Ollama: tools omitted), used once a tool returned <c>TOOL_LIMIT</c> or the turn cap is reached.
/// </summary>
public sealed class LlmToolRequest
{
    public required LlmRequest Request { get; init; }

    public required IReadOnlyList<LlmToolDefinition> Tools { get; init; }

    public IReadOnlyList<LlmToolExchange> Exchanges { get; init; } = [];

    public bool ForceFinalAnswer { get; init; }

    public override string ToString() => $"LlmToolRequest {{ {Request}, Tools = {Tools.Count}, Exchanges = {Exchanges.Count}, ForceFinalAnswer = {ForceFinalAnswer} }}";
}

/// <summary>A model turn: its text, the tool calls it wants (none: this is the final answer), tokens and stop reason.</summary>
public sealed record LlmToolTurn(string Text, IReadOnlyList<LlmToolCall> Calls, int InputTokens, int OutputTokens, string StopReason);

/// <summary>
/// The optional tool-use side of a provider (FR-71): Anthropic Messages <c>tools</c>/<c>tool_use</c>/<c>tool_result</c>,
/// OpenAI Chat Completions <c>tools</c>/<c>tool_calls</c>, Ollama <c>tools</c> (when the model advertises it) and the Fake
/// provider's scripts. Driven by <see cref="LlmToolLoop"/>; failures throw <see cref="LlmProviderException"/> as
/// <see cref="ILlmProvider.CompleteAsync"/> does.
/// </summary>
public interface IToolCallingLlmProvider
{
    /// <summary>Whether the model of <paramref name="request"/> takes tools (Ollama asks <c>/api/show</c>; others: yes).</summary>
    Task<bool> SupportsToolsAsync(LlmRequest request, CancellationToken cancellationToken);

    Task<LlmToolTurn> CompleteTurnAsync(LlmToolRequest toolRequest, CancellationToken cancellationToken);
}

/// <summary>Runs the tools a model calls in a tool loop (the repo tools: <c>Aide.Infrastructure.RepoTools.RepoToolSession</c>).</summary>
public interface ILlmToolExecutor
{
    IReadOnlyList<LlmToolDefinition> Definitions { get; }

    /// <summary>A call was refused with <c>TOOL_LIMIT</c>: the model must now answer with what it has.</summary>
    bool LimitReached { get; }

    /// <summary>Executes one call. Never throws for a bad call: errors are results (<see cref="LlmToolResult.IsError"/>).</summary>
    Task<LlmToolResult> ExecuteAsync(LlmToolCall toolCall, CancellationToken cancellationToken);

    /// <summary>Context to add to the prompt when the provider can't call tools (e.g. a file listing), or <c>null</c>.</summary>
    Task<string?> FallbackContextAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of <see cref="LlmToolLoop.RunAsync"/>: the final answer with tokens summed over every turn, the number of
/// model turns, and warnings to record on the run (<see cref="LlmToolLoop.ToolsUnsupportedWarning"/>).
/// </summary>
public sealed record LlmToolLoopResult(LlmCompletion Completion, int Turns, IReadOnlyList<string> Warnings);

/// <summary>The tokens of the tool loop's completed turns so far (input and output, summed).</summary>
public readonly record struct LlmTurnTokens(int InputTokens, int OutputTokens);

/// <summary>
/// The tool loop was stopped between turns by its observer (e.g. the token budget is used up, FR-26). Carries the tokens of
/// the turns completed before the stop, which were paid for.
/// </summary>
public sealed class LlmToolLoopStoppedException : Exception
{
    public LlmToolLoopStoppedException()
    {
    }

    public LlmToolLoopStoppedException(string message)
        : base(message)
    {
    }

    public LlmToolLoopStoppedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LlmToolLoopStoppedException(string message, LlmTurnTokens tokens)
        : base(message)
    {
        Tokens = tokens;
    }

    public LlmTurnTokens Tokens { get; }
}

/// <summary>
/// The provider-agnostic tool loop (FR-71): send the request with the tool definitions; while the model answers with tool
/// calls, execute them in order and send the results back; stop at a final answer. After a <c>TOOL_LIMIT</c> result (or at
/// <see cref="MaxTurns"/>) the next turn is forced to answer without tools. Token counts are summed over all turns.
/// Without tools (<c>null</c> executor) this is one plain <see cref="ILlmProvider.CompleteAsync"/>. A provider without tool
/// support (not <see cref="IToolCallingLlmProvider"/>, or a model that doesn't advertise tools) gets one context-only call
/// (the executor's <see cref="ILlmToolExecutor.FallbackContextAsync"/> appended to the last user message) and the
/// <see cref="ToolsUnsupportedWarning"/> warning.
/// </summary>
public static class LlmToolLoop
{
    public const string ToolsUnsupportedWarning = "tools_unsupported";

    /// <summary>Model turns per loop: the 25 calls (one per turn at worst), the refused one, and the forced final answer.</summary>
    public const int MaxTurns = 28;

    /// <param name="provider">The run's provider.</param>
    /// <param name="request">The rendered prompt, model and key.</param>
    /// <param name="tools">The tools (<c>null</c>: one plain call).</param>
    /// <param name="cancellationToken">Cancels the loop.</param>
    /// <param name="onToolTurn">
    /// Called after every turn that asks for tools, before they run, with the tokens of all turns so far: the caller records
    /// them (so a later failure doesn't lose what was paid for) and returns <c>false</c> to stop the loop
    /// (<see cref="LlmToolLoopStoppedException"/>), e.g. when the token budget is used up.
    /// </param>
    public static async Task<LlmToolLoopResult> RunAsync(
        ILlmProvider provider,
        LlmRequest request,
        ILlmToolExecutor? tools,
        CancellationToken cancellationToken,
        Func<LlmTurnTokens, CancellationToken, Task<bool>>? onToolTurn = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        if (tools is null)
        {
            return new LlmToolLoopResult(await provider.CompleteAsync(request, cancellationToken), 1, []);
        }

        if (provider is not IToolCallingLlmProvider toolProvider || !await toolProvider.SupportsToolsAsync(request, cancellationToken))
        {
            var context = await tools.FallbackContextAsync(cancellationToken);
            var completion = await provider.CompleteAsync(WithContext(request, context), cancellationToken);
            return new LlmToolLoopResult(completion, 1, [ToolsUnsupportedWarning]);
        }

        var exchanges = new List<LlmToolExchange>();
        int input = 0, output = 0;
        for (var turn = 1; ; turn++)
        {
            var force = tools.LimitReached || turn >= MaxTurns;
            var answer = await toolProvider.CompleteTurnAsync(
                new LlmToolRequest { Request = request, Tools = tools.Definitions, Exchanges = [.. exchanges], ForceFinalAnswer = force },
                cancellationToken);
            input += answer.InputTokens;
            output += answer.OutputTokens;
            if (answer.Calls.Count == 0 || force)
            {
                return new LlmToolLoopResult(new LlmCompletion(answer.Text, input, output, answer.StopReason), turn, []);
            }

            if (onToolTurn is not null && !await onToolTurn(new LlmTurnTokens(input, output), cancellationToken))
            {
                throw new LlmToolLoopStoppedException("The tool loop was stopped between turns.", new LlmTurnTokens(input, output));
            }

            var results = new List<LlmToolResult>(answer.Calls.Count);
            foreach (var call in answer.Calls)
            {
                results.Add(await tools.ExecuteAsync(call, cancellationToken));
            }

            exchanges.Add(new LlmToolExchange(answer.Text, answer.Calls, results));
        }
    }

    /// <summary><paramref name="request"/> with <paramref name="context"/> appended to its last user message.</summary>
    internal static LlmRequest WithContext(LlmRequest request, string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return request;
        }

        var messages = request.Messages.ToList();
        var last = messages.FindLastIndex(m => m.Role == LlmRole.User);
        if (last < 0)
        {
            messages.Add(new LlmMessage(LlmRole.User, context));
        }
        else
        {
            messages[last] = messages[last] with { Content = messages[last].Content + "\n\n" + context };
        }

        return new LlmRequest
        {
            Model = request.Model,
            System = request.System,
            Messages = messages,
            MaxTokens = request.MaxTokens,
            Agent = request.Agent,
            ApiKey = request.ApiKey,
        };
    }
}
