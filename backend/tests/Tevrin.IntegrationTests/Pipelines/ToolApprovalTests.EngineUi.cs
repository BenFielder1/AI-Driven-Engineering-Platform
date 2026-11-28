using Tevrin.Engine.Api;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>FR-364 (M72): a pending <c>tool_approval</c> gate shows what will run to everyone who can see it.</summary>
public sealed partial class ToolApprovalTests
{
    [Fact]
    public async Task FR364_a_tool_approval_gate_shows_the_tool_tier_redacted_arguments_and_reason_and_only_an_owner_can_decide()
    {
        var turns = new FakeTurnLog();
        await using var app = App(factory, turns);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var (run, gate) = await PendingAsync(app, fixture);
        var executor = await ExecutorRunAsync(app, run.Id);

        foreach (var session in new[] { fixture.Owner, fixture.Contributor, fixture.Viewer })
        {
            var listed = Assert.Single(await ProjectApi.ReadAsync<List<PipelineGateDto>>(await session.Client.GetAsync($"/api/pipeline-runs/{run.Id}/gates", Ct)));
            var payload = listed.Payload;
            Assert.NotNull(payload);
            Assert.Equal(
                (PipelineGatePayloadDto.ToolCallKind, FlowFixturePack.RestartPodTool, "destructive", RestartArguments, "api-0 is out of memory", (Guid?)executor.Id),
                (payload.Kind, payload.Tool, payload.Tier, payload.Arguments, payload.Reason, payload.AgentRunId));
            Assert.Null(payload.Variants);
            Assert.Null(payload.Payload);
        }

        // The inbox shows the card to a Contributor, without the approve control; an Owner can decide.
        var inbox = new Uri("/api/pipeline-gates?gateType=tool_approval&limit=200", UriKind.Relative);
        var contributor = Assert.Single((await ProjectApi.ReadAsync<PipelineGateInboxPageDto>(await fixture.Contributor.Client.GetAsync(inbox, Ct))).Items, i => i.Gate.Id == gate.Id);
        Assert.False(contributor.CanDecide);
        Assert.Equal(FlowFixturePack.RestartPodTool, contributor.Gate.Payload!.Tool);
        var owner = Assert.Single((await ProjectApi.ReadAsync<PipelineGateInboxPageDto>(await fixture.Owner.Client.GetAsync(inbox, Ct))).Items, i => i.Gate.Id == gate.Id);
        Assert.True(owner.CanDecide);
        Assert.Equal(RestartArguments, owner.Gate.Payload!.Arguments);
    }
}
