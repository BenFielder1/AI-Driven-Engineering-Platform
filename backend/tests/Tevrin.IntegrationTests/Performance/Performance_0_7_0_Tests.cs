using System.Diagnostics;
using Tevrin.Engine.Api;
using Tevrin.IntegrationTests.Pipelines;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Performance;

/// <summary>
/// §8 0.7.0 Performance: a timeline page of a run with 5,000 entries. The 300 ms target is measured and logged; the test
/// gates at 1,000 ms so a slow CI runner doesn't flake it (D-0.7.0-37).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class Performance_0_7_0_Tests(TevrinApiFactory factory)
{
    private const int Entries = 5_000;
    private const int Samples = 10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task S8_a_timeline_page_of_a_run_with_5000_entries_answers_in_under_1000ms()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var runId = await PipelineApiGateTests.RunIdAsync(factory, fixture.Project.Id);
        var start = DateTimeOffset.UtcNow.AddDays(1);
        start = start.AddTicks(-(start.Ticks % TimeSpan.TicksPerMicrosecond));
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            for (var i = 0; i < Entries; i++)
            {
                db.StageExecutions.Add(new StageExecution
                {
                    PipelineRunId = runId,
                    StageId = "refinement",
                    Iteration = i + 1,
                    Checkpoint = 1,
                    EnteredAt = start.AddSeconds(i),
                    ExitedAt = start.AddSeconds(i),
                });
            }

            await db.SaveChangesAsync(Ct);
        });
        var url = new Uri($"/api/pipeline-runs/{runId}/timeline?limit=50", UriKind.Relative);

        // Warm-up, then the slowest of the samples.
        await ProjectApi.ReadAsync<PipelineTimelinePageDto>(await fixture.Viewer.Client.GetAsync(url, Ct));
        var slowest = TimeSpan.Zero;
        for (var i = 0; i < Samples; i++)
        {
            var clock = Stopwatch.StartNew();
            var page = await ProjectApi.ReadAsync<PipelineTimelinePageDto>(await fixture.Viewer.Client.GetAsync(url, Ct));
            clock.Stop();
            Assert.Equal(50, page.Items.Count);
            slowest = clock.Elapsed > slowest ? clock.Elapsed : slowest;
        }

        TestContext.Current.SendDiagnosticMessage($"Timeline page (5,000 entries): slowest of {Samples} = {slowest.TotalMilliseconds:F0} ms (target 300 ms)");
        Assert.True(slowest < TimeSpan.FromMilliseconds(1_000), $"The timeline page took {slowest.TotalMilliseconds:F0} ms.");
    }
}
