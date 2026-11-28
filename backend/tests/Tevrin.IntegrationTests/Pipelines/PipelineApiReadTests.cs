using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Infrastructure.Persistence;
using Tevrin.IntegrationTests.Support;
using Tevrin.Pack.Sdlc;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>FR-298, FR-299, FR-300, FR-303: the generic read endpoints and their permissions.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PipelineApiReadTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri Url(string path) => new(path, UriKind.Relative);

    [Fact]
    public async Task FR299_pipelines_and_definitions_by_hash_are_listed_for_signed_in_users()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var current = SdlcRunDefinition.Current(factory.Services.GetRequiredService<PipelineRegistryHost>());

        var pipelines = await ProjectApi.ReadAsync<List<PipelineSummaryDto>>(await fixture.Viewer.Client.GetAsync(Url("/api/pipelines"), Ct));
        var sdlc = Assert.Single(pipelines, p => p.Id == SdlcPack.PipelineId);
        Assert.Equal(current.Hash, sdlc.Hash);

        var definition = await ProjectApi.ReadAsync<PipelineDefinitionDto>(
            await fixture.Viewer.Client.GetAsync(Url($"/api/pipelines/{SdlcPack.PipelineId}/definitions/{sdlc.Hash}"), Ct));
        Assert.Equal(current.Definition!.Stages.Select(s => s.Id), definition.Stages.Select(s => s.Id));
        Assert.Contains(definition.Stages, s => s.Gate?.Type == SdlcGates.PlanApproval);
        Assert.NotEmpty(definition.Artifacts);
        Assert.NotEmpty(definition.Statuses);

        await Problems.AssertAsync(
            await fixture.Viewer.Client.GetAsync(Url($"/api/pipelines/{SdlcPack.PipelineId}/definitions/{new string('0', 64)}"), Ct),
            HttpStatusCode.NotFound,
            ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR299_anonymous_callers_get_401()
    {
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/api/pipelines", "/api/pipeline-gates", $"/api/pipeline-runs/{Guid.NewGuid()}", $"/api/projects/{Guid.NewGuid()}/pipeline-runs" })
        {
            using var response = await client.GetAsync(Url(path), Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task FR300_a_run_its_stages_and_artifacts_are_readable_by_viewers_and_hidden_from_outsiders()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var runs = await ProjectApi.ReadAsync<List<PipelineRunDto>>(await fixture.Viewer.Client.GetAsync(Url($"{fixture.Path}/pipeline-runs"), Ct));
        var run = Assert.Single(runs);
        Assert.Equal((fixture.Project.Id, SdlcPack.PipelineId, 1), (run.ProjectId, run.PipelineId, run.Checkpoint));
        Assert.Equal(run.DefinitionHash[..12], run.HashShort);

        var detail = await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Viewer.Client.GetAsync(Url($"/api/pipeline-runs/{run.Id}"), Ct));
        Assert.Equal(run.Id, detail.Id);
        Assert.Contains(detail.Status, new[] { GenericRunStatus.Active, GenericRunStatus.Running, GenericRunStatus.WaitingOnGate });

        var stages = await ProjectApi.ReadAsync<List<PipelineStageDto>>(await fixture.Viewer.Client.GetAsync(Url($"/api/pipeline-runs/{run.Id}/stages"), Ct));
        Assert.Single(stages, s => s.State == StageState.Active && s.Id == detail.CurrentStage);

        await ProjectApi.ReadAsync<List<PipelineArtifactDto>>(await fixture.Viewer.Client.GetAsync(Url($"/api/pipeline-runs/{run.Id}/artifacts"), Ct));
        await Problems.AssertAsync(
            await fixture.Viewer.Client.GetAsync(Url($"/api/pipeline-runs/{run.Id}/artifacts/nothing/versions/1"), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await fixture.Viewer.Client.GetAsync(Url($"/api/pipeline-runs/{run.Id}/artifacts/nothing/diff"), Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);

        foreach (var path in new[] { $"/api/pipeline-runs/{run.Id}", $"/api/pipeline-runs/{run.Id}/timeline", $"/api/pipeline-runs/{run.Id}/replay", $"{fixture.Path}/pipeline-runs" })
        {
            await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(Url(path), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }

        await Problems.AssertAsync(await fixture.Viewer.Client.GetAsync(Url($"/api/pipeline-runs/{Guid.NewGuid()}"), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR300_a_timeline_of_120_entries_pages_by_50_and_returns_each_once_in_order()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var runId = await PipelineApiGateTests.RunIdAsync(factory, fixture.Project.Id);
        // timestamptz keeps microseconds: truncate so the stored times compare equal to these (as the SDLC agent runs do).
        var now = DateTimeOffset.UtcNow.AddDays(1);
        var start = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            for (var i = 0; i < 120; i++)
            {
                // Pairs share a timestamp, so ties are broken by id.
                db.StageExecutions.Add(new StageExecution
                {
                    PipelineRunId = runId,
                    StageId = "refinement",
                    Iteration = i + 1,
                    Checkpoint = 1,
                    EnteredAt = start.AddSeconds(i / 2),
                    ExitedAt = start.AddSeconds(i / 2),
                });
            }

            await db.SaveChangesAsync(Ct);
        });

        var all = await ReadAllAsync(fixture, runId, 200, "timeline");
        var paged = await ReadAllAsync(fixture, runId, 50, "timeline");

        Assert.True(paged.Count >= 120);
        Assert.Equal(all.Select(i => (i.Kind, i.Id)), paged.Select(i => (i.Kind, i.Id)));
        Assert.Equal(paged.Count, paged.Select(i => (i.Kind, i.Id)).Distinct().Count());
        Assert.Equal(paged.Select(i => i.At).Order(), paged.Select(i => i.At));
        Assert.Equal(120, paged.Count(i => i.Kind == PipelineTimelineKind.StageEntered && i.At >= start));
        await Problems.AssertAsync(
            await fixture.Viewer.Client.GetAsync(Url($"/api/pipeline-runs/{runId}/timeline?limit=201"), Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR303_the_replay_includes_agent_input_and_output_and_changes_nothing()
    {
        using var flow = await Characterisation.SdlcFlow.CreateAsync(factory, factory, "advisory");
        await flow.ToSpecificationAsync();
        var runId = await PipelineApiGateTests.RunIdAsync(factory, flow.Id);
        var before = await CountsAsync(flow.Id);

        var replay = await ReadAllAsync(flow.Fixture.Owner, runId, 200, "replay");
        var timeline = await ReadAllAsync(flow.Fixture.Owner, runId, 200, "timeline");

        var agentRuns = replay.Where(i => i.Kind == PipelineTimelineKind.AgentRun).ToList();
        Assert.NotEmpty(agentRuns);
        Assert.All(agentRuns, r => Assert.StartsWith("[system]\n", r.Input, StringComparison.Ordinal));
        Assert.All(agentRuns, r => Assert.False(string.IsNullOrEmpty(r.Output)));
        Assert.All(timeline.Where(i => i.Kind == PipelineTimelineKind.AgentRun), r => Assert.Null(r.Input));
        Assert.Equal(timeline.Select(i => (i.Kind, i.Id)), replay.Select(i => (i.Kind, i.Id)));
        Assert.Equal(before, await CountsAsync(flow.Id));
    }

    [Fact]
    public async Task FR298_prompt_stats_cover_only_projects_the_caller_can_see()
    {
        using var flow = await Characterisation.SdlcFlow.CreateAsync(factory, factory, "advisory");
        await flow.ToSpecificationAsync();
        using var outsider = await ApiSession.RegisterAsync(factory);

        var mine = await ProjectApi.ReadAsync<List<PromptStatsDto>>(
            await flow.Fixture.Owner.Client.GetAsync(Url($"/api/pipelines/{SdlcPack.PipelineId}/prompt-stats?projectId={flow.Id}"), Ct));
        Assert.NotEmpty(mine);
        Assert.All(mine, s => Assert.True(s.Runs > 0));

        var visible = await ProjectApi.ReadAsync<List<PromptStatsDto>>(await flow.Fixture.Owner.Client.GetAsync(Url($"/api/pipelines/{SdlcPack.PipelineId}/prompt-stats"), Ct));
        Assert.True(visible.Sum(s => s.Runs) >= mine.Sum(s => s.Runs));

        Assert.Empty(await ProjectApi.ReadAsync<List<PromptStatsDto>>(await outsider.Client.GetAsync(Url($"/api/pipelines/{SdlcPack.PipelineId}/prompt-stats"), Ct)));
        await Problems.AssertAsync(
            await outsider.Client.GetAsync(Url($"/api/pipelines/{SdlcPack.PipelineId}/prompt-stats?projectId={flow.Id}"), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await outsider.Client.GetAsync(Url("/api/pipelines/no-such-pipeline/prompt-stats"), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    private static Task<List<PipelineTimelineItemDto>> ReadAllAsync(ProjectFixture fixture, Guid runId, int limit, string view) =>
        ReadAllAsync(fixture.Viewer, runId, limit, view);

    private static async Task<List<PipelineTimelineItemDto>> ReadAllAsync(ApiSession session, Guid runId, int limit, string view)
    {
        var items = new List<PipelineTimelineItemDto>();
        string? cursor = null;
        for (var pages = 0; pages < 100; pages++)
        {
            var query = cursor is null ? $"limit={limit}" : $"limit={limit}&cursor={Uri.EscapeDataString(cursor)}";
            var page = await ProjectApi.ReadAsync<PipelineTimelinePageDto>(await session.Client.GetAsync(Url($"/api/pipeline-runs/{runId}/{view}?{query}"), Ct));
            Assert.True(page.Items.Count <= limit);
            items.AddRange(page.Items);
            cursor = page.Next;
            if (cursor is null)
            {
                return items;
            }
        }

        throw new InvalidOperationException("The timeline didn't end.");
    }

    private Task<(int Runs, int Jobs, int Events, int Gates)> CountsAsync(Guid projectId) =>
        ProjectApi.WithDbAsync(factory, async db => (
            await db.AgentRuns.CountAsync(r => r.ProjectId == projectId, Ct),
            await db.Jobs.CountAsync(j => j.ProjectId == projectId, Ct),
            await db.Events.CountAsync(e => e.ProjectId == projectId, Ct),
            await db.ApprovalGates.CountAsync(g => g.ProjectId == projectId, Ct)));
}
