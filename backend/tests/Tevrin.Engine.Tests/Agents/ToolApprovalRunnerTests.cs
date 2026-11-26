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

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>A resolver whose key a test can remove (FR-359: a resume that can't call the model never runs the tool).</summary>
internal sealed class SwitchKeyResolver(ILlmProvider provider, string apiKey) : ILlmResolver
{
    public string? Key { get; set; } = apiKey;

    public Task<ResolvedLlm> ResolveAsync(Guid projectId, string agent, CancellationToken cancellationToken) =>
        Task.FromResult(new ResolvedLlm(LlmProvider.Anthropic, "model-x", Key));

    public Task<LlmKey> KeyAsync(Guid projectId, LlmProvider kind, CancellationToken cancellationToken) => Task.FromResult(new LlmKey(Key, null));

    public ILlmProvider ProviderFor(LlmProvider kind) => provider;
}

/// <summary>Answers the first turn with all of the calls in one message, then the valid draft.</summary>
internal sealed class OneMessageCallsProvider(params LlmToolCall[] calls) : ILlmProvider, IToolCallingLlmProvider
{
    public List<LlmToolRequest> Turns { get; } = [];

    public LlmProvider Kind => LlmProvider.Fake;

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No plain call is expected.");

    public Task<bool> SupportsToolsAsync(LlmRequest request, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<LlmToolTurn> CompleteTurnAsync(LlmToolRequest toolRequest, CancellationToken cancellationToken)
    {
        Turns.Add(toolRequest);
        return Task.FromResult(toolRequest.Exchanges.Count == 0
            ? new LlmToolTurn("Doing both.", calls, 10, 5, "tool_use")
            : new LlmToolTurn(AgentStageHarness.ValidDraft, [], 10, 5, "end_turn"));
    }
}

/// <summary>
/// FR-359, §4.73 through the runner, on the Fake provider: a call to the destructive <c>tiers.wipe</c> saves the loop, raises
/// a <c>tool_approval</c> gate and defers the job; redeliveries make no model call while it waits; approving runs the tool
/// once and resumes from the saved messages; rejecting (or the 24-hour timer) sends the denial; and nothing ever runs the
/// tool without an approved gate. Each delivery runs under the same agent run id, as one job's would (FR-310).
/// </summary>
public sealed class ToolApprovalRunnerTests : IDisposable
{
    private const string DrafterLine = "    agent: { key: drafter, prompt: fixture.draft, tools: [fixture.lookup] }\n";
    private const string ApiKey = "sk-ant-test-0123456789abcdef";

    private static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly ToolRunLog _log = new();
    private readonly ManualClock _clock = new(Start);
    private readonly Guid _agentRunId = Guid.CreateVersion7();
    private AgentStageHarness? _harness;
    private Func<IReadOnlyList<LlmToolRequest>>? _turns;
    private SwitchKeyResolver? _resolver;

    private AgentStageHarness Harness => _harness!;

    private SwitchKeyResolver Resolver => _resolver!;

    private IReadOnlyList<LlmToolRequest> Turns => _turns!();

    public void Dispose() => _harness?.Dispose();

    private static string WithDrafter(string yaml)
    {
        Assert.Contains(DrafterLine, yaml, StringComparison.Ordinal);
        return yaml.Replace(
            DrafterLine,
            $"    agent: {{ key: drafter, prompt: fixture.draft, tools: [{TierToolsPack.Scan}, {TierToolsPack.Peek}, {TierToolsPack.Wipe}, {TierToolsPack.Send}] }}\n    tool_policy: {{ max_tier: destructive }}\n",
            StringComparison.Ordinal);
    }

    private void Create(params FakeToolCall[] calls) => Create(null, calls);

    private void Create(AgentStageExtension? extension, params FakeToolCall[] calls)
    {
        var drafter = new ScriptedResponder("drafter", AgentStageHarness.ValidDraft);
        var provider = new RecordingToolProvider(new FakeLlmProvider([drafter], [new FakeToolScript("drafter", calls)]));
        _turns = () => provider.Turns;
        CreateHarness(provider, extension);
    }

    private void CreateOneMessage(AgentStageExtension? extension, params LlmToolCall[] calls)
    {
        var provider = new OneMessageCallsProvider(calls);
        _turns = () => provider.Turns;
        CreateHarness(provider, extension);
    }

    private void CreateHarness(ILlmProvider provider, AgentStageExtension? extension)
    {
        _resolver = new SwitchKeyResolver(provider, ApiKey);
        _harness = new AgentStageHarness(
            [],
            WithDrafter,
            resolver: _resolver,
            extension: extension,
            configure: s =>
            {
                TierToolsPack.AddTo(s, _log);
                s.AddSingleton<TimeProvider>(_clock);
            });
    }

    private Task<AgentStageResult> DeliverAsync(Guid? agentRunId = null) =>
        Harness.RunStageAsync("draft", agentRunId ?? _agentRunId);

    private async Task<JobDeferredException> DeferredAsync(Guid? agentRunId = null) =>
        await Assert.ThrowsAsync<JobDeferredException>(() => DeliverAsync(agentRunId));

    private AgentRun Saved(Guid? agentRunId = null) => Harness.Store.AgentRuns.Single(r => r.Id == (agentRunId ?? _agentRunId));

    private ApprovalGate Gate() => Assert.Single(Harness.Store.Gates);

    private static List<(string Tool, string? Tier, bool IsError)> Calls(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(e => (
            e.GetProperty("tool").GetString()!,
            e.TryGetProperty("tier", out var tier) ? tier.GetString() : null,
            e.GetProperty("isError").GetBoolean()))];
    }

    private static void Decide(ApprovalGate gate, GateStatus status, Guid? reviewer = null, string? comment = null)
    {
        gate.Status = status;
        gate.ReviewerId = reviewer;
        gate.Comment = comment;
        gate.DecidedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>The result the model got for the destructive call: the last result of the last turn's exchanges.</summary>
    private LlmToolResult LastResultSent() => Turns[^1].Exchanges[^1].Results[^1];

    [Fact]
    public async Task FR359_a_destructive_call_saves_the_loop_raises_a_tool_approval_gate_and_defers_without_running_it()
    {
        Create(new FakeToolCall("scan", "{}"), new FakeToolCall("wipe", """{"target":"db-1","reason":"disk is full"}"""));

        var deferred = await DeferredAsync();

        Assert.Equal(["scan"], _log.Ran);
        Assert.Equal(2, Turns.Count);
        var gate = Gate();
        Assert.Equal((EngineGates.ToolApproval, GateStatus.Pending, Harness.Run.Id), (gate.Type, gate.Status, gate.PipelineRunId));
        Assert.Equal(Start + ToolApprovalRules.WaitPoll, deferred.RunAfter);
        using (var payload = JsonDocument.Parse(gate.Payload!))
        {
            var root = payload.RootElement;
            Assert.Equal(TierToolsPack.Wipe, root.GetProperty("tool").GetString());
            Assert.Equal("destructive", root.GetProperty("tier").GetString());
            Assert.Equal("""{"target":"db-1","reason":"disk is full"}""", root.GetProperty("arguments").GetString());
            Assert.Equal("disk is full", root.GetProperty("reason").GetString());
            Assert.Equal(_agentRunId, root.GetProperty("agentRunId").GetGuid());
        }

        var saved = Saved();
        Assert.Equal((AgentRunStatus.Running, (DateTimeOffset?)null), (saved.Status, saved.CompletedAt));
        var state = ToolLoopStates.Read(saved.ToolLoopState)!;
        Assert.Equal((ToolApprovalPhase.PendingApproval, gate.Id, 2), (state.Phase, state.GateId, state.Turn));
        Assert.Equal(("wipe", TierToolsPack.Wipe), (state.Pending.Name, state.Pending.Tool));
        Assert.Single(state.Exchanges);
        Assert.Equal([("scan", "read", false)], Calls(saved.ToolCalls));
        Assert.Contains($"gate:{EngineGates.ToolApproval}:", string.Join("|", Harness.Hooks.Calls), StringComparison.Ordinal);
        Assert.Contains(Harness.Run.Id, Harness.Store.Touched);
    }

    [Fact]
    public async Task FR359_a_redelivered_job_in_pending_approval_re_defers_without_calling_the_model_or_saving()
    {
        Create(new FakeToolCall("wipe", "{}"));
        await DeferredAsync();
        var turns = Turns.Count;
        var saves = Harness.Store.Saves;

        var again = await DeferredAsync();
        await DeferredAsync();

        Assert.Equal(turns, Turns.Count);
        Assert.Equal(saves, Harness.Store.Saves);
        Assert.Equal(ToolApprovalRules.WakeAt(_clock.GetUtcNow(), Gate().CreatedAt), again.RunAfter);
        Assert.Empty(_log.Ran);
        Assert.Single(Harness.Store.Gates);
    }

    [Fact]
    public async Task FR359_approving_runs_the_tool_exactly_once_and_resumes_from_the_saved_messages_without_repeating_earlier_turns()
    {
        Create(new FakeToolCall("scan", "{}"), new FakeToolCall("wipe", "{}"));
        await DeferredAsync();
        Decide(Gate(), GateStatus.Approved, Guid.CreateVersion7());

        var result = await DeliverAsync();

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Equal(["scan", "wipe"], _log.Ran);

        // Two turns before the gate, one after: the resumed turn got both earlier exchanges and the tool's result.
        Assert.Equal(3, Turns.Count);
        Assert.Equal(2, Turns[^1].Exchanges.Count);
        Assert.Equal(("ran wipe", false), (LastResultSent().Content, LastResultSent().IsError));
        var saved = Saved();
        Assert.Equal((AgentRunStatus.Succeeded, (string?)null), (saved.Status, saved.ToolLoopState));
        Assert.Equal([("scan", "read", false), ("wipe", "destructive", false)], Calls(saved.ToolCalls));
        Assert.Contains("\"ranAt\"", Gate().Payload, StringComparison.Ordinal);

        // A redelivery after the stage stored its output runs nothing again.
        var redelivered = await DeliverAsync();
        Assert.True(redelivered.Last.Redelivered);
        Assert.Equal(["scan", "wipe"], _log.Ran);
        Assert.Equal(3, Turns.Count);
    }

    [Fact]
    public async Task FR359_the_claim_is_committed_before_the_tool_runs()
    {
        Create(new FakeToolCall("wipe", "{}"));
        await DeferredAsync();
        Decide(Gate(), GateStatus.Approved, Guid.CreateVersion7());
        var savesBefore = Harness.Store.Saves;
        string? stateWhenRun = null;
        var savesWhenRun = 0;
        _log.OnRun = _ =>
        {
            stateWhenRun = Saved().ToolLoopState;
            savesWhenRun = Harness.Store.Saves;
        };

        await DeliverAsync();

        Assert.True(savesWhenRun > savesBefore);
        Assert.Equal(ToolApprovalPhase.Approved, ToolLoopStates.Read(stateWhenRun)!.Phase);
        Assert.Contains("\"ranAt\"", Gate().Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR359_rejecting_sends_denied_by_the_user_with_the_comment_and_the_tool_never_runs()
    {
        Create(new FakeToolCall("scan", "{}"), new FakeToolCall("wipe", "{}"));
        await DeferredAsync();
        var owner = Guid.CreateVersion7();
        Decide(Gate(), GateStatus.Rejected, owner, "not during the release");

        var result = await DeliverAsync();

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Equal(["scan"], _log.Ran);
        Assert.Equal(($"denied by {owner}: not during the release", true), (LastResultSent().Content, LastResultSent().IsError));
        Assert.Equal([("scan", "read", false), ("wipe", "destructive", true)], Calls(Saved().ToolCalls));
        Assert.Null(Saved().ToolLoopState);
    }

    [Fact]
    public async Task FR359_no_decision_within_24_hours_counts_as_reject_by_the_gate_timer()
    {
        Create(new FakeToolCall("wipe", "{}"));
        await DeferredAsync();

        _clock.Advance(TimeSpan.FromHours(23));
        await DeferredAsync();
        Assert.Equal(GateStatus.Pending, Gate().Status);

        _clock.Advance(TimeSpan.FromHours(1));
        var result = await DeliverAsync();

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Empty(_log.Ran);
        var gate = Gate();
        Assert.Equal((GateStatus.Rejected, ToolApprovalRules.TimeoutComment, (Guid?)null, (DateTimeOffset?)Start.AddHours(24)), (gate.Status, gate.Comment, gate.ReviewerId, gate.DecidedAt));
        Assert.Equal(("denied by system: no decision within 24 hours", true), (LastResultSent().Content, LastResultSent().IsError));
    }

    [Theory]
    [InlineData(ApprovalMode.Autonomous)]
    [InlineData(ApprovalMode.Advisory)]
    [InlineData(ApprovalMode.Strict)]
    public async Task FR359_every_approval_mode_raises_the_gate_and_the_tool_does_not_run(ApprovalMode mode)
    {
        Create(new FakeToolCall("wipe", "{}"));
        Harness.Run.ApprovalMode = mode;

        await DeferredAsync();
        await DeferredAsync();

        Assert.Equal(GateStatus.Pending, Gate().Status);
        Assert.Empty(_log.Ran);
    }

    [Fact]
    public async Task FR359_a_second_destructive_call_later_in_the_loop_gets_its_own_gate()
    {
        Create(new FakeToolCall("wipe", """{"n":1}"""), new FakeToolCall("wipe", """{"n":2}"""));
        await DeferredAsync();
        var first = Gate();
        Decide(first, GateStatus.Approved, Guid.CreateVersion7());

        await DeferredAsync();

        Assert.Equal(["wipe"], _log.Ran);
        Assert.Equal(2, Harness.Store.Gates.Count);
        var second = Harness.Store.Gates.Single(g => g.Id != first.Id);
        Assert.Equal(GateStatus.Pending, second.Status);
        using (var payload = JsonDocument.Parse(second.Payload!))
        {
            Assert.Equal("""{"n":2}""", payload.RootElement.GetProperty("arguments").GetString());
        }

        Assert.Equal(second.Id, ToolLoopStates.Read(Saved().ToolLoopState)!.GateId);

        Decide(second, GateStatus.Approved, Guid.CreateVersion7());
        var result = await DeliverAsync();

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Equal(["wipe", "wipe"], _log.Ran);
        Assert.Equal(3, Turns.Count);
        Assert.Equal([("wipe", "destructive", false), ("wipe", "destructive", false)], Calls(Saved().ToolCalls));
    }

    [Fact]
    public async Task FR359_a_call_claimed_by_a_delivery_that_stopped_is_never_run_again_and_its_outcome_is_unknown()
    {
        Create(new FakeToolCall("wipe", "{}"));
        await DeferredAsync();
        Decide(Gate(), GateStatus.Approved, Guid.CreateVersion7());

        // As if the earlier delivery committed the claim, then crashed before resuming.
        var saved = Saved();
        saved.ToolLoopState = ToolLoopStates.Serialize(ToolLoopStates.Read(saved.ToolLoopState)! with { Phase = ToolApprovalPhase.Approved });

        var result = await DeliverAsync();

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Empty(_log.Ran);
        Assert.Equal((ToolApprovalRules.OutcomeUnknown, true), (LastResultSent().Content, LastResultSent().IsError));
    }

    [Fact]
    public async Task FR359_a_cancelled_gate_closes_the_call_and_the_tool_never_runs()
    {
        Create(new FakeToolCall("wipe", "{}"));
        await DeferredAsync();
        Decide(Gate(), GateStatus.Cancelled);

        var failed = await Assert.ThrowsAsync<JobFailedException>(() => DeliverAsync());

        Assert.Equal((EngineErrorCodes.RunNotActive, false), (failed.ErrorCode, failed.Transient));
        Assert.Empty(_log.Ran);
        Assert.Equal((AgentRunStatus.Cancelled, (string?)null), (Saved().Status, Saved().ToolLoopState));
    }

    [Fact]
    public async Task FR359_a_saved_state_that_cannot_be_read_is_never_run()
    {
        Create(new FakeToolCall("wipe", "{}"));
        await DeferredAsync();
        Decide(Gate(), GateStatus.Approved, Guid.CreateVersion7());
        Saved().ToolLoopState = """{"version":99}""";

        await Assert.ThrowsAsync<JobFailedException>(() => DeliverAsync());

        Assert.Empty(_log.Ran);
        Assert.Equal(AgentRunStatus.Failed, Saved().Status);
    }

    [Fact]
    public async Task FR359_a_loop_over_the_1_MB_cap_fails_with_TOOL_STATE_TOO_LARGE_and_the_tool_never_runs()
    {
        var huge = $$"""{"q":"{{new string('a', ToolApprovalRules.MaxStateBytes + 10)}}"}""";
        Create(new FakeToolCall("scan", huge), new FakeToolCall("wipe", "{}"));

        var failed = await Assert.ThrowsAsync<JobFailedException>(() => DeliverAsync());

        Assert.Equal((EngineErrorCodes.ToolStateTooLarge, false), (failed.ErrorCode, failed.Transient));
        Assert.Equal(["scan"], _log.Ran);
        Assert.Empty(Harness.Store.Gates);
        var saved = Saved();
        Assert.Equal(AgentRunStatus.Failed, saved.Status);
        Assert.Null(saved.ToolLoopState);
        Assert.StartsWith(EngineErrorCodes.ToolStateTooLarge, saved.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR359_the_saved_state_and_the_gate_payload_are_redacted()
    {
        Create(
            new FakeToolCall("scan", $$"""{"token":"{{ApiKey}}"}"""),
            new FakeToolCall("wipe", $$"""{"target":"db-1","api_key":"hunter2-secret","reason":"key {{ApiKey}}"}"""));

        await DeferredAsync();

        var state = Saved().ToolLoopState!;
        var payload = Gate().Payload!;
        foreach (var stored in new[] { state, payload })
        {
            Assert.DoesNotContain(ApiKey, stored, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2-secret", stored, StringComparison.Ordinal);
            Assert.Contains(SecretRedaction.Mask, stored, StringComparison.Ordinal);
        }

        Assert.True(ToolApprovalRules.FitsCap(state));
    }

    [Fact]
    public async Task FR359_the_approved_tool_runs_with_the_redacted_arguments_the_approver_saw()
    {
        Create(new FakeToolCall("wipe", """{"target":"db-1","api_key":"hunter2-secret"}"""));
        await DeferredAsync();
        string shown;
        using (var payload = JsonDocument.Parse(Gate().Payload!))
        {
            shown = payload.RootElement.GetProperty("arguments").GetString()!;
        }

        Decide(Gate(), GateStatus.Approved, Guid.CreateVersion7());
        await DeliverAsync();

        Assert.Equal([shown], _log.Arguments);
        Assert.DoesNotContain("hunter2-secret", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR359_a_call_waits_while_the_run_has_another_pending_tool_approval_and_raises_its_own_after()
    {
        Create(new FakeToolCall("wipe", "{}"));
        await DeferredAsync();
        var first = Gate();
        var other = Guid.CreateVersion7();

        var waiting = await DeferredAsync(other);

        Assert.Single(Harness.Store.Gates);
        Assert.Equal(_clock.GetUtcNow() + AgentStageRunner.GateQueuedRetry, waiting.RunAfter);
        Assert.Null(ToolLoopStates.Read(Saved(other).ToolLoopState)!.GateId);

        Decide(first, GateStatus.Rejected, Guid.CreateVersion7(), "no");
        await DeferredAsync(other);

        var second = Harness.Store.Gates.Single(g => g.Id != first.Id);
        Assert.Equal(GateStatus.Pending, second.Status);
        Assert.Equal(other, ToolLoopStates.AgentRunOf(second.Payload));
        Assert.Empty(_log.Ran);
    }

    [Theory]
    [InlineData("wrong_run")]
    [InlineData("wrong_type")]
    public async Task FR359_a_gate_that_is_not_this_calls_tool_approval_closes_the_call_and_the_tool_never_runs(string corruption)
    {
        Create(new FakeToolCall("wipe", "{}"));
        await DeferredAsync();
        var gate = Gate();
        Decide(gate, GateStatus.Approved, Guid.CreateVersion7());
        if (corruption == "wrong_run")
        {
            gate.Payload = gate.Payload!.Replace(_agentRunId.ToString(), Guid.CreateVersion7().ToString(), StringComparison.Ordinal);
        }
        else
        {
            gate.Type = EngineGates.Escalation;
        }

        var failed = await Assert.ThrowsAsync<JobFailedException>(() => DeliverAsync());

        Assert.Equal(EngineErrorCodes.RunNotActive, failed.ErrorCode);
        Assert.Empty(_log.Ran);
        Assert.Equal((AgentRunStatus.Cancelled, (string?)null), (Saved().Status, Saved().ToolLoopState));
    }

    [Fact]
    public async Task FR359_a_claim_already_on_the_gate_means_the_call_is_never_run_again_whatever_the_saved_phase()
    {
        Create(new FakeToolCall("wipe", "{}"));
        await DeferredAsync();
        var gate = Gate();
        Decide(gate, GateStatus.Approved, Guid.CreateVersion7());
        gate.Payload = ToolLoopStates.WithRanAt(gate.Payload, Start);

        var result = await DeliverAsync();

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Empty(_log.Ran);
        Assert.Equal((ToolApprovalRules.OutcomeUnknown, true), (LastResultSent().Content, LastResultSent().IsError));
    }

    [Theory]
    [InlineData("peek")]
    [InlineData("wipe")]
    public async Task FR359_calls_after_the_destructive_one_in_the_same_message_are_not_run_on_resume(string second)
    {
        CreateOneMessage(null, new LlmToolCall("c1", "wipe", """{"n":1}"""), new LlmToolCall("c2", second, """{"api_key":"hunter2-secret"}"""));
        await DeferredAsync();
        Decide(Gate(), GateStatus.Approved, Guid.CreateVersion7());

        var result = await DeliverAsync();

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Equal(["wipe"], _log.Ran);
        Assert.Single(Harness.Store.Gates);
        Assert.Equal(2, Turns.Count);
        var sent = Assert.Single(Turns[^1].Exchanges).Results;
        Assert.Equal([("c1", "ran wipe", false), ("c2", ToolApprovalRules.NotRunAfterPause, true)], sent.Select(r => (r.CallId, r.Content, r.IsError)));
    }

    [Theory]
    [InlineData(GateStatus.Pending)]
    [InlineData(GateStatus.Approved)]
    public async Task FR359_a_permanent_failure_before_resuming_closes_the_call_so_the_stage_can_be_retried(GateStatus decision)
    {
        Create(new FakeToolCall("wipe", "{}"));
        await DeferredAsync();
        if (decision == GateStatus.Approved)
        {
            Decide(Gate(), GateStatus.Approved, Guid.CreateVersion7());
        }
        else
        {
            // Pending past its timer: the timeout's reject comes after the key check, so the gate is still pending here.
            _clock.Advance(ToolApprovalRules.GateTimeout);
        }

        Resolver.Key = null;
        var failed = await Assert.ThrowsAsync<JobFailedException>(() => DeliverAsync());

        Assert.Equal((EngineErrorCodes.ProviderNotConfigured, false), (failed.ErrorCode, failed.Transient));
        Assert.Empty(_log.Ran);
        Assert.Equal(decision == GateStatus.Pending ? GateStatus.Cancelled : GateStatus.Approved, Gate().Status);
        Assert.DoesNotContain("ranAt", Gate().Payload, StringComparison.Ordinal);
        Assert.Equal((AgentRunStatus.Failed, (string?)null), (Saved().Status, Saved().ToolLoopState));
    }

    [Fact]
    public async Task FR359_an_approved_call_the_session_refuses_is_never_claimed_and_its_gate_never_says_it_ran()
    {
        var session = new ConfigurableToolSession { RefuseFromCall = 1 };
        Create(new ToolOptionsExtension { Session = session }, new FakeToolCall("wipe", "{}"));
        await DeferredAsync();
        Assert.Equal(0, session.Started);
        Decide(Gate(), GateStatus.Approved, Guid.CreateVersion7());

        var result = await DeliverAsync();

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Empty(_log.Ran);
        Assert.DoesNotContain("ranAt", Gate().Payload, StringComparison.Ordinal);
        Assert.Equal((session.RefusalText, true), (LastResultSent().Content, LastResultSent().IsError));
    }

    [Fact]
    public async Task FR359_at_the_session_limit_a_destructive_call_raises_no_gate()
    {
        var session = new ConfigurableToolSession { RefuseFromCall = 1 };
        CreateOneMessage(new ToolOptionsExtension { Session = session }, new LlmToolCall("c1", "scan", "{}"), new LlmToolCall("c2", "wipe", "{}"));

        var result = await DeliverAsync();

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Empty(Harness.Store.Gates);
        Assert.Empty(_log.Ran);
        Assert.Equal([("scan", "read", true), ("wipe", "destructive", true)], Calls(Saved().ToolCalls));
    }
}
