using System.Collections.Concurrent;
using System.Diagnostics;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime;
using Tevrin.Engine.Runtime.Agents;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>§8 observability: the engine's stage-execution and agent-stage spans, and the validation-retry log's count.</summary>
public sealed class EngineTelemetryTests : IDisposable
{
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ActivityListener _listener;

    public EngineTelemetryTests()
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

    private Activity Stopped(Guid runId) =>
        Assert.Single(_stopped, a => Equals(a.GetTagItem(EngineTelemetry.PipelineRunIdTag), runId.ToString()));

    [Fact]
    public void Section8_a_closed_stage_execution_is_one_span_with_its_real_times_and_tags()
    {
        var entered = new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
        var execution = new StageExecution
        {
            PipelineRunId = Guid.NewGuid(),
            StageId = "check",
            Iteration = 2,
            Checkpoint = 4,
            EnteredAt = entered,
            ExitedAt = entered.AddMinutes(90),
            Outcome = "loop",
        };

        EngineTelemetry.TraceStageExecution(execution);

        var span = Stopped(execution.PipelineRunId);
        Assert.Equal("stage check", span.DisplayName);
        Assert.Equal(("check", 2, "loop"), (span.GetTagItem(EngineTelemetry.StageTag), span.GetTagItem(EngineTelemetry.IterationTag), span.GetTagItem(EngineTelemetry.OutcomeTag)));
        Assert.Equal(entered.UtcDateTime, span.StartTimeUtc);
        Assert.Equal(TimeSpan.FromMinutes(90), span.Duration);
    }

    [Fact]
    public void Section8_an_open_stage_execution_emits_no_span_yet()
    {
        var runId = Guid.NewGuid();

        EngineTelemetry.TraceStageExecution(new StageExecution { PipelineRunId = runId, StageId = "draft", Iteration = 1, EnteredAt = DateTimeOffset.UtcNow });

        Assert.DoesNotContain(_stopped, a => Equals(a.GetTagItem(EngineTelemetry.PipelineRunIdTag), runId.ToString()));
    }

    [Fact]
    public void Section8_an_agent_stage_span_carries_the_run_stage_agent_and_attempt()
    {
        var runId = Guid.NewGuid();

        using (EngineTelemetry.StartAgentStage(runId, "draft", "drafter", 2))
        {
        }

        var span = Stopped(runId);
        Assert.Equal(("draft", "drafter", 2), (span.GetTagItem(EngineTelemetry.StageTag), span.GetTagItem(EngineTelemetry.AgentTag), span.GetTagItem(EngineTelemetry.AttemptTag)));
    }

    [Fact]
    public void Section8_the_validation_retry_log_counts_findings_one_per_line() =>
        Assert.Equal(
            (2, 1, 0),
            (AgentStageCoordinator.FindingCount("The report has no verdict.\n/verdict: required"),
             AgentStageCoordinator.FindingCount("/: not JSON"),
             AgentStageCoordinator.FindingCount(string.Empty)));
}
