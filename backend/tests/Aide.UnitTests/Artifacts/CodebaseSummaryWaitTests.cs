using Aide.Domain.Agents;
using Aide.Domain.Artifacts;

namespace Aide.UnitTests.Artifacts;

/// <summary>FR-70: refinement waits up to 10 minutes for the codebase summary, and the summary's state for the UI.</summary>
public class CodebaseSummaryWaitTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AnalysisJobState.Queued)]
    [InlineData(AnalysisJobState.Running)]
    public void FR70_Refinement_waits_while_the_analyst_is_queued_or_running(AnalysisJobState state)
    {
        var decision = CodebaseSummaryWait.Decide(hasExistingCode: true, summaryExists: false, state, Start, Start.AddMinutes(2));

        Assert.True(decision.Wait);
        Assert.Equal(Start.AddMinutes(2) + CodebaseSummaryWait.PollInterval, decision.RetryAt);
        Assert.Null(decision.Warning);
    }

    [Fact]
    public void FR70_The_wait_never_polls_past_the_ten_minute_deadline()
    {
        var decision = CodebaseSummaryWait.Decide(true, false, AnalysisJobState.Running, Start, Start.AddMinutes(10).AddSeconds(-5));

        Assert.True(decision.Wait);
        Assert.Equal(Start.AddMinutes(10), decision.RetryAt);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(45)]
    public void FR70_After_ten_minutes_refinement_continues_without_the_summary_and_records_the_timeout(int minutes)
    {
        var decision = CodebaseSummaryWait.Decide(true, false, AnalysisJobState.Queued, Start, Start.AddMinutes(minutes));

        Assert.False(decision.Wait);
        Assert.Equal(AgentRunWarnings.SummaryWaitTimeout, decision.Warning);
    }

    [Theory]
    [InlineData(AnalysisJobState.Failed)]
    [InlineData(AnalysisJobState.Succeeded)]
    [InlineData(AnalysisJobState.None)]
    public void FR70_An_analyst_that_ended_without_a_summary_is_not_waited_for(AnalysisJobState state)
    {
        var decision = CodebaseSummaryWait.Decide(true, false, state, Start, Start.AddMinutes(1));

        Assert.False(decision.Wait);
        Assert.Equal(AgentRunWarnings.SummaryUnavailable, decision.Warning);
    }

    [Fact]
    public void FR70_No_wait_without_existing_code_or_once_the_summary_exists()
    {
        Assert.Equal(SummaryWaitDecision.Proceed(), CodebaseSummaryWait.Decide(false, false, AnalysisJobState.None, Start, Start));
        Assert.Equal(SummaryWaitDecision.Proceed(), CodebaseSummaryWait.Decide(true, true, AnalysisJobState.Running, Start, Start));
    }

    [Theory]
    [InlineData(false, false, AnalysisJobState.None, 1, CodebaseSummaryState.NotNeeded)]
    [InlineData(true, false, AnalysisJobState.Queued, 1, CodebaseSummaryState.Queued)]
    [InlineData(true, false, AnalysisJobState.Running, 1, CodebaseSummaryState.Running)]
    [InlineData(true, true, AnalysisJobState.Succeeded, 1, CodebaseSummaryState.Completed)]
    [InlineData(true, true, AnalysisJobState.Running, 30, CodebaseSummaryState.Completed)]
    [InlineData(true, false, AnalysisJobState.Failed, 1, CodebaseSummaryState.Failed)]
    [InlineData(true, false, AnalysisJobState.Succeeded, 1, CodebaseSummaryState.Failed)]
    [InlineData(true, false, AnalysisJobState.Queued, 11, CodebaseSummaryState.TimedOut)]
    [InlineData(true, false, AnalysisJobState.Running, 11, CodebaseSummaryState.TimedOut)]
    public void FR70_The_summary_state_shown_on_the_refinement_page(bool hasCode, bool summaryExists, AnalysisJobState analysis, int minutes, CodebaseSummaryState expected) =>
        Assert.Equal(expected, CodebaseSummaryWait.State(hasCode, summaryExists, analysis, Start, Start.AddMinutes(minutes)));
}
