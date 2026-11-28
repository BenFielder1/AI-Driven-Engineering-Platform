using System.Text.Json;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// FR-358 runtime (M71b), FR-359 (M71c) and FR-360 (M71d): the runner offers only the listed tools whose tier
/// in the registry now is within the stage's <c>max_tier</c> (default <c>write</c>); a call to any other tool is an error
/// result, logged without its arguments; an offered destructive tool suspends the loop for a <c>tool_approval</c> gate and
/// an external one for an <c>external_action</c> gate, and neither runs from the model's call; every call records its tier.
/// </summary>
public class ToolTierRuntimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Secret = "hunter2-secret-argument";

    private static LlmToolCall Call(string tool, string args = """{"q":"x"}""") => new("call-1", tool, args);

    private static (AgentToolExecutor Executor, ToolRunLog Log, ListLogger Logger) Executor(ToolTier maxTier, params (string Name, ToolTier? Tier, bool External)[] tools)
    {
        var log = new ToolRunLog();
        var logger = new ListLogger();
        var listed = tools.Select(t => new StageTool(t.Name, Tool(t.Name, log), t.Tier, t.External));
        return (AgentToolExecutor.ForStage(listed, maxTier, logger: logger), log, logger);
    }

    private static RecordingTool Tool(string name, ToolRunLog log) => name switch
    {
        TierToolsPack.Scan => new ScanTool(log),
        TierToolsPack.Peek => new PeekTool(log),
        TierToolsPack.Wipe => new WipeTool(log),
        _ => new SendTool(log),
    };

    // ---- the pure rules ----

    [Theory]
    [InlineData(ToolTier.Read, ToolTier.Read, true)]
    [InlineData(ToolTier.Read, ToolTier.Write, false)]
    [InlineData(ToolTier.Write, ToolTier.Write, true)]
    [InlineData(ToolTier.Write, ToolTier.Destructive, false)]
    [InlineData(ToolTier.Destructive, ToolTier.Destructive, true)]
    public void FR358_a_listed_tool_is_offered_only_at_or_below_max_tier(ToolTier maxTier, ToolTier tier, bool offered) =>
        Assert.Equal(offered, ToolTierRules.IsOffered(maxTier, tier));

    [Fact]
    public void FR358_the_default_ceiling_is_write_so_a_destructive_tool_is_not_offered_without_tool_policy()
    {
        var ceiling = ToolTierRules.MaxTierOf(null);

        Assert.Equal(ToolTier.Write, ceiling);
        Assert.True(ToolTierRules.IsOffered(ceiling, ToolTier.Write));
        Assert.False(ToolTierRules.IsOffered(ceiling, ToolTier.Destructive));
    }

    [Fact]
    public void FR358_a_tool_nothing_registers_any_more_is_never_offered() =>
        Assert.False(ToolTierRules.IsOffered(ToolTier.Destructive, null));

    [Theory]
    [InlineData(ToolTier.Read, false, ToolTier.Write, ToolCallVerdict.Run)]
    [InlineData(ToolTier.Write, false, ToolTier.Write, ToolCallVerdict.Run)]
    [InlineData(ToolTier.Write, false, ToolTier.Read, ToolCallVerdict.NotOffered)]
    [InlineData(ToolTier.Destructive, false, ToolTier.Write, ToolCallVerdict.NotOffered)]
    [InlineData(ToolTier.Destructive, false, ToolTier.Destructive, ToolCallVerdict.NeedsApproval)]
    [InlineData(ToolTier.Read, true, ToolTier.Write, ToolCallVerdict.NeedsApproval)]
    [InlineData(ToolTier.Read, true, ToolTier.Read, ToolCallVerdict.NeedsApproval)]
    public void FR358_FR359_FR360_only_offered_read_and_write_tools_that_are_not_external_run(ToolTier tier, bool external, ToolTier maxTier, ToolCallVerdict verdict) =>
        Assert.Equal(verdict, ToolTierRules.VerdictOf(maxTier, tier, external));

    // ---- the executor ----

    [Fact]
    public void FR358_the_offered_set_is_the_listed_tools_within_max_tier()
    {
        var (executor, _, _) = Executor(
            ToolTier.Write,
            (TierToolsPack.Scan, ToolTier.Read, false),
            (TierToolsPack.Peek, ToolTier.Write, false),
            (TierToolsPack.Wipe, ToolTier.Destructive, false),
            (TierToolsPack.Send, null, false));

        Assert.Equal(["scan", "peek"], executor.Definitions.Select(d => d.Name));
    }

    [Fact]
    public async Task FR358_two_listed_names_bound_to_tools_with_one_model_name_keep_the_first_offered_and_do_not_throw()
    {
        var log = new ToolRunLog();
        StageTool[] listed =
        [
            new(TierToolsPack.Scan, new ScanTool(log), ToolTier.Read, false),
            new("tiers.scan-again", new ScanTool(log), ToolTier.Read, false),
        ];

        var executor = AgentToolExecutor.ForStage(listed, ToolTier.Write);
        await executor.ExecuteAsync(Call("scan"), Ct);

        Assert.Equal(["scan"], executor.Definitions.Select(d => d.Name));
        Assert.Equal(["scan"], log.Ran);
        Assert.Equal("read", Assert.Single(executor.Calls).Tier);
    }

    [Fact]
    public async Task FR358_a_call_to_an_unlisted_tool_is_an_error_result_not_an_exception_and_logs_no_arguments()
    {
        var (executor, log, logger) = Executor(ToolTier.Write, (TierToolsPack.Scan, ToolTier.Read, false));

        var result = await executor.ExecuteAsync(Call("ghost", $$"""{"token":"{{Secret}}"}"""), Ct);

        Assert.Equal(("Unknown tool 'ghost'.", true), (result.Content, result.IsError));
        Assert.Empty(log.Ran);
        var call = Assert.Single(executor.Calls);
        Assert.Equal(("ghost", true, (string?)null), (call.Tool, call.IsError, call.Tier));
        var entry = Assert.Single(logger.Entries);
        Assert.Contains("not_listed", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR358_a_call_to_a_listed_tool_above_max_tier_is_refused_and_not_run()
    {
        var (executor, log, logger) = Executor(ToolTier.Read, (TierToolsPack.Peek, ToolTier.Write, false));

        var result = await executor.ExecuteAsync(Call("peek", $$"""{"q":"{{Secret}}"}"""), Ct);

        Assert.Equal((AgentToolExecutor.NotOffered("peek"), true), (result.Content, result.IsError));
        Assert.Empty(log.Ran);
        Assert.Empty(executor.Definitions);
        Assert.Equal("write", Assert.Single(executor.Calls).Tier);
        var entry = Assert.Single(logger.Entries);
        Assert.Contains(TierToolsPack.Peek, entry.Message, StringComparison.Ordinal);
        Assert.Contains("above_max_tier", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR359_a_call_to_an_offered_destructive_tool_suspends_the_loop_and_never_runs()
    {
        var (executor, log, logger) = Executor(ToolTier.Destructive, (TierToolsPack.Wipe, ToolTier.Destructive, false));

        var suspended = await Assert.ThrowsAsync<LlmToolCallSuspendedException>(() => executor.ExecuteAsync(Call("wipe", $$"""{"q":"{{Secret}}"}"""), Ct));

        Assert.Equal(["wipe"], executor.Definitions.Select(d => d.Name));
        Assert.Equal("call-1", suspended.Call!.Id);
        Assert.Empty(log.Ran);
        Assert.Empty(executor.Calls);
        Assert.False(executor.LimitReached);
        var entry = Assert.Single(logger.Entries);
        Assert.Contains("waits on approval", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR359_an_approved_destructive_call_runs_once_through_the_approved_path_and_records_its_tier()
    {
        var (executor, log, _) = Executor(ToolTier.Destructive, (TierToolsPack.Wipe, ToolTier.Destructive, false));

        var result = await executor.ExecuteApprovedAsync(Call("wipe"), Ct);

        Assert.Equal(("ran wipe", false), (result.Content, result.IsError));
        Assert.Equal(["wipe"], log.Ran);
        Assert.Equal(("destructive", false), (Assert.Single(executor.Calls).Tier, executor.Calls[0].IsError));
    }

    [Theory]
    [InlineData(ToolTier.Write, ToolTier.Destructive, false)]
    [InlineData(ToolTier.Destructive, ToolTier.Write, false)]
    [InlineData(ToolTier.Destructive, ToolTier.Destructive, true)]
    public async Task FR359_the_approved_path_refuses_a_tool_that_is_not_an_offered_non_external_destructive_one_now(ToolTier maxTier, ToolTier tier, bool external)
    {
        var (executor, log, _) = Executor(maxTier, (TierToolsPack.Wipe, tier, external));

        var result = await executor.ExecuteApprovedAsync(Call("wipe"), Ct);

        Assert.Equal((AgentToolExecutor.NotOffered("wipe"), true), (result.Content, result.IsError));
        Assert.Empty(log.Ran);
    }

    [Theory]
    [InlineData(ToolTier.Read)]
    [InlineData(ToolTier.Destructive)]
    public async Task FR360_an_external_tool_suspends_for_an_external_action_gate_and_never_runs_whatever_its_tier(ToolTier tier)
    {
        // M71d: M71b refused the call (ApprovalUnavailable); it now waits on its external_action gate.
        var (executor, log, logger) = Executor(ToolTier.Destructive, (TierToolsPack.Send, tier, true));

        var suspended = await Assert.ThrowsAsync<LlmToolCallSuspendedException>(() => executor.ExecuteAsync(Call("send"), Ct));

        Assert.Contains(EngineGates.ExternalAction, suspended.Message, StringComparison.Ordinal);
        Assert.Empty(log.Ran);
        Assert.Empty(executor.Calls);
        Assert.Contains(EngineGates.ExternalAction, Assert.Single(logger.Entries).Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\"q\"", logger.Entries[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR360_the_approved_path_runs_an_external_tool_only_for_an_external_action_gate()
    {
        var (executor, log, _) = Executor(ToolTier.Write, (TierToolsPack.Send, ToolTier.Read, true));

        var refused = executor.RefuseApproved(Call("send"), EngineGates.ToolApproval);
        Assert.Equal((AgentToolExecutor.NotOffered("send"), true), (refused!.Content, refused.IsError));
        Assert.Null(executor.RefuseApproved(Call("send"), EngineGates.ExternalAction));

        var result = await executor.RunApprovedAsync(Call("send"), EngineGates.ExternalAction, Ct);

        Assert.Equal(("ran send", false), (result.Content, result.IsError));
        Assert.Equal(["send"], log.Ran);
    }

    [Fact]
    public async Task FR358_an_offered_read_tool_runs_and_its_tier_is_recorded_in_the_json()
    {
        var (executor, log, logger) = Executor(ToolTier.Write, (TierToolsPack.Scan, ToolTier.Read, false));

        var result = await executor.ExecuteAsync(Call("scan"), Ct);

        Assert.Equal(("ran scan", false), (result.Content, result.IsError));
        Assert.Equal(["scan"], log.Ran);
        Assert.Empty(logger.Entries);
        using var json = JsonDocument.Parse(executor.CallsJson());
        var entry = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(("scan", "read", false), (entry.GetProperty("tool").GetString(), entry.GetProperty("tier").GetString(), entry.GetProperty("isError").GetBoolean()));
    }

    [Fact]
    public async Task FR358_an_executor_without_a_tier_policy_writes_no_tier_so_its_json_is_unchanged()
    {
        var executor = new AgentToolExecutor([new EchoTool()]);

        await executor.ExecuteAsync(Call("lookup"), Ct);

        Assert.DoesNotContain("tier", executor.CallsJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void FR358_a_row_written_before_the_tier_reads_back_with_no_tier()
    {
        var old = JsonSerializer.Deserialize<AgentToolCallRecord[]>("""[{"tool":"lookup","arguments":"{}","isError":false}]""", JsonSerializerOptions.Web);

        Assert.Null(Assert.Single(old!).Tier);
    }
}
