using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>FR-363 (M72): a parallel group's lanes: the definition's branches and each branch execution's label.</summary>
public sealed partial class ParallelDriverTests
{
    [Fact]
    public async Task FR363_the_definition_lists_the_groups_branches_and_the_timeline_labels_each_branch_execution()
    {
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit());
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Parallel);
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);
        var run = await RunAsync(app, runId);

        var definition = await ProjectApi.ReadAsync<PipelineDefinitionDto>(
            await fixture.Viewer.Client.GetAsync($"/api/pipelines/{FlowFixturePack.Parallel}/definitions/{run.DefinitionHash}", Ct));
        var group = definition.Stages.Single(s => s.Id == "analysts");
        Assert.NotNull(group.Branches);
        Assert.Equal(["0", "1"], group.Branches.Select(b => b.Id));
        Assert.Equal(["bull", "bull_review"], group.Branches[0].Stages.Select(s => s.Id));
        Assert.Equal(["bear"], group.Branches[1].Stages.Select(s => s.Id));
        Assert.Null(definition.Stages.Single(s => s.Id == "decide").Branches);

        var timeline = await ProjectApi.ReadAsync<PipelineTimelinePageDto>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{runId}/timeline?limit=200", Ct));
        var lanes = timeline.Items.Where(i => i.Kind == PipelineTimelineKind.StageEntered && i.Branch is not null)
            .Select(i => (i.StageId, i.Branch))
            .Order()
            .ToList();
        Assert.Equal([("bear", "1"), ("bull", "0"), ("bull_review", "0")], lanes);
        Assert.Null(Assert.Single(timeline.Items, i => i.Kind == PipelineTimelineKind.StageEntered && i.StageId == "decide").Branch);
    }
}
