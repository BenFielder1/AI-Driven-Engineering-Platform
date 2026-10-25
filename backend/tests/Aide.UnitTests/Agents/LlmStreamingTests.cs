using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Llm;
using Microsoft.Extensions.Options;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-161: each HTTP provider streams (Anthropic and OpenAI Server-Sent Events, Ollama NDJSON) and assembles the same reply
/// as before: text, tool calls (arguments split anywhere, parsed as JSON), usage and stop reason. Stalls (idle timeout), the
/// whole-call timeout and streams that end early are transient provider failures; cancellation aborts the stream. Only
/// scripted stub streams; no test reaches a real provider (CLAUDE.md rule 10).
/// </summary>
public sealed class LlmStreamingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LlmRequest Request(string? apiKey = "sk-test-key") => new()
    {
        Model = "m-1",
        System = "You are a test.",
        Messages = [new LlmMessage(LlmRole.User, "Hello")],
        MaxTokens = 1234,
        Agent = AgentKind.SpecGenerator,
        ApiKey = apiKey,
    };

    private static LlmToolRequest ToolRequest() => new() { Request = Request(), Tools = [] };

    private static IOptions<LlmOptions> Options(TimeSpan? requestTimeout = null, TimeSpan? idleTimeout = null) =>
        Microsoft.Extensions.Options.Options.Create(new LlmOptions
        {
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30),
            StreamIdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(10),
            Providers = new LlmProvidersOptions { Ollama = new LlmProviderOptions { BaseUrl = "http://ollama.test:11434" } },
        });

    // ------------------------------------------------------------------ Anthropic (SSE)

    private const string AnthropicText = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-opus-5-5","stop_reason":null,"usage":{"input_tokens":25,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: ping
        data: {"type": "ping"}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" wörld"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":12}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    [Fact]
    public async Task FR161_Anthropic_streams_text_and_usage_and_requests_stream_true()
    {
        // Delivered in 7-byte pieces, so events and multi-byte characters are split across reads.
        var handler = new StreamHandler(_ => StreamScript.Sse(Chunks(AnthropicText, 7)));
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options());

        var completion = await provider.CompleteAsync(Request(), Ct);

        Assert.Equal(new LlmCompletion("Hello wörld", 25, 12, "end_turn"), completion);
        var body = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal(1234, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task FR161_Anthropic_tool_use_with_argument_deltas_split_mid_token_is_assembled_and_parsed()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Sse(
            """{"type":"message_start","message":{"usage":{"input_tokens":100,"output_tokens":2}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hidden"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Let me look."}}""",
            """{"type":"content_block_stop","index":1}""",
            """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"read_file","input":{}}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":""}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"pa"}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"th\": \"READ"}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"ME.md\"}"}}""",
            """{"type":"content_block_stop","index":2}""",
            """{"type":"content_block_start","index":3,"content_block":{"type":"tool_use","id":"toolu_2","name":"list_files","input":{}}}""",
            """{"type":"content_block_stop","index":3}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":40}}""",
            """{"type":"message_stop"}""")));
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options());

        var turn = await provider.CompleteTurnAsync(ToolRequest(), Ct);

        Assert.Equal(("Let me look.", 100, 40, "tool_use"), (turn.Text, turn.InputTokens, turn.OutputTokens, turn.StopReason));
        Assert.Equal(2, turn.Calls.Count);
        Assert.Equal(("toolu_1", "read_file"), (turn.Calls[0].Id, turn.Calls[0].Name));
        Assert.Equal("README.md", JsonDocument.Parse(turn.Calls[0].ArgumentsJson).RootElement.GetProperty("path").GetString());
        Assert.Equal(("toolu_2", "{}"), (turn.Calls[1].Id, turn.Calls[1].ArgumentsJson));
    }

    [Fact]
    public async Task FR161_Anthropic_max_tokens_stop_is_reported()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(AnthropicStream("cut off", "max_tokens")));

        var completion = await new AnthropicLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct);

        Assert.Equal(("cut off", "max_tokens"), (completion.Text, completion.StopReason));
        Assert.True(LlmToolLoop.IsLengthStop(completion.StopReason));
    }

    [Fact]
    public async Task FR161_Anthropic_message_delta_input_tokens_override_message_start()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Sse(
            """{"type":"message_start","message":{"usage":{"input_tokens":10,"output_tokens":1}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":15,"output_tokens":3}}""",
            """{"type":"message_stop"}""")));

        var completion = await new AnthropicLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct);

        Assert.Equal((15, 3), (completion.InputTokens, completion.OutputTokens));
    }

    [Theory]
    [InlineData("overloaded_error", true)]
    [InlineData("api_error", true)]
    [InlineData("rate_limit_error", true)]
    [InlineData("invalid_request_error", false)]
    public async Task FR161_Anthropic_error_event_mid_stream_fails_the_call(string type, bool transient)
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Sse(
            """{"type":"message_start","message":{"usage":{"input_tokens":10,"output_tokens":1}}}""",
            """{"type":"error","error":{"type":"TYPE","message":"Something broke for sk-ant-api03-abcdef123456"}}""".Replace("TYPE", type, StringComparison.Ordinal))));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new AnthropicLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct));

        Assert.Equal((transient, ErrorCodes.ProviderError), (ex.Transient, ex.ErrorCode));
        Assert.Contains(type, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Something broke", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant-api03", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR161_Anthropic_stream_ending_without_message_stop_is_a_transient_failure()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Sse(
            """{"type":"message_start","message":{"usage":{"input_tokens":10,"output_tokens":1}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"half an ans"}}""")));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new AnthropicLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct));

        Assert.True(ex.Transient);
        Assert.Contains("ended before the reply was complete", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR161_A_stall_longer_than_the_idle_timeout_is_a_transient_failure()
    {
        var parts = Chunks(AnthropicStream("never finished", "end_turn"), 40).Take(3).ToList();
        var handler = new StreamHandler(_ => StreamScript.Sse(parts) with { StallAtEnd = true });
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options(idleTimeout: TimeSpan.FromMilliseconds(200)));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request(), Ct));

        Assert.True(ex.Transient);
        Assert.Null(ex.StatusCode);
        Assert.Contains("stream idle timeout", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR161_A_stream_with_gaps_shorter_than_the_idle_timeout_succeeds_even_when_it_lasts_longer()
    {
        // 10 pieces 100 ms apart (about 1 s in all) under a 400 ms idle timeout.
        var parts = Chunks(AnthropicText, AnthropicText.Length / 9 + 1).Select(p => p with { Delay = TimeSpan.FromMilliseconds(100) }).ToList();
        var handler = new StreamHandler(_ => StreamScript.Sse(parts));
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options(idleTimeout: TimeSpan.FromMilliseconds(400)));

        var completion = await provider.CompleteAsync(Request(), Ct);

        Assert.Equal("Hello wörld", completion.Text);
    }

    [Fact]
    public async Task FR161_The_whole_call_timeout_fails_a_stream_that_keeps_trickling()
    {
        var trickle = Enumerable.Range(0, 200).Select(_ => new StreamPart(": keep-alive\n", TimeSpan.FromMilliseconds(20))).ToList();
        var handler = new StreamHandler(_ => StreamScript.Sse(trickle));
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options(requestTimeout: TimeSpan.FromMilliseconds(300), idleTimeout: TimeSpan.FromSeconds(10)));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request(), Ct));

        Assert.True(ex.Transient);
        Assert.Contains("request timeout", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR161_Cancellation_aborts_the_stream_and_is_not_a_provider_failure()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Chunks(AnthropicText, 30).Take(2)) with { StallAtEnd = true });
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(Request(), cts.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task FR161_An_error_status_on_a_streamed_request_is_classified_as_before(HttpStatusCode status, bool transient)
    {
        var handler = new LlmProviderTests.FakeHandler(status, """{"type":"error","error":{"type":"some_error","message":"Nope"}}""");

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new AnthropicLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct));

        Assert.Equal((transient, (int?)(int)status), (ex.Transient, ex.StatusCode));
        Assert.Contains("Nope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR161_A_server_that_ignores_stream_and_answers_JSON_is_read_as_a_whole_body()
    {
        var handler = new LlmProviderTests.FakeHandler(HttpStatusCode.OK, """{"content":[{"type":"text","text":"whole"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":4}}""");

        var completion = await new AnthropicLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct);

        Assert.Equal(new LlmCompletion("whole", 3, 4, "end_turn"), completion);
    }

    // ------------------------------------------------------------------ OpenAI (SSE)

    [Fact]
    public async Task FR161_OpenAI_streams_text_and_takes_usage_from_the_final_chunk()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Chunks(Sse(
            """{"id":"c1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","content":"","refusal":null},"finish_reason":null}],"usage":null}""",
            """{"id":"c1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hi"},"finish_reason":null}],"usage":null}""",
            """{"id":"c1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":" there"},"finish_reason":null}],"usage":null}""",
            """{"id":"c1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":null}""",
            """{"id":"c1","object":"chat.completion.chunk","choices":[],"usage":{"prompt_tokens":21,"completion_tokens":5,"total_tokens":26}}""",
            "[DONE]"), 11)));
        var provider = new OpenAiLlmProvider(new HttpClient(handler), Options());

        var completion = await provider.CompleteAsync(Request(), Ct);

        Assert.Equal(new LlmCompletion("Hi there", 21, 5, "stop"), completion);
        var body = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(1234, body.GetProperty("max_completion_tokens").GetInt32());
    }

    [Fact]
    public async Task FR161_OpenAI_tool_call_deltas_by_index_are_assembled_with_arguments_split_mid_token()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Sse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":null,"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"search_code","arguments":""}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"que"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ry\": \"wid"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"list_files","arguments":"{\"path\":"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"get\"}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"\".\"}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":40,"completion_tokens":9}}""",
            "[DONE]")));
        var provider = new OpenAiLlmProvider(new HttpClient(handler), Options());

        var turn = await provider.CompleteTurnAsync(ToolRequest(), Ct);

        Assert.Equal((string.Empty, 40, 9, "tool_calls"), (turn.Text, turn.InputTokens, turn.OutputTokens, turn.StopReason));
        Assert.Equal([("call_a", "search_code"), ("call_b", "list_files")], turn.Calls.Select(c => (c.Id, c.Name)));
        Assert.Equal("widget", JsonDocument.Parse(turn.Calls[0].ArgumentsJson).RootElement.GetProperty("query").GetString());
        Assert.Equal(".", JsonDocument.Parse(turn.Calls[1].ArgumentsJson).RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task FR161_OpenAI_length_stop_is_reported()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Sse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"First half,"},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"length"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5}}""",
            "[DONE]")));

        var completion = await new OpenAiLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct);

        Assert.Equal(new LlmCompletion("First half,", 10, 5, "length"), completion);
    }

    [Fact]
    public async Task FR161_OpenAI_stream_ending_without_a_finish_reason_is_a_transient_failure()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Sse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"Half"},"finish_reason":null}]}""")));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new OpenAiLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct));

        Assert.True(ex.Transient);
    }

    [Fact]
    public async Task FR161_OpenAI_stream_dropping_after_finish_reason_without_usage_or_DONE_is_a_transient_failure()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Sse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"Whole answer"},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""")));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new OpenAiLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct));

        Assert.True(ex.Transient);
        Assert.Contains("ended before the reply was complete", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR161_OpenAI_finish_reason_then_the_usage_chunk_is_complete_even_if_DONE_never_arrives()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Sse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"Done"},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":7,"completion_tokens":2}}""")));

        var completion = await new OpenAiLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct);

        Assert.Equal(new LlmCompletion("Done", 7, 2, "stop"), completion);
    }

    [Fact]
    public async Task FR161_OpenAI_error_chunk_mid_stream_is_a_transient_failure()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Sse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"Half"},"finish_reason":null}]}""",
            """{"error":{"message":"The server had an error while processing your request.","type":"server_error"}}""")));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new OpenAiLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct));

        Assert.True(ex.Transient);
        Assert.Contains("The server had an error", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR161_OpenAI_streamed_refusal_is_a_permanent_failure()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Sse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":null,"refusal":"I can't "},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"refusal":"help with that."},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]")));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new OpenAiLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct));

        Assert.False(ex.Transient);
        Assert.Contains("I can't help with that.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR161_OpenAI_stall_longer_than_the_idle_timeout_is_a_transient_failure()
    {
        var handler = new StreamHandler(_ => StreamScript.Sse(Sse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"Half"},"finish_reason":null}]}""")) with { StallAtEnd = true });
        var provider = new OpenAiLlmProvider(new HttpClient(handler), Options(idleTimeout: TimeSpan.FromMilliseconds(200)));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request(), Ct));

        Assert.True(ex.Transient);
        Assert.Contains("stream idle timeout", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Ollama (NDJSON)

    [Fact]
    public async Task FR161_Ollama_streams_ndjson_text_and_takes_counts_from_the_done_line()
    {
        var handler = new StreamHandler(_ => StreamScript.Ndjson(Chunks(Ndjson(
            """{"model":"qwen2.5-coder","created_at":"2026-10-25T10:00:00Z","message":{"role":"assistant","content":"do"},"done":false}""",
            """{"model":"qwen2.5-coder","created_at":"2026-10-25T10:00:01Z","message":{"role":"assistant","content":"ne"},"done":false}""",
            """{"model":"qwen2.5-coder","created_at":"2026-10-25T10:00:02Z","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","total_duration":1,"prompt_eval_count":40,"eval_count":7}"""), 13)));
        var provider = new OllamaLlmProvider(new HttpClient(handler), Options());

        var completion = await provider.CompleteAsync(Request(apiKey: null), Ct);

        Assert.Equal(new LlmCompletion("done", 40, 7, "stop"), completion);
        var body = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal(1234, body.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public async Task FR161_Ollama_streamed_tool_calls_are_collected()
    {
        var handler = new StreamHandler(_ => StreamScript.Ndjson(Ndjson(
            """{"model":"qwen","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"read_file","arguments":{"path":"README.md"}}}]},"done":false}""",
            """{"model":"qwen","message":{"role":"assistant","content":"","tool_calls":[{"id":"call_x","function":{"index":1,"name":"list_files","arguments":{"path":"."}}}]},"done":false}""",
            """{"model":"qwen","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":5,"eval_count":1}""")));
        var provider = new OllamaLlmProvider(new HttpClient(handler), Options());

        var turn = await provider.CompleteTurnAsync(ToolRequest(), Ct);

        Assert.Equal([("call_1_1", "read_file"), ("call_x", "list_files")], turn.Calls.Select(c => (c.Id, c.Name)));
        Assert.Equal("README.md", JsonDocument.Parse(turn.Calls[0].ArgumentsJson).RootElement.GetProperty("path").GetString());
        Assert.Equal((5, 1, "stop"), (turn.InputTokens, turn.OutputTokens, turn.StopReason));
    }

    [Fact]
    public async Task FR161_Ollama_length_stop_is_reported()
    {
        var handler = new StreamHandler(_ => StreamScript.Ndjson(Ndjson(
            """{"message":{"role":"assistant","content":"cut"},"done":false}""",
            """{"message":{"role":"assistant","content":""},"done":true,"done_reason":"length","prompt_eval_count":3,"eval_count":1234}""")));

        var completion = await new OllamaLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(apiKey: null), Ct);

        Assert.Equal(new LlmCompletion("cut", 3, 1234, "length"), completion);
    }

    [Fact]
    public async Task FR161_Ollama_stream_ending_without_done_is_a_transient_failure()
    {
        var handler = new StreamHandler(_ => StreamScript.Ndjson(Ndjson("""{"message":{"role":"assistant","content":"cut"},"done":false}""")));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new OllamaLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(apiKey: null), Ct));

        Assert.True(ex.Transient);
    }

    [Fact]
    public async Task FR161_Ollama_error_line_mid_stream_is_a_transient_failure()
    {
        var handler = new StreamHandler(_ => StreamScript.Ndjson(Ndjson(
            """{"message":{"role":"assistant","content":"cut"},"done":false}""",
            """{"error":"llama runner process has terminated"}""")));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new OllamaLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(apiKey: null), Ct));

        Assert.True(ex.Transient);
        Assert.Contains("llama runner process has terminated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR161_Ollama_stall_longer_than_the_idle_timeout_is_a_transient_failure()
    {
        var handler = new StreamHandler(_ => StreamScript.Ndjson(Ndjson("""{"message":{"role":"assistant","content":"cut"},"done":false}""")) with { StallAtEnd = true });
        var provider = new OllamaLlmProvider(new HttpClient(handler), Options(idleTimeout: TimeSpan.FromMilliseconds(200)));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request(apiKey: null), Ct));

        Assert.True(ex.Transient);
        Assert.Contains("stream idle timeout", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A whole Anthropic stream answering <paramref name="text"/> with <paramref name="stopReason"/>.</summary>
    internal static string AnthropicStream(string text, string stopReason, int inputTokens = 1, int outputTokens = 1) => Sse(
        JsonSerializer.Serialize(new { type = "message_start", message = new { usage = new { input_tokens = inputTokens, output_tokens = 1 } } }),
        """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
        JsonSerializer.Serialize(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } }),
        """{"type":"content_block_stop","index":0}""",
        JsonSerializer.Serialize(new { type = "message_delta", delta = new { stop_reason = stopReason }, usage = new { output_tokens = outputTokens } }),
        """{"type":"message_stop"}""");

    /// <summary>Server-Sent Events, one <c>data:</c> event per payload.</summary>
    internal static string Sse(params string[] payloads) => string.Concat(payloads.Select(p => "data: " + p + "\n\n"));

    internal static string Ndjson(params string[] lines) => string.Concat(lines.Select(l => l + "\n"));

    /// <summary><paramref name="text"/> as UTF-8 pieces of <paramref name="size"/> bytes (splitting characters and events).</summary>
    internal static IEnumerable<StreamPart> Chunks(string text, int size)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < bytes.Length; i += size)
        {
            yield return new StreamPart(bytes[i..Math.Min(bytes.Length, i + size)]);
        }
    }

    /// <summary>One piece of a scripted stream, delivered after <see cref="Delay"/>.</summary>
    public sealed record StreamPart(byte[] Bytes, TimeSpan Delay = default)
    {
        public StreamPart(string text, TimeSpan delay = default)
            : this(Encoding.UTF8.GetBytes(text), delay)
        {
        }
    }

    /// <summary>A scripted 200 response: its pieces, media type, and whether it then stalls instead of ending.</summary>
    public sealed record StreamScript(IReadOnlyList<StreamPart> Parts, string MediaType, bool StallAtEnd = false)
    {
        public static StreamScript Sse(IEnumerable<StreamPart> parts) => new([.. parts], "text/event-stream");

        public static StreamScript Sse(string text) => Sse([new StreamPart(text)]);

        public static StreamScript Ndjson(IEnumerable<StreamPart> parts) => new([.. parts], "application/x-ndjson");

        public static StreamScript Ndjson(string text) => Ndjson([new StreamPart(text)]);
    }

    /// <summary>Answers every request with a scripted stream and records the request bodies.</summary>
    public sealed class StreamHandler(Func<HttpRequestMessage, StreamScript> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var script = respond(request);
            var content = new StreamContent(new ScriptedStream(script));
            content.Headers.ContentType = new MediaTypeHeaderValue(script.MediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    /// <summary>A read-only stream that yields the script's pieces (waiting each one's delay) and then ends or stalls.
    /// Every wait honours the read's cancellation token, like a socket.</summary>
    private sealed class ScriptedStream(StreamScript script) : Stream
    {
        private int _part;
        private int _offset;

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
            if (_part >= script.Parts.Count)
            {
                if (script.StallAtEnd)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return 0;
            }

            var part = script.Parts[_part];
            if (_offset == 0 && part.Delay > TimeSpan.Zero)
            {
                await Task.Delay(part.Delay, cancellationToken);
            }

            var count = Math.Min(buffer.Length, part.Bytes.Length - _offset);
            part.Bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset >= part.Bytes.Length)
            {
                _part++;
                _offset = 0;
            }

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
