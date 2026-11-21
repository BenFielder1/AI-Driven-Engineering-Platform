using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// FR-317, FR-319: the host's tool session and prompt hooks around the engine tool loop (0.6.1 parity for the tool-using
/// SDLC agents): the limit, the fallback context, raw arguments, tool exceptions, the log, no tools, and the prompt
/// transform. Defaults keep the engine's behaviour.
/// </summary>
public class AgentToolSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Pace = """{"q":"pace"}""";

    private static (AgentStageHarness Harness, RecordingToolProvider Provider, ScriptedResponder Drafter) Drafting(
        ToolOptionsExtension extension, int calls = 1, bool supportsTools = true)
    {
        var drafter = new ScriptedResponder("drafter", AgentStageHarness.ValidDraft);
        var script = new FakeToolScript("drafter", [.. Enumerable.Repeat(new FakeToolCall("lookup", Pace), calls)]);
        var provider = new RecordingToolProvider(new FakeLlmProvider([drafter], [script]), supportsTools);
        var harness = new AgentStageHarness([], resolver: new KeyedResolver(provider, "sk-test"), extension: extension);
        return (harness, provider, drafter);
    }

    [Fact]
    public async Task FR317_a_session_refusal_reports_the_limit_and_forces_the_final_answer_with_its_message()
    {
        var session = new ConfigurableToolSession { RefuseFromCall = 2 };
        var (harness, provider, _) = Drafting(new ToolOptionsExtension { Session = session }, calls: 3);
        using var disposing = harness;

        var result = await harness.RunStageAsync("draft");

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Equal([false, false, true], provider.Turns.Select(t => t.ForceFinalAnswer));
        var refused = provider.Turns[^1].Exchanges[^1].Results.Single();
        Assert.Equal((session.RefusalText, true), (refused.Content, refused.IsError));
        Assert.Equal((2, 1), (session.Started, session.Ran));
        Assert.Contains("\"isError\":true", harness.Store.AgentRuns[0].ToolCalls, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR317_without_a_session_the_limit_is_never_reached()
    {
        var (harness, provider, _) = Drafting(new ToolOptionsExtension(), calls: 3);
        using var disposing = harness;

        await harness.RunStageAsync("draft");

        Assert.Equal([false, false, false, false], provider.Turns.Select(t => t.ForceFinalAnswer));
    }

    [Fact]
    public async Task FR317_the_session_supplies_the_fallback_context_for_a_provider_without_tools()
    {
        var session = new ConfigurableToolSession { Fallback = "<repository_files>\nREADME.md  (10 bytes)\n</repository_files>" };
        var (harness, provider, drafter) = Drafting(new ToolOptionsExtension { Session = session }, supportsTools: false);
        using var disposing = harness;

        await harness.RunStageAsync("draft");

        Assert.Empty(provider.Turns);
        Assert.EndsWith("\n\n" + session.Fallback, Assert.Single(drafter.Prompts), StringComparison.Ordinal);
        Assert.Contains(LlmToolLoop.ToolsUnsupportedWarning, harness.Store.AgentRuns[0].Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR317_the_default_fallback_context_is_none()
    {
        var (harness, _, drafter) = Drafting(new ToolOptionsExtension(), supportsTools: false);
        using var disposing = harness;

        await harness.RunStageAsync("draft");

        Assert.DoesNotContain("\n\n\n", Assert.Single(drafter.Prompts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR317_a_raw_arguments_tool_gets_malformed_json_as_is()
    {
        var raw = new RawTool();
        var executor = new AgentToolExecutor([raw]);

        var result = await executor.ExecuteAsync(new LlmToolCall("c1", "raw", "{not json"), Ct);

        Assert.Equal(["{not json"], raw.Received);
        Assert.Equal(("INVALID_ARGUMENT: 'path' is required.", true), (result.Content, result.IsError));
        Assert.True(Assert.Single(executor.Calls).IsError);
    }

    [Fact]
    public async Task FR317_malformed_json_for_a_parsed_tool_is_the_engine_message_by_default_or_the_sessions()
    {
        var call = new LlmToolCall("c1", "lookup", "{not json");

        var engine = await new AgentToolExecutor([new EchoTool()]).ExecuteAsync(call, Ct);
        var host = await new AgentToolExecutor([new EchoTool()], new ConfigurableToolSession()).ExecuteAsync(call, Ct);

        Assert.Equal(("The arguments are not valid JSON.", true), (engine.Content, engine.IsError));
        Assert.Equal(("INVALID_ARGUMENT: The arguments must be a JSON object.", true), (host.Content, host.IsError));
    }

    [Fact]
    public async Task FR317_a_tool_exception_is_an_error_result_by_default_and_rethrown_when_the_session_says_so()
    {
        var call = new LlmToolCall("c1", "lookup", Pace);

        var engine = await new AgentToolExecutor([new ThrowingTool()]).ExecuteAsync(call, Ct);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AgentToolExecutor([new ThrowingTool()], new ConfigurableToolSession { Rethrow = true }).ExecuteAsync(call, Ct));

        Assert.Equal(("The tool 'lookup' failed.", true), (engine.Content, engine.IsError));
        Assert.Equal("tool broke", ex.Message);
    }

    [Fact]
    public async Task FR317_a_rethrown_tool_exception_fails_the_run_with_PROVIDER_ERROR()
    {
        var tools = new AgentToolExecutor([new ThrowingTool()], new ConfigurableToolSession { Rethrow = true });
        var (harness, _, _) = Drafting(new ToolOptionsExtension { Tools = tools });
        using var disposing = harness;

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("draft"));

        Assert.Equal((EngineErrorCodes.ProviderError, false), (ex.ErrorCode, ex.Transient));
        Assert.Equal("anthropic call failed unexpectedly (InvalidOperationException).", ex.Message);
        Assert.Equal(AgentRunStatus.Failed, Assert.Single(harness.Store.AgentRuns).Status);
    }

    [Fact]
    public async Task FR317_the_unknown_tool_text_is_the_engines_by_default_or_the_sessions()
    {
        var call = new LlmToolCall("c1", "delete_repo", "{}");

        var engine = await new AgentToolExecutor([new EchoTool()]).ExecuteAsync(call, Ct);
        var host = await new AgentToolExecutor([new EchoTool()], new ConfigurableToolSession()).ExecuteAsync(call, Ct);

        Assert.Equal("Unknown tool 'delete_repo'.", engine.Content);
        Assert.Equal("INVALID_ARGUMENT: Unknown tool 'delete_repo'. Use lookup.", host.Content);
    }

    [Fact]
    public async Task FR317_the_engine_log_redacts_and_caps_arguments_and_the_session_may_own_the_log_format()
    {
        var executor = new AgentToolExecutor([new EchoTool()]);
        await executor.ExecuteAsync(new LlmToolCall("c1", "lookup", """{"key":"sk-ant-api03-abcdefghijklmnopqrstuv"}"""), Ct);
        await executor.ExecuteAsync(new LlmToolCall("c2", "lookup", "{\"q\":\"" + new string('a', 3000) + "\"}"), Ct);

        Assert.DoesNotContain("abcdefghijklmnop", executor.Calls[0].Arguments, StringComparison.Ordinal);
        Assert.Contains(SecretRedaction.Mask, executor.Calls[0].Arguments, StringComparison.Ordinal);
        Assert.Equal(AgentToolExecutor.MaxLoggedArgumentLength + 1, executor.Calls[1].Arguments.Length);
        Assert.EndsWith("…", executor.Calls[1].Arguments, StringComparison.Ordinal);
        Assert.Equal("[{\"x\":1}]", new AgentToolExecutor([], new ConfigurableToolSession { LogJson = "[{\"x\":1}]" }).CallsJson());
    }

    [Fact]
    public async Task FR317_the_session_log_format_is_stored_on_the_run()
    {
        var (harness, _, _) = Drafting(new ToolOptionsExtension { Session = new ConfigurableToolSession { LogJson = "[\"host\"]" } });
        using var disposing = harness;

        await harness.RunStageAsync("draft");

        Assert.Equal("[\"host\"]", harness.Store.AgentRuns[0].ToolCalls);
    }

    [Fact]
    public async Task FR317_no_tools_for_a_call_is_a_plain_completion_distinct_from_the_stages_tools()
    {
        var (harness, provider, _) = Drafting(new ToolOptionsExtension { Tools = AgentToolExecutor.None });
        using var disposing = harness;

        await harness.RunStageAsync("draft");

        Assert.Empty(provider.Turns);
        Assert.Single(provider.Plain);
        Assert.Equal("[]", harness.Store.AgentRuns[0].ToolCalls);
        Assert.DoesNotContain(LlmToolLoop.ToolsUnsupportedWarning, harness.Store.AgentRuns[0].Output ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR317_null_tools_means_the_stages_tools()
    {
        var (harness, provider, _) = Drafting(new ToolOptionsExtension());
        using var disposing = harness;

        await harness.RunStageAsync("draft");

        Assert.Equal("lookup", Assert.Single(provider.Turns[0].Tools).Name);
    }

    [Fact]
    public async Task FR319_the_host_transforms_the_rendered_prompt_before_the_call_and_the_input_records_it()
    {
        var extension = new ToolOptionsExtension
        {
            Tools = AgentToolExecutor.None,
            Transform = p => new RenderedPrompt(p.System + "\n## Role: specialist", p.User + "\n## Step input"),
        };
        var (harness, provider, drafter) = Drafting(extension);
        using var disposing = harness;

        await harness.RunStageAsync("draft");

        var sent = Assert.Single(provider.Plain);
        Assert.EndsWith("\n## Role: specialist", sent.System, StringComparison.Ordinal);
        Assert.EndsWith("\n## Step input", Assert.Single(drafter.Prompts), StringComparison.Ordinal);
        Assert.Contains("## Role: specialist", harness.Store.AgentRuns[0].Input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR319_the_default_prompt_transform_is_identity()
    {
        var plain = new ScriptedResponder("drafter", AgentStageHarness.ValidDraft);
        using (var harness = new AgentStageHarness([plain]))
        {
            await harness.RunStageAsync("draft");
        }

        var (other, _, drafter) = Drafting(new ToolOptionsExtension { Tools = AgentToolExecutor.None });
        using var disposing = other;
        await other.RunStageAsync("draft");

        Assert.Equal(plain.Prompts, drafter.Prompts);
    }

    [Fact]
    public void FR317_a_redaction_timeout_logs_a_placeholder_instead_of_failing()
    {
        var logged = AgentToolExecutor.LoggedArguments("{}", _ => throw new System.Text.RegularExpressions.RegexMatchTimeoutException());

        Assert.Equal(AgentToolExecutor.RedactionTimedOut, logged);
    }

    [Fact]
    public async Task FR317_a_rethrown_tool_exception_is_recorded_in_the_log_first()
    {
        var executor = new AgentToolExecutor([new ThrowingTool()], new ConfigurableToolSession { Rethrow = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(new LlmToolCall("c1", "lookup", Pace), Ct));

        Assert.Equal(new AgentToolCallRecord("lookup", Pace, IsError: true), Assert.Single(executor.Calls));
    }

    [Fact]
    public async Task FR317_the_no_tools_executor_is_stateless_and_shared_safely()
    {
        var none = AgentToolExecutor.None;

        var result = await none.ExecuteAsync(new LlmToolCall("c1", "lookup", Pace), Ct);

        Assert.Same(none, AgentToolExecutor.None);
        Assert.Empty(none.Definitions);
        Assert.False(none.LimitReached);
        Assert.True(result.IsError);
        Assert.Null(await none.FallbackContextAsync(Ct));
        Assert.IsNotType<AgentToolExecutor>(none);
    }
}
