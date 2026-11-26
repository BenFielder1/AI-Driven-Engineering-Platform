using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// FR-360 (M71d) through the runner, on the Fake provider: a call to an external tool (<c>tiers.send</c>, read but
/// external) saves the loop, raises an <c>external_action</c> gate showing its exact arguments and defers the job in every
/// approval mode; approving runs it once with those arguments and resumes; rejecting sends the model the denial (as
/// FR-359 does); an external tool that is also destructive (<c>tiers.pay</c>) gets the one <c>external_action</c> gate; and a
/// <c>tool_approval</c> decision never runs an external tool.
/// </summary>
public sealed class ExternalToolRunnerTests : IDisposable
{
    private const string DrafterLine = "    agent: { key: drafter, prompt: fixture.draft, tools: [fixture.lookup] }\n";
    private const string SendArguments = """{"to":"ops@example.test","body":"Pod api-0 restarted"}""";

    private static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly ToolRunLog _log = new();
    private readonly ManualClock _clock = new(Start);
    private readonly Guid _agentRunId = Guid.CreateVersion7();
    private AgentStageHarness? _harness;
    private RecordingToolProvider? _provider;

    private AgentStageHarness Harness => _harness!;

    private List<LlmToolRequest> Turns => _provider!.Turns;

    public void Dispose() => _harness?.Dispose();

    private static string WithDrafter(string yaml, string maxTier)
    {
        Assert.Contains(DrafterLine, yaml, StringComparison.Ordinal);
        var tools = maxTier == "destructive"
            ? $"{TierToolsPack.Scan}, {TierToolsPack.Wipe}, {TierToolsPack.Send}, {TierToolsPack.Pay}"
            : $"{TierToolsPack.Scan}, {TierToolsPack.Send}";
        return yaml.Replace(
            DrafterLine,
            $"    agent: {{ key: drafter, prompt: fixture.draft, tools: [{tools}] }}\n    tool_policy: {{ max_tier: {maxTier} }}\n",
            StringComparison.Ordinal);
    }

    private void Create(string maxTier, params FakeToolCall[] calls)
    {
        var drafter = new ScriptedResponder("drafter", AgentStageHarness.ValidDraft);
        _provider = new RecordingToolProvider(new FakeLlmProvider([drafter], [new FakeToolScript("drafter", calls)]));
        _harness = new AgentStageHarness(
            [],
            yaml => WithDrafter(yaml, maxTier),
            resolver: new SwitchKeyResolver(_provider, "sk-ant-test-0123456789abcdef"),
            configure: s =>
            {
                TierToolsPack.AddTo(s, _log);
                s.AddSingleton<TimeProvider>(_clock);
            });
    }

    private Task<AgentStageResult> DeliverAsync() => Harness.RunStageAsync("draft", _agentRunId);

    private async Task<JobDeferredException> DeferredAsync() => await Assert.ThrowsAsync<JobDeferredException>(DeliverAsync);

    private AgentRun Saved() => Harness.Store.AgentRuns.Single(r => r.Id == _agentRunId);

    private ApprovalGate Gate() => Assert.Single(Harness.Store.Gates);

    private LlmToolResult LastResultSent() => Turns[^1].Exchanges[^1].Results[^1];

    private static void Decide(ApprovalGate gate, GateStatus status, Guid? reviewer = null, string? comment = null)
    {
        gate.Status = status;
        gate.ReviewerId = reviewer;
        gate.Comment = comment;
        gate.DecidedAt = DateTimeOffset.UtcNow;
    }

