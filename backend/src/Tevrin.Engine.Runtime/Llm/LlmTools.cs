using System.Text.Json;

namespace Tevrin.Engine.Runtime.Llm;

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

    /// <summary>Messages rendered after the exchanges (a truncated answer and the request to continue it, <see cref="LlmToolLoop"/>).</summary>
    public IReadOnlyList<LlmMessage> TrailingMessages { get; init; } = [];

    public override string ToString() => $"LlmToolRequest {{ {Request}, Tools = {Tools.Count}, Exchanges = {Exchanges.Count}, ForceFinalAnswer = {ForceFinalAnswer}, TrailingMessages = {TrailingMessages.Count} }}";
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

/// <summary>Runs the tools a model calls in a tool loop (e.g. the SDLC repo tools).</summary>
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
/// model turns, warnings to record on the run (<see cref="LlmToolLoop.ToolsUnsupportedWarning"/>,
/// <see cref="LlmToolLoop.OutputTruncatedWarning"/>) and how many continuations the answer took (FR-162, 0 to
/// <see cref="LlmToolLoop.MaxContinuations"/>).
/// </summary>
public sealed record LlmToolLoopResult(LlmCompletion Completion, int Turns, IReadOnlyList<string> Warnings, int Continuations = 0);

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
/// FR-359: thrown by an <see cref="ILlmToolExecutor"/> instead of running <see cref="Call"/>, which waits on a human (a
/// destructive tool's <c>tool_approval</c> gate). The tool loop stops and reports where it was
/// (<see cref="LlmToolLoopSuspendedException"/>).
/// </summary>
public sealed class LlmToolCallSuspendedException : Exception
{
    public LlmToolCallSuspendedException()
    {
    }

    public LlmToolCallSuspendedException(string message)
        : base(message)
    {
    }

    public LlmToolCallSuspendedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LlmToolCallSuspendedException(string message, LlmToolCall call)
        : base(message)
    {
        Call = call;
    }

    public LlmToolCall? Call { get; }
}

/// <summary>
/// FR-359, §4.73: where a tool loop stopped: the completed <see cref="Exchanges"/>, the <see cref="Current"/> turn (the
/// model's text and calls, and the results of the calls before the waiting one) and its 1-based <see cref="Turn"/>. The
/// waiting call is <c>Current.Calls[Current.Results.Count]</c>.
/// </summary>
public sealed record LlmToolLoopSnapshot(IReadOnlyList<LlmToolExchange> Exchanges, LlmToolExchange Current, int Turn)
{
    /// <summary>The call the loop stopped at.</summary>
    public LlmToolCall Waiting => Current.Calls[Current.Results.Count];
}

/// <summary>
/// FR-359: the tool loop stopped at a call that waits on a human (<see cref="LlmToolCallSuspendedException"/>). Carries
/// where (<see cref="Snapshot"/>) and the tokens of the turns so far, which were paid for.
/// </summary>
public sealed class LlmToolLoopSuspendedException : Exception
{
    public LlmToolLoopSuspendedException()
    {
    }

    public LlmToolLoopSuspendedException(string message)
        : base(message)
    {
    }

    public LlmToolLoopSuspendedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LlmToolLoopSuspendedException(string message, LlmToolLoopSnapshot snapshot, LlmTurnTokens tokens, Exception innerException)
        : base(message, innerException)
    {
        Snapshot = snapshot;
        Tokens = tokens;
    }

    public LlmToolLoopSnapshot? Snapshot { get; }

    public LlmTurnTokens Tokens { get; }
}

/// <summary>
/// FR-359, §4.73: continues a stopped tool loop with no repeated model call for its earlier turns: the saved
/// <see cref="Exchanges"/>, then the rest of the <see cref="Current"/> turn's calls (its <c>Results</c> include the decided
/// call's), then the next model turn (<see cref="Turn"/> + 1). The request's messages are the saved ones.
/// </summary>
public sealed record LlmToolLoopResume(IReadOnlyList<LlmToolExchange> Exchanges, LlmToolExchange Current, int Turn);

