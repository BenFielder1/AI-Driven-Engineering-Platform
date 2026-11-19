using System.Diagnostics;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>§8 performance: <c>PipelineStateMachine.Apply</c> is pure and allocation-light (&lt; 1 ms per call).</summary>
public class StateMachinePerformanceTests
{
    private const int Calls = 10_000;

    [Fact]
    public void Section8_Apply_averages_under_1_ms_over_10000_calls()
    {
        var definition = Fixtures.Load("fixture-review.pipeline.yaml");
        var state = new RunState("draft", "drafting", 3, new Dictionary<string, int>(StringComparer.Ordinal));
        var facts = new RunFacts
        {
            Mode = ApprovalMode.Advisory,
            LockedArtifacts = ["draft"],
            Guards = new Dictionary<string, GuardResult>(StringComparer.Ordinal) { [FixturePack.ReportConsistentGuard] = GuardResult.Pass },
            Settings = new Dictionary<string, int>(StringComparer.Ordinal) { [FixturePack.RedraftCycles] = 2 },
        };
        var move = Transition.Advance();

        // Warm-up (JIT, first-call caches), then the measured calls.
        for (var i = 0; i < 1_000; i++)
        {
            Assert.True(PipelineStateMachine.Apply(state, move, facts, definition).IsSuccess);
        }

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < Calls; i++)
        {
            _ = PipelineStateMachine.Apply(state, move, facts, definition);
        }

        clock.Stop();
        var averageMs = clock.Elapsed.TotalMilliseconds / Calls;
        TestContext.Current.SendDiagnosticMessage($"PipelineStateMachine.Apply: {averageMs:F4} ms per call over {Calls} calls");
        Assert.True(averageMs < 1.0, $"Apply averaged {averageMs:F4} ms per call (budget 1 ms).");
    }
}
