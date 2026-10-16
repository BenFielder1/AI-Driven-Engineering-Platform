using System.Net;
using System.Text;
using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.RepoTools;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.RepoTools;
using Aide.UnitTests.Agents;
using Microsoft.Extensions.Options;

namespace Aide.UnitTests.RepoTools;

/// <summary>
/// FR-71: the tool loop through each provider's tool-use API (Anthropic Messages <c>tool_use</c>/<c>tool_result</c>, OpenAI
/// <c>tool_calls</c>, Ollama), the Ollama context-only fallback, and the Fake provider's scripted calls. Only fake HTTP
/// handlers; no test reaches a real provider (CLAUDE.md rule 11).
/// </summary>
public sealed class LlmToolLoopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aide-toolloop-tests", Guid.NewGuid().ToString("N"));

    public LlmToolLoopTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "README.md"), "# Widgets\nA widget service.\n");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static LlmRequest Request(AgentKind agent = AgentKind.SpecGenerator) => new()
    {
        Model = "m-1",
        System = "You are a test.",
        Messages = [new LlmMessage(LlmRole.User, "Describe the repo.")],
        MaxTokens = 1000,
        Agent = agent,
        ApiKey = "sk-test-key",
    };

    private static IOptions<LlmOptions> Options() => Microsoft.Extensions.Options.Options.Create(new LlmOptions());

    private static LlmProviderTests.FakeHandler Sequence(params string[] bodies)
    {
        var index = 0;
        return new LlmProviderTests.FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(bodies[Math.Min(index++, bodies.Length - 1)], Encoding.UTF8, "application/json"),
        });
    }

    // ------------------------------------------------------------------ Anthropic

    [Fact]
    public async Task FR71_Anthropic_tool_loop_sends_tools_and_tool_results_and_sums_tokens()
    {
        var handler = Sequence(
            """
            {"content":[{"type":"text","text":"Let me look."},{"type":"tool_use","id":"toolu_1","name":"read_file","input":{"path":"README.md"}}],
             "stop_reason":"tool_use","usage":{"input_tokens":100,"output_tokens":10}}
            """,
            """
            {"content":[{"type":"text","text":"It is a widget service."}],"stop_reason":"end_turn","usage":{"input_tokens":150,"output_tokens":20}}
            """);
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options());
        var tools = new RepoToolSession(_root);

        var result = await LlmToolLoop.RunAsync(provider, Request(), tools, Ct);

        Assert.Equal(new LlmCompletion("It is a widget service.", 250, 30, "end_turn"), result.Completion);
        Assert.Equal(2, result.Turns);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, handler.Requests.Count);

        var first = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        var definitions = first.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(["list_files", "read_file", "search_code"], definitions.Select(t => t.GetProperty("name").GetString()));
        Assert.Equal("object", definitions[1].GetProperty("input_schema").GetProperty("type").GetString());
        Assert.False(first.TryGetProperty("tool_choice", out _));

        var second = JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "user"], second.Select(m => m.GetProperty("role").GetString()));
        var assistant = second[1].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(("text", "Let me look."), (assistant[0].GetProperty("type").GetString(), assistant[0].GetProperty("text").GetString()));
        Assert.Equal(("tool_use", "toolu_1", "read_file"), (assistant[1].GetProperty("type").GetString(), assistant[1].GetProperty("id").GetString(), assistant[1].GetProperty("name").GetString()));
        Assert.Equal("README.md", assistant[1].GetProperty("input").GetProperty("path").GetString());
        var toolResult = Assert.Single(second[2].GetProperty("content").EnumerateArray());
        Assert.Equal(("tool_result", "toolu_1", false), (toolResult.GetProperty("type").GetString(), toolResult.GetProperty("tool_use_id").GetString(), toolResult.GetProperty("is_error").GetBoolean()));
        Assert.Contains("A widget service.", toolResult.GetProperty("content").GetString(), StringComparison.Ordinal);

        var logged = Assert.Single(tools.Calls);
        Assert.Equal(RepoToolNames.ReadFile, logged.Tool);
    }

    [Fact]
    public async Task FR71_Anthropic_unsafe_path_result_is_an_error_tool_result()
    {
        var handler = Sequence(
            """{"content":[{"type":"tool_use","id":"toolu_9","name":"read_file","input":{"path":"../../etc/passwd"}}],"stop_reason":"tool_use","usage":{}}""",
            """{"content":[{"type":"text","text":"done"}],"stop_reason":"end_turn","usage":{}}""");
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options());

        await LlmToolLoop.RunAsync(provider, Request(), new RepoToolSession(_root), Ct);

        var messages = JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Single(messages[1].GetProperty("content").EnumerateArray()); // no empty text block
        var result = Assert.Single(messages[2].GetProperty("content").EnumerateArray());
        Assert.True(result.GetProperty("is_error").GetBoolean());
        Assert.StartsWith("UNSAFE_PATH", result.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR71_after_TOOL_LIMIT_the_next_Anthropic_turn_forces_an_answer_with_tool_choice_none()
    {
        var call = """{"content":[{"type":"tool_use","id":"t","name":"list_files","input":{"path":"."}}],"stop_reason":"tool_use","usage":{"input_tokens":1,"output_tokens":1}}""";
        var bodies = Enumerable.Repeat(call, 26).Append("""{"content":[{"type":"text","text":"final"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""").ToArray();
        var handler = Sequence(bodies);
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options());
        var tools = new RepoToolSession(_root);

        var result = await LlmToolLoop.RunAsync(provider, Request(), tools, Ct);

        Assert.Equal("final", result.Completion.Text);
        Assert.Equal(27, handler.Requests.Count);
        Assert.Equal(26, tools.Calls.Count);
        Assert.Equal(RepoToolErrors.ToolLimit, tools.Calls[^1].Error);
        var last = JsonDocument.Parse(handler.Requests[^1].Body).RootElement;
        Assert.Equal("none", last.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal((27, 27), (result.Completion.InputTokens, result.Completion.OutputTokens));
    }

    // ------------------------------------------------------------------ OpenAI

    [Fact]
    public async Task FR71_OpenAI_tool_loop_sends_function_tools_and_tool_messages()
    {
        var handler = Sequence(
            """
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
               {"id":"call_a","type":"function","function":{"name":"search_code","arguments":"{\"query\":\"widget\"}"}},
               {"id":"call_b","type":"function","function":{"name":"list_files","arguments":"{\"path\":\".\"}"}}]},
              "finish_reason":"tool_calls"}],"usage":{"prompt_tokens":40,"completion_tokens":5}}
            """,
            """
            {"choices":[{"message":{"role":"assistant","content":"Widgets."},"finish_reason":"stop"}],"usage":{"prompt_tokens":60,"completion_tokens":7}}
            """);
        var provider = new OpenAiLlmProvider(new HttpClient(handler), Options());
        var tools = new RepoToolSession(_root);

        var result = await LlmToolLoop.RunAsync(provider, Request(), tools, Ct);

        Assert.Equal(new LlmCompletion("Widgets.", 100, 12, "stop"), result.Completion);
        var first = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        var tool = first.GetProperty("tools").EnumerateArray().First();
        Assert.Equal(("function", "list_files"), (tool.GetProperty("type").GetString(), tool.GetProperty("function").GetProperty("name").GetString()));
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        Assert.False(first.TryGetProperty("tool_choice", out _));

        var messages = JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user", "assistant", "tool", "tool"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.False(messages[2].TryGetProperty("content", out _));
        var echoed = messages[2].GetProperty("tool_calls").EnumerateArray().ToList();
        Assert.Equal(("call_a", "function", "search_code", """{"query":"widget"}"""),
            (echoed[0].GetProperty("id").GetString(), echoed[0].GetProperty("type").GetString(), echoed[0].GetProperty("function").GetProperty("name").GetString(), echoed[0].GetProperty("function").GetProperty("arguments").GetString()));
        Assert.Equal(["call_a", "call_b"], messages.Skip(3).Select(m => m.GetProperty("tool_call_id").GetString()));
        Assert.Contains("README.md:2:", messages[3].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, tools.Calls.Count);
    }

    [Fact]
    public async Task FR71_OpenAI_malformed_arguments_reach_the_tool_as_INVALID_ARGUMENT()
    {
        var handler = Sequence(
            """{"choices":[{"message":{"tool_calls":[{"id":"c","type":"function","function":{"name":"read_file","arguments":"{oops"}}]},"finish_reason":"tool_calls"}],"usage":{}}""",
            """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{}}""");
        var tools = new RepoToolSession(_root);

        await LlmToolLoop.RunAsync(new OpenAiLlmProvider(new HttpClient(handler), Options()), Request(), tools, Ct);

        Assert.Equal(RepoToolErrors.InvalidArgument, Assert.Single(tools.Calls).Error);
        var messages = JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("{oops", messages[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
    }

    // ------------------------------------------------------------------ Ollama

    [Fact]
    public async Task FR71_Ollama_without_tool_support_falls_back_to_context_only_and_warns()
    {
        var handler = new LlmProviderTests.FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/api/show", StringComparison.Ordinal)
            ? Json("""{"capabilities":["completion"]}""")
            : Json("""{"message":{"role":"assistant","content":"From context."},"done_reason":"stop","prompt_eval_count":9,"eval_count":3}"""));
        var provider = new OllamaLlmProvider(new HttpClient(handler), Options());
        var tools = new RepoToolSession(_root);

        var result = await LlmToolLoop.RunAsync(provider, Request(), tools, Ct);

        Assert.Equal([LlmToolLoop.ToolsUnsupportedWarning], result.Warnings);
        Assert.Equal("tools_unsupported", LlmToolLoop.ToolsUnsupportedWarning);
        Assert.Equal(new LlmCompletion("From context.", 9, 3, "stop"), result.Completion);
        Assert.Empty(tools.Calls);
        var chat = JsonDocument.Parse(handler.Requests[1].Body).RootElement;
        Assert.False(chat.TryGetProperty("tools", out _));
        var user = chat.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        Assert.StartsWith("Describe the repo.", user, StringComparison.Ordinal);
        Assert.Contains("<repository_files>", user, StringComparison.Ordinal);
        Assert.Contains("README.md", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR71_Ollama_that_cannot_be_asked_about_capabilities_counts_as_no_tool_support()
    {
        var handler = new LlmProviderTests.FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/api/show", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json("""{"message":{"content":"ok"}}"""));

        var result = await LlmToolLoop.RunAsync(new OllamaLlmProvider(new HttpClient(handler), Options()), Request(), new RepoToolSession(_root), Ct);

        Assert.Equal([LlmToolLoop.ToolsUnsupportedWarning], result.Warnings);
    }

    [Fact]
    public async Task FR71_Ollama_with_tool_support_runs_the_loop()
    {
        var chats = 0;
        var handler = new LlmProviderTests.FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/show", StringComparison.Ordinal))
            {
                return Json("""{"capabilities":["completion","tools"]}""");
            }

            return chats++ == 0
                ? Json("""{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"read_file","arguments":{"path":"README.md"}}}]},"prompt_eval_count":5,"eval_count":1}""")
                : Json("""{"message":{"role":"assistant","content":"Widgets."},"done_reason":"stop","prompt_eval_count":8,"eval_count":2}""");
        });

        var result = await LlmToolLoop.RunAsync(new OllamaLlmProvider(new HttpClient(handler), Options()), Request(), new RepoToolSession(_root), Ct);

        Assert.Equal(new LlmCompletion("Widgets.", 13, 3, "stop"), result.Completion);
        Assert.Empty(result.Warnings);
        var messages = JsonDocument.Parse(handler.Requests[2].Body).RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user", "assistant", "tool"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("README.md", messages[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetProperty("path").GetString());
        Assert.Equal("read_file", messages[3].GetProperty("tool_name").GetString());
        Assert.Equal("function", JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
    }

    // ------------------------------------------------------------------ Fake and the loop itself

    [Fact]
    public async Task FR71_Fake_scripted_read_of_etc_passwd_gets_UNSAFE_PATH_and_then_answers()
    {
        var fake = new FakeLlmProvider([], [new FakeToolScript(AgentKind.IdeaRefinement, FakeToolScripts.PathEscape)]);
        var tools = new RepoToolSession(_root);

        var result = await LlmToolLoop.RunAsync(fake, Request(AgentKind.IdeaRefinement), tools, Ct);

        var call = Assert.Single(tools.Calls);
        Assert.Equal((RepoToolNames.ReadFile, RepoToolErrors.UnsafePath, 0), (call.Tool, call.Error, call.Bytes));
        Assert.Equal("../../etc/passwd", call.Arguments!["path"]!.GetValue<string>());
        Assert.Contains("Fake idea_refinement output", result.Completion.Text, StringComparison.Ordinal);
        Assert.Equal(2, result.Turns);
    }

    [Fact]
    public async Task FR71_Fake_script_of_30_calls_stops_at_TOOL_LIMIT_and_answers_with_what_it_has()
    {
        var fake = new FakeLlmProvider([], [new FakeToolScript(AgentKind.SpecGenerator, FakeToolScripts.Many(30))]);
        var tools = new RepoToolSession(_root);

        var result = await LlmToolLoop.RunAsync(fake, Request(), tools, Ct);

        Assert.Equal(26, tools.Calls.Count);
        Assert.All(tools.Calls.Take(25), c => Assert.Null(c.Error));
        Assert.Equal(RepoToolErrors.ToolLimit, tools.Calls[25].Error);
        Assert.Equal(27, result.Turns);
        Assert.Equal(FakeLlmProvider.StopReason, result.Completion.StopReason);
        Assert.True(result.Completion.InputTokens > 27);
    }

    [Fact]
    public async Task FR71_Fake_default_script_explores_and_gives_the_same_answer_as_without_tools()
    {
        var fake = new FakeLlmProvider([], FakeToolScripts.Defaults());
        var tools = new RepoToolSession(_root);

        var looped = await LlmToolLoop.RunAsync(fake, Request(), tools, Ct);
        var plain = await fake.CompleteAsync(Request(), Ct);

        Assert.Equal([RepoToolNames.ListFiles, RepoToolNames.ReadFile], tools.Calls.Select(c => c.Tool));
        Assert.Equal(plain.Text, looped.Completion.Text);
    }

    [Fact]
    public async Task FR71_without_an_executor_the_loop_is_one_plain_call()
    {
        var fake = new FakeLlmProvider([], FakeToolScripts.Defaults());

        var result = await LlmToolLoop.RunAsync(fake, Request(), null, Ct);

        Assert.Equal((1, 0), (result.Turns, result.Warnings.Count));
    }

    [Fact]
    public async Task FR71_a_model_that_never_stops_calling_tools_is_cut_off_at_the_turn_cap()
    {
        var provider = new EndlessToolCaller();
        var tools = new RepoToolSession(_root, budget: new RepoToolBudget(maxCalls: 1000, maxBytes: int.MaxValue));

        var result = await LlmToolLoop.RunAsync(provider, Request(), tools, Ct);

        Assert.Equal(LlmToolLoop.MaxTurns, result.Turns);
        Assert.True(provider.LastForced);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class EndlessToolCaller : ILlmProvider, IToolCallingLlmProvider
    {
        public bool LastForced { get; private set; }

        public Aide.Domain.Entities.LlmProvider Kind => Aide.Domain.Entities.LlmProvider.Fake;

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SupportsToolsAsync(LlmRequest request, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<LlmToolTurn> CompleteTurnAsync(LlmToolRequest toolRequest, CancellationToken cancellationToken)
        {
            LastForced = toolRequest.ForceFinalAnswer;
            return Task.FromResult(new LlmToolTurn("still going", [new LlmToolCall("x", RepoToolNames.ListFiles, "{}")], 1, 1, "tool_use"));
        }
    }
}