/// <summary>
/// The provider-agnostic tool loop (FR-71): send the request with the tool definitions; while the model answers with tool
/// calls, execute them in order and send the results back; stop at a final answer. After a <c>TOOL_LIMIT</c> result (or at
/// <see cref="MaxTurns"/>) the next turn is forced to answer without tools. Token counts are summed over all turns.
/// Without tools (<c>null</c> executor) this is one plain <see cref="ILlmProvider.CompleteAsync"/>. A provider without tool
/// support (not <see cref="IToolCallingLlmProvider"/>, or a model that doesn't advertise tools) gets one context-only call
/// (the executor's <see cref="ILlmToolExecutor.FallbackContextAsync"/> appended to the last user message) and the
/// <see cref="ToolsUnsupportedWarning"/> warning.
/// <para>
/// FR-162, §4.24: a final answer cut off by the output cap (<see cref="IsLengthStop"/>) is continued: up to
/// <see cref="MaxContinuations"/> more turns send the answer so far as an <c>assistant</c> message followed by
/// <see cref="ContinuePrompt"/> as a <c>user</c> message (not an assistant prefill, which current Claude models reject), and
/// the parts are joined (<see cref="JoinContinuation"/>). Continuing stops early when the next request would overflow the
/// model's context window (<see cref="FitsContext"/>, <see cref="LlmRequest.ContextTokens"/>), or when the budget hook
/// refuses (<see cref="LlmToolLoopStoppedException"/>). An answer still cut off gets the <see cref="OutputTruncatedWarning"/>
/// warning. Each call to <see cref="RunAsync"/> is one answer with its own allowance (a corrective re-run is a new answer).
/// </para>
/// </summary>
public static class LlmToolLoop
{
    public const string ToolsUnsupportedWarning = "tools_unsupported";

    /// <summary>The final answer still hit the output cap after <see cref="MaxContinuations"/> continuations.</summary>
    public const string OutputTruncatedWarning = Tevrin.Engine.Domain.Agents.AgentRunWarnings.OutputTruncated;

    /// <summary>Model turns per loop: the 25 calls (one per turn at worst), the refused one, and the forced final answer.</summary>
    public const int MaxTurns = 28;

    /// <summary>FR-162: how many times a final answer cut off by the output cap is continued (6 calls in all).</summary>
    public const int MaxContinuations = 5;

    /// <summary>FR-162, §4.24: the characters per token used to estimate a continuation's input.</summary>
    public const int CharsPerToken = 4;

    /// <summary>The shortest repeated text <see cref="JoinContinuation"/> removes where a continuation overlaps the answer.</summary>
    public const int MinOverlap = 20;

    private const int MaxOverlap = 2000;

    public const string ContinuePrompt =
        "Your previous reply was cut off because it reached the output token limit. Continue it from the exact point where it "
        + "stopped. Do not repeat any text that is already written, do not restart a section, and add no preamble or "
        + "commentary: your reply is appended directly to the text so far.";

    /// <param name="provider">The run's provider.</param>
    /// <param name="request">The rendered prompt, model and key.</param>
    /// <param name="tools">The tools (<c>null</c>: one plain call).</param>
    /// <param name="cancellationToken">Cancels the loop.</param>
    /// <param name="onToolTurn">
    /// Called after every turn that asks for tools (before they run) and before every continuation, with the tokens of all
    /// turns so far: the caller records them (so a later failure doesn't lose what was paid for) and returns <c>false</c> to
    /// stop the loop (<see cref="LlmToolLoopStoppedException"/>), e.g. when the token budget is used up.
    /// </param>
    public static Task<LlmToolLoopResult> RunAsync(
        ILlmProvider provider,
        LlmRequest request,
        ILlmToolExecutor? tools,
        CancellationToken cancellationToken,
        Func<LlmTurnTokens, CancellationToken, Task<bool>>? onToolTurn = null) =>
        RunCoreAsync(provider, request, tools, resume: null, onToolTurn, cancellationToken);

    /// <summary>
    /// FR-359, §4.73: continues a stopped tool loop (<see cref="LlmToolLoopResume"/>) without calling the model for its
    /// earlier turns: the rest of the stopped turn's calls, then the next model turn, as <c>RunAsync</c> does from there.
    /// <paramref name="request"/> carries the saved messages.
    /// </summary>
    public static Task<LlmToolLoopResult> ResumeAsync(
        ILlmProvider provider,
        LlmRequest request,
        ILlmToolExecutor tools,
        LlmToolLoopResume resume,
        CancellationToken cancellationToken,
        Func<LlmTurnTokens, CancellationToken, Task<bool>>? onToolTurn = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(resume);
        return RunCoreAsync(provider, request, tools, resume, onToolTurn, cancellationToken);
    }

