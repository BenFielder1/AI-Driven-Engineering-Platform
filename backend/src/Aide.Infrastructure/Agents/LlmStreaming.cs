using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aide.Infrastructure.Llm;

namespace Aide.Infrastructure.Agents;

/// <summary>
/// One provider reply assembled from a stream (FR-161), or from a whole JSON body: the text, the tool calls (arguments as
/// JSON text), the reported token usage, the stop reason, and whether the provider sent any text at all (OpenAI can answer
/// with no content and a <see cref="Refusal"/>).
/// </summary>
internal sealed record LlmStreamedMessage(
    string Text,
    IReadOnlyList<LlmToolCall> Calls,
    int InputTokens,
    int OutputTokens,
    string StopReason,
    bool HasText,
    string? Refusal);

/// <summary>How a provider frames its stream: Server-Sent Events (Anthropic, OpenAI) or newline-delimited JSON (Ollama).</summary>
internal enum LlmStreamFormat
{
    Sse = 0,
    Ndjson = 1,
}

/// <summary>
/// Assembles one provider's streamed reply (FR-161). <see cref="Add"/> gets each SSE event's <c>data</c> or each NDJSON
/// line; it throws <see cref="LlmProviderException"/> for an error event. Reading stops at <see cref="Ended"/> (or the end of
/// the body); the reply counts only when <see cref="Completed"/> (its final stop event arrived).
/// </summary>
internal interface ILlmStreamAccumulator
{
    /// <summary>The final stop event arrived (Anthropic <c>message_stop</c>, OpenAI a <c>finish_reason</c> followed by the usage
    /// chunk or <c>[DONE]</c>, Ollama <c>done</c>).</summary>
    bool Completed { get; }

    /// <summary>Nothing more will come (Anthropic <c>message_stop</c>, OpenAI <c>[DONE]</c>, Ollama <c>done</c>).</summary>
    bool Ended { get; }

    void Add(string data);

    LlmStreamedMessage Result();

    /// <summary>The same reply from a whole (non-streamed) JSON body, for a server that ignored <c>stream</c>.</summary>
    LlmStreamedMessage FromJson(JsonElement root);
}

