using System.Net;
using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>FR-363 (M72): a map stage's progress bar, its paged item list and the item labels on the timeline.</summary>
public sealed partial class MapDriverTests
{
    [Fact]
    public async Task FR363_a_map_stage_shows_200_total_197_completed_3_failed_and_pages_its_items()
    {
        var log = new QaLog();
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), log);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.QaHandlers, Transcripts(200, 5, 50, 150));
        await DriveAsync(app, fixture.Project.Id, runId);
        var viewer = fixture.Viewer.Client;
        var items = await ItemsAsync(app, runId);
        var mapExecution = items[0].ParentExecutionId!.Value;

        var stage = (await ProjectApi.ReadAsync<List<PipelineStageDto>>(await viewer.GetAsync($"/api/pipeline-runs/{runId}/stages", Ct))).Single(s => s.Id == "score");
        Assert.Equal(new PipelineMapProgressDto(mapExecution, 200, 197, 3), stage.Map);

        var url = $"/api/pipeline-runs/{runId}/stage-executions/{mapExecution}/items";
        var first = await ProjectApi.ReadAsync<PipelineMapItemPageDto>(await viewer.GetAsync($"{url}?limit=50", Ct));
        Assert.Equal(stage.Map, first.Progress);
        Assert.Equal(Enumerable.Range(0, 50), first.Items.Select(i => i.ItemIndex));
        Assert.Equal("49", first.Next);
        Assert.Equal(MapItemStatus.Failed, first.Items[5].Status);
        Assert.Equal(MapItemStatus.Completed, first.Items[4].Status);
        Assert.Equal(items[4].Id, first.Items[4].ExecutionId);

        var second = await ProjectApi.ReadAsync<PipelineMapItemPageDto>(await viewer.GetAsync($"{url}?limit=50&cursor={first.Next}", Ct));
        Assert.Equal(Enumerable.Range(50, 50), second.Items.Select(i => i.ItemIndex));

        var failed = await ProjectApi.ReadAsync<PipelineMapItemPageDto>(await viewer.GetAsync($"{url}?status=failed", Ct));
        Assert.Equal([5, 50, 150], failed.Items.Select(i => i.ItemIndex));
        Assert.Null(failed.Next);

        await Problems.AssertAsync(await viewer.GetAsync($"{url}?status=nope", Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await viewer.GetAsync($"{url}?cursor=x", Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await viewer.GetAsync($"{url}?limit=0", Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(
            await viewer.GetAsync($"/api/pipeline-runs/{runId}/stage-executions/{items[0].Id}/items", Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await viewer.GetAsync($"/api/pipeline-runs/{runId}/stage-executions/{Guid.NewGuid()}/items", Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(url, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);

        // The timeline labels each item's stage execution with its index and its map execution.
        var timeline = await ProjectApi.ReadAsync<PipelineTimelinePageDto>(await viewer.GetAsync($"/api/pipeline-runs/{runId}/timeline?limit=200", Ct));
        var entries = timeline.Items.Where(i => i.Kind == PipelineTimelineKind.StageEntered && i.ParentExecutionId == mapExecution).ToList();
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.NotNull(e.ItemIndex));
    }

    [Fact]
    public async Task FR363_the_definition_describes_the_map_and_its_reduce()
    {
        var scorer = new RecordingResponder("qa-scorer", "{\"score\": 4}");
        var summariser = new RecordingResponder("qa-summariser", "{\"mean\": 4.0, \"count\": 1}");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), scorer, summariser);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Qa, Transcripts(1));
        var run = await RunAsync(app, runId);

        var definition = await ProjectApi.ReadAsync<PipelineDefinitionDto>(
            await fixture.Viewer.Client.GetAsync($"/api/pipelines/{FlowFixturePack.Qa}/definitions/{run.DefinitionHash}", Ct));
        var map = definition.Stages.Single(s => s.Id == "score").Map;
        Assert.Equal(
            new PipelineMapDefinitionDto("transcripts.items", "qa-scorer", null, 8, 1000, MapItemFailure.Skip, "5%", "qa-summariser", null),
            map);
    }

    [Fact]
    public async Task FR363_paging_an_earlier_map_execution_shows_its_own_agent_runs_and_outputs_after_the_stage_is_re_entered()
    {
        var scorer = new RecordingResponder("qa-scorer", "{\"score\": 4}");
        var summariser = new RecordingResponder("qa-summariser", "{\"mean\": 4.0, \"count\": 3}");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), scorer, summariser);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Qa, Transcripts(3));
        await DriveAsync(app, fixture.Project.Id, runId);
        var firstItems = await ItemsAsync(app, runId);
        var first = firstItems[0].ParentExecutionId!.Value;
        var url = $"/api/pipeline-runs/{runId}/stage-executions/{first}/items";
        var before = await ProjectApi.ReadAsync<PipelineMapItemPageDto>(await fixture.Viewer.Client.GetAsync(url, Ct));
        Assert.All(before.Items, i => Assert.NotNull(i.AgentRunId));
        Assert.All(before.Items, i => Assert.Equal("scores", i.ArtifactKey));

        // The stage is entered again (as by a route, loop or rollback): a new map execution whose item 0 ran and stored
        // an output of its own, later than everything of the first.
        var (second, laterRun, laterOutput) = await ProjectApi.WithDbAsync<(Guid, Guid, Guid)>(app, async db =>
        {
            var output = await db.Artifacts.AsNoTracking().FirstAsync(a => a.PipelineRunId == runId && a.Type == "scores" && a.ItemIndex == 0, Ct);
            var at = (await db.Artifacts.AsNoTracking().Where(a => a.PipelineRunId == runId).MaxAsync(a => a.CreatedAt, Ct)).AddSeconds(5);
            var entry = new StageExecution { PipelineRunId = runId, StageId = "score", Iteration = 2, Checkpoint = 1, EnteredAt = at };
            db.StageExecutions.Add(entry);
            db.StageExecutions.Add(new StageExecution
            {
                PipelineRunId = runId,
                StageId = "score",
                Iteration = 2,
                Checkpoint = 1,
                EnteredAt = at,
                ItemIndex = 0,
                ParentExecutionId = entry.Id,
            });
            var agentRun = new AgentRun
            {
                ProjectId = fixture.Project.Id,
                PipelineRunId = runId,
                Agent = "qa-scorer",
                AgentKey = "qa-scorer",
                Stage = "score",
                Model = "fake",
                PromptVersion = "flow.qa-score-1",
                Status = AgentRunStatus.Succeeded,
                ItemIndex = 0,
                CreatedAt = at.AddSeconds(1),
            };
            db.AgentRuns.Add(agentRun);
            var artifact = new Artifact
            {
                ProjectId = fixture.Project.Id,
                PipelineRunId = runId,
                ReleaseVersionId = output.ReleaseVersionId,
                Type = "scores",
                Version = 99,
                Content = "{\"score\": 1}",
                ContentHash = ContentHash.Compute("{\"score\": 1}"),
                Status = ArtifactStatus.Locked,
                CheckpointAtCreation = 1,
                ItemIndex = 0,
                AgentRunId = agentRun.Id,
                CreatedAt = at.AddSeconds(2),
            };
            db.Artifacts.Add(artifact);
            await db.SaveChangesAsync(Ct);
            return (entry.Id, agentRun.Id, artifact.Id);
        });

        var after = await ProjectApi.ReadAsync<PipelineMapItemPageDto>(await fixture.Viewer.Client.GetAsync(url, Ct));
        Assert.Equal(
            before.Items.Select(i => (i.ItemIndex, i.AgentRunId, i.ArtifactId)),
            after.Items.Select(i => (i.ItemIndex, i.AgentRunId, i.ArtifactId)));
        Assert.DoesNotContain(after.Items, i => i.AgentRunId == laterRun || i.ArtifactId == laterOutput);

        var newer = await ProjectApi.ReadAsync<PipelineMapItemPageDto>(
            await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{runId}/stage-executions/{second}/items", Ct));
        var item = Assert.Single(newer.Items);
        Assert.Equal((MapItemStatus.Open, (Guid?)laterRun, (Guid?)laterOutput, 99), (item.Status, item.AgentRunId, item.ArtifactId, item.ArtifactVersion ?? 0));
    }
}