    /// <summary>
    /// The loop; FR-359: a call the executor suspends (<see cref="LlmToolCallSuspendedException"/>) stops it with
    /// <see cref="LlmToolLoopSuspendedException"/>, and <paramref name="resume"/> continues a stopped one.
    /// </summary>
    private static async Task<LlmToolLoopResult> RunCoreAsync(
        ILlmProvider provider,
        LlmRequest request,
        ILlmToolExecutor? tools,
        LlmToolLoopResume? resume,
        Func<LlmTurnTokens, CancellationToken, Task<bool>>? onToolTurn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        IToolCallingLlmProvider toolProvider;
        if (resume is not null)
        {
            // The stopped loop called tools on this provider, so it takes them.
            ArgumentNullException.ThrowIfNull(tools);
            toolProvider = provider as IToolCallingLlmProvider
                ?? throw new InvalidOperationException("A tool loop can only be resumed on a provider that calls tools.");
        }
        else if (tools is null)
        {
            var completion = await provider.CompleteAsync(request, cancellationToken);
            return await ContinuePlainAsync(provider, request, completion, [], onToolTurn, cancellationToken);
        }
        else if (provider is not IToolCallingLlmProvider calling || !await calling.SupportsToolsAsync(request, cancellationToken))
        {
            var withContext = WithContext(request, await tools.FallbackContextAsync(cancellationToken));
            var completion = await provider.CompleteAsync(withContext, cancellationToken);
            return await ContinuePlainAsync(provider, withContext, completion, [ToolsUnsupportedWarning], onToolTurn, cancellationToken);
        }
        else
        {
            toolProvider = calling;
        }

        var exchanges = new List<LlmToolExchange>(resume?.Exchanges ?? []);
        int input = 0, output = 0;
        var first = 1;
        if (resume is not null)
        {
            // FR-359: the rest of the stopped turn's calls (a later destructive one stops the loop again), then a new turn.
            await RunCallsAsync(tools, resume.Current.AssistantText, resume.Current.Calls, [.. resume.Current.Results], exchanges, resume.Turn, new LlmTurnTokens(0, 0), cancellationToken);
            first = resume.Turn + 1;
        }

        for (var turn = first; ; turn++)
        {
            var force = tools.LimitReached || turn >= MaxTurns;
            var answer = await toolProvider.CompleteTurnAsync(
                new LlmToolRequest { Request = request, Tools = tools.Definitions, Exchanges = [.. exchanges], ForceFinalAnswer = force },
                cancellationToken);
            input += answer.InputTokens;
            output += answer.OutputTokens;
            if (answer.Calls.Count == 0 || force)
            {
                var text = answer.Text;
                var stopReason = answer.StopReason;
                var inputChars = InputChars(request, exchanges);
                var continuations = 0;
                for (; continuations < MaxContinuations && IsLengthStop(stopReason) && text.Length > 0; continuations++)
                {
                    if (!FitsContext(request.ContextTokens, request.MaxTokens, inputChars, text.Length))
                    {
                        break;
                    }

                    await CheckCanContinueAsync(onToolTurn, input, output, cancellationToken);
                    var next = await toolProvider.CompleteTurnAsync(
                        new LlmToolRequest
                        {
                            Request = request,
                            Tools = tools.Definitions,
                            Exchanges = [.. exchanges],
                            ForceFinalAnswer = true,
                            TrailingMessages = ContinuationMessages(text),
                        },
                        cancellationToken);
                    input += next.InputTokens;
                    output += next.OutputTokens;
                    turn++;
                    text = JoinContinuation(text, next.Text);
                    stopReason = next.StopReason;
                }

                return new LlmToolLoopResult(
                    new LlmCompletion(text, input, output, stopReason),
                    turn,
                    IsLengthStop(stopReason) ? [OutputTruncatedWarning] : [],
                    continuations);
            }

            if (onToolTurn is not null && !await onToolTurn(new LlmTurnTokens(input, output), cancellationToken))
            {
                throw new LlmToolLoopStoppedException("The tool loop was stopped between turns.", new LlmTurnTokens(input, output));
            }

            await RunCallsAsync(tools, answer.Text, answer.Calls, new List<LlmToolResult>(answer.Calls.Count), exchanges, turn, new LlmTurnTokens(input, output), cancellationToken);
        }
    }

    /// <summary>
    /// Runs a turn's calls from the first without a result, in order, and adds the turn to <paramref name="exchanges"/>. A
    /// call the executor suspends stops the loop: <see cref="LlmToolLoopSuspendedException"/> with where it stopped.
    /// </summary>
    private static async Task RunCallsAsync(
        ILlmToolExecutor tools,
        string text,
        IReadOnlyList<LlmToolCall> calls,
        List<LlmToolResult> results,
        List<LlmToolExchange> exchanges,
        int turn,
        LlmTurnTokens tokens,
        CancellationToken cancellationToken)
    {
        for (var i = results.Count; i < calls.Count; i++)
        {
            try
            {
                results.Add(await tools.ExecuteAsync(calls[i], cancellationToken));
            }
            catch (LlmToolCallSuspendedException ex)
            {
                throw new LlmToolLoopSuspendedException(
                    "The tool loop stopped at a call that waits on approval.",
                    new LlmToolLoopSnapshot([.. exchanges], new LlmToolExchange(text, calls, [.. results]), turn),
                    tokens,
                    ex);
            }
        }

        exchanges.Add(new LlmToolExchange(text, calls, results));
    }