/// <summary>
/// FR-161: one streamed provider call. The request is sent with <see cref="HttpCompletionOption.ResponseHeadersRead"/> and
/// the body read as it arrives, under two timeouts that both surface as the usual transient <see cref="LlmProviderException"/>
/// (retried by the job worker, FR-25): <c>Llm:RequestTimeout</c> for the whole call, and <c>Llm:StreamIdleTimeout</c> for
/// any wait with no bytes (for the headers, and between reads). A stream that ends before its final stop event is also a
/// transient failure. The caller's token (run cancelled, lease lost) aborts the call and surfaces as cancellation.
/// HTTP error statuses are classified as for non-streamed calls. A server that answers <c>application/json</c> instead
/// of a stream (e.g. an OpenAI-compatible proxy that ignores <c>stream</c>) is read as a whole body.
/// </summary>
internal static class LlmStream
{
    public static async Task<LlmStreamedMessage> SendAsync(
        HttpClient http,
        LlmOptions options,
        string providerName,
        Uri url,
        object body,
        Action<HttpRequestMessage>? configure,
        LlmStreamFormat format,
        ILlmStreamAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: LlmHttp.Json) };
        configure?.Invoke(request);

        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        call.CancelAfter(options.RequestTimeout);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(call.Token);
        try
        {
            idle.CancelAfter(options.StreamIdleTimeout);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
            idle.CancelAfter(Timeout.InfiniteTimeSpan);

            await using var raw = await response.Content.ReadAsStreamAsync(call.Token);
            await using var stream = new IdleTimeoutStream(raw, idle, options.StreamIdleTimeout);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                throw LlmHttp.StatusFailure(providerName, status, await reader.ReadToEndAsync(call.Token));
            }

            if (response.Content.Headers.ContentType?.MediaType is "application/json")
            {
                var text = await reader.ReadToEndAsync(call.Token);
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    return accumulator.FromJson(doc.RootElement);
                }
                catch (JsonException ex)
                {
                    throw new LlmProviderException($"{providerName} returned a response that isn't JSON.", transient: false, status, innerException: ex);
                }
            }

            if (format == LlmStreamFormat.Sse)
            {
                await ReadSseAsync(reader, accumulator, call.Token);
            }
            else
            {
                await ReadNdjsonAsync(reader, accumulator, call.Token);
            }

            return accumulator.Completed
                ? accumulator.Result()
                : throw new LlmProviderException($"{providerName}'s stream ended before the reply was complete.", transient: true);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimedOut(providerName, options, call.IsCancellationRequested, ex);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The LLM call was cancelled.", ex, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException && idle.IsCancellationRequested)
        {
            // An aborted read can surface as an I/O error rather than a cancellation.
            throw TimedOut(providerName, options, call.IsCancellationRequested, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmProviderException($"{providerName} could not be reached ({ex.HttpRequestError}).", transient: true, innerException: ex);
        }
        catch (IOException ex)
        {
            throw new LlmProviderException($"{providerName}'s stream was interrupted.", transient: true, innerException: ex);
        }
    }

    /// <summary>
    /// Server-Sent Events: <c>data:</c> lines (joined with newlines) make one event, dispatched at a blank line or the end of
    /// the body; <c>event:</c>, <c>id:</c>, <c>retry:</c> and comment (<c>:</c>) lines are ignored (the payloads carry their type).
    /// </summary>
    private static async Task ReadSseAsync(StreamReader reader, ILlmStreamAccumulator accumulator, CancellationToken cancellationToken)
    {
        var data = new StringBuilder();
        var hasData = false;
        while (!accumulator.Ended && await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (hasData)
                {
                    accumulator.Add(data.ToString());
                    data.Clear();
                    hasData = false;
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.AsSpan(5);
                if (value.StartsWith(" "))
                {
                    value = value[1..];
                }

                if (hasData)
                {
                    data.Append('\n');
                }

                data.Append(value);
                hasData = true;
            }
        }

        if (hasData && !accumulator.Ended)
        {
            accumulator.Add(data.ToString());
        }
    }

    /// <summary>Newline-delimited JSON: one object per non-blank line.</summary>
    private static async Task ReadNdjsonAsync(StreamReader reader, ILlmStreamAccumulator accumulator, CancellationToken cancellationToken)
    {
        while (!accumulator.Ended && await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                accumulator.Add(line);
            }
        }
    }

    private static LlmProviderException TimedOut(string providerName, LlmOptions options, bool wholeCall, Exception inner) =>
        wholeCall
            ? new LlmProviderException($"{providerName} did not finish within the request timeout ({options.RequestTimeout}).", transient: true, innerException: inner)
            : new LlmProviderException($"{providerName} sent no data for {options.StreamIdleTimeout} (stream idle timeout).", transient: true, innerException: inner);

    /// <summary>Parses one stream chunk; a chunk that isn't JSON is a transient failure (a garbled stream).</summary>
    internal static JsonDocument Parse(string providerName, string data)
    {
        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException ex)
        {
            throw new LlmProviderException($"{providerName} sent a stream event that isn't JSON.", transient: true, innerException: ex);
        }
    }

    /// <summary>
    /// A tool call's accumulated argument text: <c>{}</c> when empty, the JSON value as parsed when it is valid JSON, else the
    /// text as sent (e.g. cut off by the output limit), which the tool then reports as invalid arguments.
    /// </summary>
    internal static string ToolArguments(string accumulated)
    {
        if (string.IsNullOrWhiteSpace(accumulated))
        {
            return "{}";
        }

        try
        {
            using var doc = JsonDocument.Parse(accumulated);
            return doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return accumulated;
        }
    }
}

