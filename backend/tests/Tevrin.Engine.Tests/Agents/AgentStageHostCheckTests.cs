using System.Text.Json;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// FR-314, FR-317 (M62c): a host's own output checks with its <c>on_invalid</c> policy, and host tools for a call, on the
/// fixture pipeline. Without them, the definition's validation and the stage's tools are used (the defaults).
/// </summary>
public class AgentStageHostCheckTests
{
    private const string Rejected = """{"title":"reject","items":["run 5k"]}""";

    [Fact]
    public async Task FR314_a_host_check_rejects_an_output_and_its_policy_retries_it()
    {
        var extension = new HostCheckExtension { Policy2 = new OnInvalidPolicy(true, 2, InvalidOutcome.Fail) };
        var responder = new ScriptedResponder("drafter", Rejected, AgentStageHarness.ValidDraft);
        using var harness = new AgentStageHarness([responder], extension: extension);

        var result = await harness.RunStageAsync("draft");

        Assert.Equal((AgentStageEnd.Stored, 2), (result.End, result.Attempts.Count));
        Assert.Equal("The title may not be reject.", result.Attempts[0].Findings);
        Assert.Contains("The title may not be reject.", responder.Prompts[1], StringComparison.Ordinal);
        Assert.Equal(AgentStageHarness.ValidDraft, Assert.Single(harness.Store.Artifacts).Content);
    }

    [Fact]
    public async Task FR314_a_host_check_without_a_policy_fails_on_the_first_invalid_output()
    {
        var extension = new HostCheckExtension();
        using var harness = new AgentStageHarness([new ScriptedResponder("drafter", Rejected)], extension: extension);

        var result = await harness.RunStageAsync("draft");

        Assert.Equal((AgentStageEnd.Failed, 1), (result.End, result.Attempts.Count));
        Assert.Empty(harness.Store.Artifacts);
    }

    [Fact]
    public void FR314_the_default_policy_is_the_definitions()
    {
        var stage = new StageDefinition { Id = "s", OnInvalid = new OnInvalidPolicy(true, 3, InvalidOutcome.Escalate) };

        Assert.Same(stage.OnInvalid, AgentStageExtension.Default.OnInvalid(stage));
    }

    [Fact]
    public async Task FR317_host_tools_replace_the_stage_tools_and_their_log_is_the_runs()
    {
        var tools = new HostTools();
        var extension = new HostCheckExtension { Options = new AgentCallOptions { Tools = tools, ToolCalls = () => "[\"host\"]" } };
        var script = new FakeToolScript("drafter", [new FakeToolCall("lookup", """{"q":"pace"}""")]);
        using var harness = new AgentStageHarness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)], scripts: [script], extension: extension);

        await harness.RunStageAsync("draft");

        Assert.Equal(["lookup"], tools.Calls);
        Assert.Equal("[\"host\"]", Assert.Single(harness.Store.AgentRuns).ToolCalls);
    }

    [Fact]
    public async Task FR317_a_host_log_of_null_keeps_the_runs_log()
    {
        var extension = new HostCheckExtension { Options = new AgentCallOptions { ToolCalls = () => null } };
        var script = new FakeToolScript("drafter", [new FakeToolCall("lookup", """{"q":"pace"}""")]);
        using var harness = new AgentStageHarness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)], scripts: [script], extension: extension);

        await harness.RunStageAsync("draft");

        // The stage tool was called, but the host's log (none) wins and the run keeps its log.
        Assert.Equal("[]", Assert.Single(harness.Store.AgentRuns).ToolCalls);
    }

    private sealed class HostCheckExtension : AgentStageExtension
    {
        public OnInvalidPolicy? Policy2 { get; init; }

        public AgentCallOptions Options { get; init; } = AgentCallOptions.None;

        public override string PipelineId => "fixture-review";

        public override Task<IReadOnlyList<string>> ValidateOutputAsync(AgentAttempt attempt, string content, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(content.Contains("\"reject\"", StringComparison.Ordinal) ? ["The title may not be reject."] : []);

        public override OnInvalidPolicy? OnInvalid(StageDefinition stage) => Policy2 ?? base.OnInvalid(stage);

        public override Task<AgentCallOptions> CallOptionsAsync(AgentAttempt attempt, CancellationToken cancellationToken) => Task.FromResult(Options);
    }

    private sealed class HostTools : ILlmToolExecutor
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<LlmToolDefinition> Definitions { get; } =
            [new LlmToolDefinition("lookup", "Host lookup.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone())];

        public bool LimitReached => false;

        public Task<LlmToolResult> ExecuteAsync(LlmToolCall toolCall, CancellationToken cancellationToken)
        {
            Calls.Add(toolCall.Name);
            return Task.FromResult(new LlmToolResult(toolCall.Id, "host result", false));
        }

        public Task<string?> FallbackContextAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
