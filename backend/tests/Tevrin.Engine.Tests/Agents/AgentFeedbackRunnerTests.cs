using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// FR-351, §4.75: an agent stage with <c>feedback: { inject: true }</c> gets its agent's entries (this pipeline and project,
/// not deleted, newest first, up to the limit) and its agent run records which; a stage without it records none.
/// </summary>
public class AgentFeedbackRunnerTests
{
    private const string DraftAgent = "    agent: { key: drafter, prompt: fixture.draft, tools: [fixture.lookup] }\n";

    private static string Injecting(string yaml)
    {
        Assert.Contains(DraftAgent, yaml, StringComparison.Ordinal);
        return yaml.Replace(DraftAgent, DraftAgent + "    feedback: { inject: true, limit: 2 }\n", StringComparison.Ordinal);
    }

    private static AgentFeedback Entry(AgentStageHarness harness, int minutes, string text, Guid? projectId = null, bool deleted = false, string agentKey = "drafter") => new()
    {
        PipelineId = "fixture-review",
        AgentKey = agentKey,
        ProjectId = projectId ?? harness.Run.ProjectId,
        Source = AgentFeedbackSource.Human,
        Text = text,
        CreatedAt = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero).AddMinutes(minutes),
        DeletedAt = deleted ? DateTimeOffset.UnixEpoch : null,
    };

    [Fact]
    public async Task FR351_an_injecting_stage_records_the_newest_entries_of_its_agent_up_to_the_limit()
    {
        using var harness = new AgentStageHarness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)], Injecting);
        var oldest = Entry(harness, 0, "Oldest.");
        var middle = Entry(harness, 1, "Middle.");
        var newest = Entry(harness, 2, "Newest.");
        harness.Store.Feedback.AddRange(
        [
            oldest, middle, newest,
            Entry(harness, 3, "Deleted.", deleted: true),
            Entry(harness, 4, "Another project.", projectId: Guid.CreateVersion7()),
            Entry(harness, 5, "Another agent.", agentKey: "checker"),
        ]);

        await harness.RunStageAsync("draft");

        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal([newest.Id, middle.Id], run.FeedbackIds);
    }

    [Fact]
    public async Task FR351_an_injecting_stage_with_no_entries_records_an_empty_list()
    {
        using var harness = new AgentStageHarness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)], Injecting);

        await harness.RunStageAsync("draft");

        Assert.Equal([], Assert.Single(harness.Store.AgentRuns).FeedbackIds!);
    }

    [Fact]
    public async Task FR351_a_stage_without_feedback_injects_nothing_and_records_null()
    {
        using var harness = new AgentStageHarness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)]);
        harness.Store.Feedback.Add(Entry(harness, 0, "Not used."));

        await harness.RunStageAsync("draft");

        Assert.Null(Assert.Single(harness.Store.AgentRuns).FeedbackIds);
    }
}