/// <summary>
/// A read-only stream that fails a read with no bytes for <c>timeout</c>: every read restarts <paramref name="idle"/>'s
/// timer and reads with its token (which is linked to the call's token, so the caller's token is honoured through it).
/// </summary>
internal sealed class IdleTimeoutStream(Stream inner, CancellationTokenSource idle, TimeSpan timeout) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        idle.CancelAfter(timeout);
        try
        {
            return await inner.ReadAsync(buffer, idle.Token);
        }
        finally
        {
            if (!idle.IsCancellationRequested)
            {
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}

/// <summary>
/// Anthropic Messages streaming (FR-161): <c>message_start</c> (input tokens), <c>content_block_start</c> /
/// <c>content_block_delta</c> (<c>text_delta</c>, <c>input_json_delta</c> for <c>tool_use</c>) / <c>content_block_stop</c>,
/// <c>message_delta</c> (<c>stop_reason</c>, cumulative <c>usage</c>), <c>message_stop</c>; <c>ping</c> and thinking deltas are
/// ignored and <c>error</c> fails the call.
/// </summary>
internal sealed class AnthropicStreamAccumulator(string providerName, int turn) : ILlmStreamAccumulator
{
    private readonly SortedDictionary<int, Block> _blocks = [];
    private int _inputTokens;
    private int _outputTokens;
    private string? _stopReason;

    public bool Completed { get; private set; }

    public bool Ended => Completed;

    public void Add(string data)
    {
        using var doc = LlmStream.Parse(providerName, data);
        var root = doc.RootElement;
        switch (LlmHttp.String(root, "type"))
        {
            case "message_start" when root.TryGetProperty("message", out var message):
                _inputTokens = LlmHttp.Int(message, "usage", "input_tokens");
                _outputTokens = LlmHttp.Int(message, "usage", "output_tokens");
                break;
            case "content_block_start" when root.TryGetProperty("content_block", out var start):
                var block = new Block(LlmHttp.String(start, "type") ?? string.Empty, LlmHttp.String(start, "id"), LlmHttp.String(start, "name"));
                if (block.Type == "text" && LlmHttp.String(start, "text") is { Length: > 0 } initial)
                {
                    block.Content.Append(initial);
                }

                _blocks[LlmHttp.Int(root, "index")] = block;
                break;
            case "content_block_delta" when root.TryGetProperty("delta", out var delta):
                var index = LlmHttp.Int(root, "index");
                if (!_blocks.TryGetValue(index, out var target))
                {
                    target = new Block("text", null, null);
                    _blocks[index] = target;
                }

                switch (LlmHttp.String(delta, "type"))
                {
                    case "text_delta" when LlmHttp.String(delta, "text") is { } text:
                        target.Content.Append(text);
                        break;
                    case "input_json_delta" when LlmHttp.String(delta, "partial_json") is { } json:
                        target.Content.Append(json);
                        break;
                }

                break;
            case "message_delta":
                if (root.TryGetProperty("delta", out var messageDelta) && LlmHttp.String(messageDelta, "stop_reason") is { } stop)
                {
                    _stopReason = stop;
                }

                if (root.TryGetProperty("usage", out var usage))
                {
                    // Cumulative counts; input_tokens may also be repeated here.
                    _outputTokens = LlmHttp.Int(usage, "output_tokens") is > 0 and var output ? output : _outputTokens;
                    _inputTokens = LlmHttp.Int(usage, "input_tokens") is > 0 and var input ? input : _inputTokens;
                }

                break;
            case "message_stop":
                Completed = true;
                break;
            case "error":
                throw StreamError(root);
        }
    }

    public LlmStreamedMessage Result()
    {
        var text = new StringBuilder();
        var calls = new List<LlmToolCall>();
        foreach (var block in _blocks.Values)
        {
            switch (block.Type)
            {
                case "text":
                    text.Append(block.Content);
                    break;
                case "tool_use":
                    calls.Add(new LlmToolCall(
                        block.Id ?? LlmHttp.CallId(turn, calls.Count + 1),
                        block.Name ?? string.Empty,
                        LlmStream.ToolArguments(block.Content.ToString())));
                    break;
            }
        }

        return new LlmStreamedMessage(text.ToString(), calls, _inputTokens, _outputTokens, _stopReason ?? "unknown", true, null);
    }

    public LlmStreamedMessage FromJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            throw LlmHttp.Unreadable(providerName, "no content");
        }

        var text = new StringBuilder();
        var calls = new List<LlmToolCall>();
        foreach (var block in content.EnumerateArray())
        {
            switch (LlmHttp.String(block, "type"))
            {
                case "text" when LlmHttp.String(block, "text") is { } part:
                    text.Append(part);
                    break;
                case "tool_use":
                    calls.Add(new LlmToolCall(
                        LlmHttp.String(block, "id") ?? LlmHttp.CallId(turn, calls.Count + 1),
                        LlmHttp.String(block, "name") ?? string.Empty,
                        block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}"));
                    break;
            }
        }

        return new LlmStreamedMessage(
            text.ToString(),
            calls,
            LlmHttp.Int(root, "usage", "input_tokens"),
            LlmHttp.Int(root, "usage", "output_tokens"),
            LlmHttp.String(root, "stop_reason") ?? "unknown",
            true,
            null);
    }

    /// <summary>
    /// An <c>error</c> event (<c>{"type":"error","error":{"type":"overloaded_error","message":…}}</c>): request errors
    /// (invalid request, auth, permission, not found, too large) are permanent; overload, rate limit, API and timeout
    /// errors are transient, as their HTTP statuses would be.
    /// </summary>
    private LlmProviderException StreamError(JsonElement root)
    {
        var type = root.TryGetProperty("error", out var error) ? LlmHttp.String(error, "type") : null;
        var permanent = type is "invalid_request_error" or "authentication_error" or "permission_error" or "not_found_error" or "request_too_large";
        var detail = LlmHttp.ErrorDetail(root.GetRawText());
        return new LlmProviderException(
            detail is null ? $"{providerName} sent a stream error ({type ?? "unknown"})." : $"{providerName} sent a stream error ({type ?? "unknown"}): {detail}",
            transient: !permanent);
    }

    private sealed class Block(string type, string? id, string? name)
    {
        public string Type { get; } = type;

        public string? Id { get; } = id;

        public string? Name { get; } = name;

        public StringBuilder Content { get; } = new();
    }
}

