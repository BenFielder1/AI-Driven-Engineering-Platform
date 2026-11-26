using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// FR-359, §4.73: the tool loop stops at a destructive call in the middle of a turn and resumes after it: the calls before
/// it ran once, the decided result takes its place, the calls after it run on resume, and the model is called again only
/// for the next turn.
/// </summary>
public class ToolLoopResumeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers turn 1 with three calls (scan, wipe, scan), then a final answer; records every turn request.</summary>
    private sealed class ThreeCallProvider : ILlmProvider, IToolCallingLlmProvider
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
                ? new LlmToolTurn("Checking, then restarting.", [new("c1", "scan", "{}"), new("c2", "wipe", "{}"), new("c3", "scan", "{}")], 10, 5, "tool_use")
                : new LlmToolTurn("done", [], 10, 5, "end_turn"));
        }
    }

    private static (AgentToolExecutor Executor, ToolRunLog Log) Executor()
    {
        var log = new ToolRunLog();
        StageTool[] listed =
        [
            new(TierToolsPack.Scan, new ScanTool(log), ToolTier.Read, false),
            new(TierToolsPack.Wipe, new WipeTool(log), ToolTier.Destructive, false),
        ];
        return (AgentToolExecutor.ForStage(listed, ToolTier.Destructive), log);
    }

    private static LlmRequest Request() => new()
    {
        Model = "fake",
        System = "system",
        Messages = [new LlmMessage(LlmRole.User, "go")],
        Agent = "drafter",
    };

    [Fact]
    public async Task FR359_the_loop_stops_at_the_destructive_call_with_the_results_before_it()
    {
        var provider = new ThreeCallProvider();
        var (executor, log) = Executor();

        var stopped = await Assert.ThrowsAsync<LlmToolLoopSuspendedException>(() => LlmToolLoop.RunAsync(provider, Request(), executor, Ct));

        var snapshot = stopped.Snapshot!;
        Assert.Equal(["scan"], log.Ran);
        Assert.Equal((1, 0, "c2"), (snapshot.Turn, snapshot.Exchanges.Count, snapshot.Waiting.Id));
        Assert.Equal(["c1"], snapshot.Current.Results.Select(r => r.CallId));
        Assert.Equal(new LlmTurnTokens(10, 5), stopped.Tokens);
    }

    [Fact]
    public async Task FR359_resuming_runs_the_rest_of_the_turn_then_calls_the_model_once_more()
    {
        var provider = new ThreeCallProvider();
        var (executor, log) = Executor();
        var stopped = await Assert.ThrowsAsync<LlmToolLoopSuspendedException>(() => LlmToolLoop.RunAsync(provider, Request(), executor, Ct));
        var snapshot = stopped.Snapshot!;
        var (resumedExecutor, resumedLog) = Executor();
        var decided = resumedExecutor.RecordUnrun(snapshot.Waiting, "denied by ben: no");

        var result = await LlmToolLoop.ResumeAsync(
            provider,
            Request(),
            resumedExecutor,
            new LlmToolLoopResume(snapshot.Exchanges, snapshot.Current with { Results = [.. snapshot.Current.Results, decided] }, snapshot.Turn),
            Ct);

        Assert.Equal("done", result.Completion.Text);
        Assert.Equal((2, 2), (provider.Turns.Count, result.Turns));
        Assert.Equal(["scan"], log.Ran);
        Assert.Equal(["scan"], resumedLog.Ran);
        var sent = Assert.Single(provider.Turns[^1].Exchanges);
        Assert.Equal(["c1", "c2", "c3"], sent.Results.Select(r => r.CallId));
        Assert.Equal("denied by ben: no", sent.Results[1].Content);
    }
}