    [Theory]
    [InlineData(ApprovalMode.Autonomous)]
    [InlineData(ApprovalMode.Advisory)]
    [InlineData(ApprovalMode.Strict)]
    public async Task FR360_an_external_tool_call_raises_an_external_action_gate_with_its_exact_arguments_in_every_mode_and_never_runs_before_approval(ApprovalMode mode)
    {
        Create("write", new FakeToolCall("scan", "{}"), new FakeToolCall("send", SendArguments));
        Harness.Run.ApprovalMode = mode;

        var deferred = await DeferredAsync();
        await DeferredAsync();

        Assert.Equal(["scan"], _log.Ran);
        Assert.Equal(2, Turns.Count);
        Assert.Equal(Start + ToolApprovalRules.WaitPoll, deferred.RunAfter);
        var gate = Gate();
        Assert.Equal((EngineGates.ExternalAction, GateStatus.Pending), (gate.Type, gate.Status));
        using (var payload = JsonDocument.Parse(gate.Payload!))
        {
            Assert.Equal(TierToolsPack.Send, payload.RootElement.GetProperty("tool").GetString());
            Assert.Equal("read", payload.RootElement.GetProperty("tier").GetString());
            Assert.Equal(SendArguments, payload.RootElement.GetProperty("arguments").GetString());
            Assert.Equal(_agentRunId, payload.RootElement.GetProperty("agentRunId").GetGuid());
        }

        var state = ToolLoopStates.Read(Saved().ToolLoopState)!;
        Assert.Equal((EngineGates.ExternalAction, gate.Id), (ToolLoopStates.GateTypeOf(state), state.GateId));
        Assert.Contains($"gate:{EngineGates.ExternalAction}:", string.Join("|", Harness.Hooks.Calls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR360_approving_runs_the_external_tool_once_with_the_arguments_shown_and_resumes_the_loop()
    {
        Create("write", new FakeToolCall("send", SendArguments));
        await DeferredAsync();
        Decide(Gate(), GateStatus.Approved, Guid.CreateVersion7());

        var result = await DeliverAsync();

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Equal(["send"], _log.Ran);
        Assert.Equal([SendArguments], _log.Arguments);
        Assert.Equal(("ran send", false), (LastResultSent().Content, LastResultSent().IsError));
        Assert.Contains("\"ranAt\"", Gate().Payload, StringComparison.Ordinal);
        Assert.Null(Saved().ToolLoopState);

        await DeliverAsync();
        Assert.Equal(["send"], _log.Ran);
    }

    [Fact]
    public async Task FR360_rejecting_sends_the_model_the_denial_and_the_external_tool_never_runs()
    {
        Create("write", new FakeToolCall("send", SendArguments));
        await DeferredAsync();
        var owner = Guid.CreateVersion7();
        Decide(Gate(), GateStatus.Rejected, owner, "not to customers");

        var result = await DeliverAsync();

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Empty(_log.Ran);
        Assert.Equal(($"denied by {owner}: not to customers", true), (LastResultSent().Content, LastResultSent().IsError));
    }

    [Fact]
    public async Task FR360_an_external_tool_that_is_also_destructive_gets_one_external_action_gate()
    {
        Create("destructive", new FakeToolCall("pay", """{"amount":10}"""));

        await DeferredAsync();

        Assert.Equal(EngineGates.ExternalAction, Gate().Type);
        Assert.Empty(_log.Ran);
        Decide(Gate(), GateStatus.Approved, Guid.CreateVersion7());

        await DeliverAsync();

        Assert.Single(Harness.Store.Gates);
        Assert.Equal(["pay"], _log.Ran);
    }

    [Fact]
    public async Task FR360_a_destructive_tool_still_gets_tool_approval()
    {
        Create("destructive", new FakeToolCall("wipe", "{}"));

        await DeferredAsync();

        Assert.Equal(EngineGates.ToolApproval, Gate().Type);
        Assert.Null(ToolLoopStates.Read(Saved().ToolLoopState)!.GateType);
    }

    [Fact]
    public async Task FR360_a_tool_approval_decision_never_runs_an_external_tool()
    {
        Create("write", new FakeToolCall("send", SendArguments));
        await DeferredAsync();
        var gate = Gate();
        gate.Type = EngineGates.ToolApproval;
        Decide(gate, GateStatus.Approved, Guid.CreateVersion7());

        var failed = await Assert.ThrowsAsync<JobFailedException>(DeliverAsync);

        Assert.Equal(EngineErrorCodes.RunNotActive, failed.ErrorCode);
        Assert.Empty(_log.Ran);
        Assert.Equal((AgentRunStatus.Cancelled, (string?)null), (Saved().Status, Saved().ToolLoopState));
    }
}