/// <summary>
/// OpenAI Chat Completions streaming (FR-161, with <c>stream_options.include_usage</c>): <c>data:</c> chunks with
/// <c>choices[0].delta.content</c> (and <c>refusal</c>), <c>delta.tool_calls[]</c> keyed by <c>index</c> (the id and name in the
/// first delta, <c>function.arguments</c> in pieces), <c>choices[0].finish_reason</c>, a last chunk with <c>usage</c> and empty
/// <c>choices</c>, then <c>data: [DONE]</c>. A chunk with <c>error</c> fails the call (transient).
/// </summary>
internal sealed class OpenAiStreamAccumulator(string providerName, int turn) : ILlmStreamAccumulator
{
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _refusal = new();
    private readonly SortedDictionary<int, ToolCallParts> _calls = [];
    private bool _hasText;
    private int _inputTokens;
    private int _outputTokens;
    private string? _finishReason;
    private bool _usageSeen;

    /// <summary>
    /// FR-161: a <c>finish_reason</c> alone isn't the end: the usage chunk (or <c>[DONE]</c>) must follow, else a stream that
    /// drops right after <c>finish_reason</c> would be taken as complete with no token usage.
    /// </summary>
    public bool Completed => _finishReason is not null && (_usageSeen || Ended);

    public bool Ended { get; private set; }

    public void Add(string data)
    {
        if (data.Trim() == "[DONE]")
        {
            Ended = true;
            return;
        }

        using var doc = LlmStream.Parse(providerName, data);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            var detail = LlmHttp.ErrorDetail(root.GetRawText());
            throw new LlmProviderException(detail is null ? $"{providerName} sent a stream error." : $"{providerName} sent a stream error: {detail}", transient: true);
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            _inputTokens = LlmHttp.Int(usage, "prompt_tokens");
            _outputTokens = LlmHttp.Int(usage, "completion_tokens");
            _usageSeen = true;
        }

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return;
        }

        var choice = choices[0];
        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            if (LlmHttp.String(delta, "content") is { } content)
            {
                _hasText = true;
                _text.Append(content);
            }

            if (LlmHttp.String(delta, "refusal") is { } refusal)
            {
                _refusal.Append(refusal);
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in toolCalls.EnumerateArray())
                {
                    var index = LlmHttp.Int(part, "index");
                    if (!_calls.TryGetValue(index, out var call))
                    {
                        call = new ToolCallParts();
                        _calls[index] = call;
                    }

                    call.Id ??= LlmHttp.String(part, "id");
                    if (part.TryGetProperty("function", out var function))
                    {
                        if (LlmHttp.String(function, "name") is { Length: > 0 } name)
                        {
                            call.Name = name;
                        }

                        if (LlmHttp.String(function, "arguments") is { } arguments)
                        {
                            call.Arguments.Append(arguments);
                        }
                    }
                }
            }
        }

        if (LlmHttp.String(choice, "finish_reason") is { } finish)
        {
            _finishReason = finish;
        }
    }

    public LlmStreamedMessage Result()
    {
        var calls = new List<LlmToolCall>();
        foreach (var call in _calls.Values)
        {
            calls.Add(new LlmToolCall(call.Id ?? LlmHttp.CallId(turn, calls.Count + 1), call.Name ?? string.Empty, LlmStream.ToolArguments(call.Arguments.ToString())));
        }

        return new LlmStreamedMessage(
            _text.ToString(),
            calls,
            _inputTokens,
            _outputTokens,
            _finishReason ?? "unknown",
            _hasText,
            _refusal.Length == 0 ? null : _refusal.ToString());
    }

    public LlmStreamedMessage FromJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw LlmHttp.Unreadable(providerName, "no choices");
        }

        var choice = choices[0];
        var message = choice.TryGetProperty("message", out var m) ? m : default;
        var calls = new List<LlmToolCall>();
        if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.TryGetProperty("function", out var f) ? f : default;
                calls.Add(new LlmToolCall(
                    LlmHttp.String(call, "id") ?? LlmHttp.CallId(turn, calls.Count + 1),
                    LlmHttp.String(function, "name") ?? string.Empty,
                    LlmHttp.String(function, "arguments") ?? "{}"));
            }
        }

        var text = LlmHttp.String(message, "content");
        return new LlmStreamedMessage(
            text ?? string.Empty,
            calls,
            LlmHttp.Int(root, "usage", "prompt_tokens"),
            LlmHttp.Int(root, "usage", "completion_tokens"),
            LlmHttp.String(choice, "finish_reason") ?? "unknown",
            text is not null,
            LlmHttp.String(message, "refusal"));
    }

    private sealed class ToolCallParts
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}

