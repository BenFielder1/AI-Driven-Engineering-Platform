using System.Collections.Concurrent;
using System.Diagnostics;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Tests.Agents;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>§8: stage spans are emitted only for committed closes, as root spans linked to the closer; agent-stage spans
/// record any failure by type.</summary>
public sealed class EngineSpanCommitTests : IDisposable
{
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ActivityListener _listener;

    public EngineSpanCommitTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EngineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    private static StageExecution Closed() => new()
    {
        PipelineRunId = Guid.NewGuid(),
        StageId = "check",
        Iteration = 1,
        EnteredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        ExitedAt = DateTimeOffset.UtcNow,
        Outcome = "advance",
    };

    private List<Activity> SpansOf(Guid runId) =>
        [.. _stopped.Where(a => Equals(a.GetTagItem(EngineTelemetry.PipelineRunIdTag), runId.ToString()))];

    [Fact]
    public void Section8_a_failed_save_emits_no_stage_span()
    {
        var buffer = new StageSpanBuffer();
        var execution = Closed();

        buffer.Collect([execution]);
        buffer.Failed();
        buffer.Saved(inTransaction: false);

        Assert.Empty(SpansOf(execution.PipelineRunId));
    }

    [Fact]
    public void Section8_a_successful_save_emits_the_stage_span()
    {
        var buffer = new StageSpanBuffer();
        var execution = Closed();

        buffer.Collect([execution]);
        buffer.Saved(inTransaction: false);

        Assert.Single(SpansOf(execution.PipelineRunId));
    }

    [Fact]
    public void Section8_inside_a_transaction_the_span_waits_for_the_commit_and_a_rollback_drops_it()
    {
        var buffer = new StageSpanBuffer();
        var committed = Closed();
        var rolledBack = Closed();

        buffer.Collect([committed]);
        buffer.Saved(inTransaction: true);
        Assert.Empty(SpansOf(committed.PipelineRunId));
        buffer.Committed();
        Assert.Single(SpansOf(committed.PipelineRunId));

        buffer.Collect([rolledBack]);
        buffer.Saved(inTransaction: true);
        buffer.ResetTransaction();
        buffer.Committed();
        Assert.Empty(SpansOf(rolledBack.PipelineRunId));
    }

    [Fact]
    public void Section8_the_stage_span_is_a_root_span_linked_to_the_current_activity()
    {
        var execution = Closed();
        using var request = new Activity("request").Start();

        EngineTelemetry.TraceStageExecution(execution);

        var span = Assert.Single(SpansOf(execution.PipelineRunId));
        Assert.Null(span.ParentId);
        Assert.NotEqual(request.TraceId, span.TraceId);
        Assert.Contains(span.Links, l => l.Context.SpanId == request.SpanId);
        Assert.Same(request, Activity.Current);
    }

    [Fact]
    public async Task Section8_an_agent_stage_span_records_any_exception_by_type()
    {
        using var harness = new AgentStageHarness([], contextProviders: [new ThrowingContextProvider()]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Runner.RunAsync(new AgentStageRequest(harness.Run.Id, "draft", "stage_entered"), TestContext.Current.CancellationToken));

        var span = Assert.Single(SpansOf(harness.Run.Id));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(InvalidOperationException), span.GetTagItem(EngineTelemetry.ErrorCodeTag));
        Assert.DoesNotContain("secret", span.StatusDescription ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class ThrowingContextProvider : IAgentContextProvider
    {
        public string PipelineId => "fixture-review";

        public Task<IReadOnlyList<AgentContextSection>> GetContextAsync(AgentContextRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("secret detail");
    }
}