    /// <summary>Whether a provider stopped because the output cap was reached (Anthropic <c>max_tokens</c>; OpenAI and
    /// Ollama <c>length</c>).</summary>
    public static bool IsLengthStop(string? stopReason) => stopReason is "max_tokens" or "length";

    /// <summary>
    /// <paramref name="text"/> followed by <paramref name="continuation"/>, without the text the continuation repeated: the
    /// longest start of the continuation (at least <see cref="MinOverlap"/> characters) that the text already ends with.
    /// </summary>
    public static string JoinContinuation(string text, string continuation)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(continuation);
        for (var length = Math.Min(MaxOverlap, Math.Min(text.Length, continuation.Length)); length >= MinOverlap; length--)
        {
            if (text.AsSpan().EndsWith(continuation.AsSpan(0, length), StringComparison.Ordinal))
            {
                return string.Concat(text, continuation.AsSpan(length));
            }
        }

        return text + continuation;
    }

    /// <summary>Continues a plain (no tools) completion cut off by the output cap.</summary>
    private static async Task<LlmToolLoopResult> ContinuePlainAsync(
        ILlmProvider provider,
        LlmRequest request,
        LlmCompletion completion,
        IReadOnlyList<string> warnings,
        Func<LlmTurnTokens, CancellationToken, Task<bool>>? onToolTurn,
        CancellationToken cancellationToken)
    {
        var turns = 1;
        var inputChars = InputChars(request, []);
        var continuations = 0;
        for (; continuations < MaxContinuations && IsLengthStop(completion.StopReason) && completion.Text.Length > 0; continuations++)
        {
            if (!FitsContext(request.ContextTokens, request.MaxTokens, inputChars, completion.Text.Length))
            {
                break;
            }

            await CheckCanContinueAsync(onToolTurn, completion.InputTokens, completion.OutputTokens, cancellationToken);
            var next = await provider.CompleteAsync(
                new LlmRequest
                {
                    Model = request.Model,
                    System = request.System,
                    Messages = [.. request.Messages, .. ContinuationMessages(completion.Text)],
                    MaxTokens = request.MaxTokens,
                    ContextTokens = request.ContextTokens,
                    Agent = request.Agent,
                    ApiKey = request.ApiKey,
                },
                cancellationToken);
            turns++;
            completion = new LlmCompletion(
                JoinContinuation(completion.Text, next.Text),
                completion.InputTokens + next.InputTokens,
                completion.OutputTokens + next.OutputTokens,
                next.StopReason);
        }

        return new LlmToolLoopResult(completion, turns, IsLengthStop(completion.StopReason) ? [.. warnings, OutputTruncatedWarning] : warnings, continuations);
    }

    /// <summary>
    /// FR-162, §4.24: whether one more continuation fits the model's context window: the estimated input (the original input
    /// plus the answer so far, at <see cref="CharsPerToken"/> characters per token) must not exceed
    /// <c>contextTokens − maxOutputTokens</c>. An unknown window (<c>null</c> or 0) always fits.
    /// </summary>
    public static bool FitsContext(int? contextTokens, int? maxOutputTokens, long inputChars, long answerChars)
    {
        if (contextTokens is not > 0)
        {
            return true;
        }

        var estimatedInput = (inputChars + answerChars) / CharsPerToken;
        return estimatedInput <= (long)contextTokens.Value - (maxOutputTokens ?? 0);
    }

    /// <summary>The characters of everything a continuation resends before the answer so far: the system prompt, the
    /// messages and the tool exchanges.</summary>
    private static long InputChars(LlmRequest request, IReadOnlyList<LlmToolExchange> exchanges)
    {
        long chars = request.System.Length + request.Messages.Sum(m => (long)m.Content.Length);
        foreach (var exchange in exchanges)
        {
            chars += exchange.AssistantText.Length
                + exchange.Calls.Sum(c => (long)c.Name.Length + c.ArgumentsJson.Length)
                + exchange.Results.Sum(r => (long)r.Content.Length);
        }

        return chars;
    }

    private static async Task CheckCanContinueAsync(Func<LlmTurnTokens, CancellationToken, Task<bool>>? onToolTurn, int input, int output, CancellationToken cancellationToken)
    {
        if (onToolTurn is not null && !await onToolTurn(new LlmTurnTokens(input, output), cancellationToken))
        {
            throw new LlmToolLoopStoppedException("The tool loop was stopped before continuing a truncated answer.", new LlmTurnTokens(input, output));
        }
    }

    private static LlmMessage[] ContinuationMessages(string textSoFar) =>
        [new LlmMessage(LlmRole.Assistant, textSoFar), new LlmMessage(LlmRole.User, ContinuePrompt)];

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
            ContextTokens = request.ContextTokens,
            Agent = request.Agent,
            ApiKey = request.ApiKey,
        };
    }
}