/// <summary>
/// Ollama <c>/api/chat</c> streaming (FR-161): one JSON object per line with <c>message.content</c> pieces and whole
/// <c>message.tool_calls</c> (arguments as objects), then a last line with <c>done: true</c>, <c>done_reason</c>,
/// <c>prompt_eval_count</c> and <c>eval_count</c>. A line with <c>error</c> fails the call (transient). A non-streamed reply
/// is that last line with the whole message, so the same reading applies.
/// </summary>
internal sealed class OllamaStreamAccumulator(string providerName, int turn) : ILlmStreamAccumulator
{
    private readonly StringBuilder _text = new();
    private readonly List<LlmToolCall> _calls = [];
    private bool _hasText;
    private int _inputTokens;
    private int _outputTokens;
    private string? _doneReason;

    public bool Completed { get; private set; }

    public bool Ended => Completed;

    public void Add(string data)
    {
        using var doc = LlmStream.Parse(providerName, data);
        Add(doc.RootElement, streamed: true);
    }

    public LlmStreamedMessage Result() =>
        new(_text.ToString(), [.. _calls], _inputTokens, _outputTokens, _doneReason ?? "stop", _hasText, null);

    public LlmStreamedMessage FromJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            throw LlmHttp.Unreadable(providerName, "no message");
        }

        Add(root, streamed: false);
        return Result();
    }

    private void Add(JsonElement root, bool streamed)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (streamed && root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            var detail = LlmHttp.ErrorDetail(root.GetRawText());
            throw new LlmProviderException(detail is null ? $"{providerName} sent a stream error." : $"{providerName} sent a stream error: {detail}", transient: true);
        }

        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            if (LlmHttp.String(message, "content") is { } content)
            {
                _hasText = true;
                _text.Append(content);
            }

            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in toolCalls.EnumerateArray())
                {
                    var function = call.TryGetProperty("function", out var f) ? f : default;
                    var arguments = function.ValueKind == JsonValueKind.Object && function.TryGetProperty("arguments", out var a)
                        ? (a.ValueKind == JsonValueKind.String ? LlmStream.ToolArguments(a.GetString() ?? string.Empty) : a.GetRawText())
                        : "{}";
                    _calls.Add(new LlmToolCall(
                        LlmHttp.String(call, "id") ?? LlmHttp.CallId(turn, _calls.Count + 1),
                        LlmHttp.String(function, "name") ?? string.Empty,
                        arguments));
                }
            }
        }

        if (!streamed || (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True))
        {
            Completed = true;
            _doneReason = LlmHttp.String(root, "done_reason");
            _inputTokens = LlmHttp.Int(root, "prompt_eval_count");
            _outputTokens = LlmHttp.Int(root, "eval_count");
        }
    }
}
