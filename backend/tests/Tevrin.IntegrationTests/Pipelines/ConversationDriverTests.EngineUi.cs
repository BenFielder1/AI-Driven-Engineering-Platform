using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>FR-363 (M72): a conversation's turns carry their index and role on the timeline and replay (the chat view).</summary>
public sealed partial class ConversationDriverTests
{
    [Fact]
    public async Task FR363_timeline_and_replay_label_each_turn_with_its_index_and_role_and_the_definition_lists_participants()
    {
        var customer = Customer("My router keeps dropping.", "Yes, it works now.");
        var support = Support("Have you restarted it?", "Great. Goodbye!");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), customer, support);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);
        await DriveAsync(app, fixture.Project.Id);

        var timeline = await ProjectApi.ReadAsync<PipelineTimelinePageDto>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{runId}/timeline?limit=200", Ct));
        var turns = timeline.Items.Where(i => i.Kind == PipelineTimelineKind.AgentRun && i.TurnIndex is not null).OrderBy(i => i.TurnIndex).ToList();
        Assert.Equal([1, 2, 3, 4], turns.Select(t => t.TurnIndex!.Value));
        Assert.Equal(["customer", "agent", "customer", "agent"], turns.Select(t => t.TurnRole));

        var replay = await ProjectApi.ReadAsync<PipelineTimelinePageDto>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{runId}/replay?limit=200", Ct));
        var spoken = replay.Items.Where(i => i.Kind == PipelineTimelineKind.AgentRun && i.TurnIndex is not null).OrderBy(i => i.TurnIndex).ToList();
        Assert.Equal(["customer", "agent", "customer", "agent"], spoken.Select(t => t.TurnRole));
        Assert.Contains("Great. Goodbye!", spoken[^1].Output, StringComparison.Ordinal);

        var run = await RunAsync(app, runId);
        var definition = await ProjectApi.ReadAsync<PipelineDefinitionDto>(
            await fixture.Viewer.Client.GetAsync($"/api/pipelines/{FlowFixturePack.CallSim}/definitions/{run.DefinitionHash}", Ct));
        var conversation = definition.Stages.Single(s => s.Id == "call").Conversation;
        Assert.NotNull(conversation);
        Assert.Equal(["customer-sim", "support-sim"], conversation.Participants.Select(p => p.Agent));
        Assert.Equal(["customer", "agent"], conversation.Participants.Select(p => p.Role));
        Assert.Equal(6, conversation.MaxTurns);
    }
}
