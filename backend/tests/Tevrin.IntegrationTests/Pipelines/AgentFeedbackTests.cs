using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-351, §4.75 (M69): the agent feedback store end to end on the Fake provider, with <c>fixture-postmortem</c>: people add,
/// list and delete entries for the worker; the worker's prompt gets them in <c>{{AGENT_FEEDBACK}}</c> and its agent run
/// records which; the reviewer's postmortem <c>lessons</c> become <c>stage</c> entries for the worker.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class AgentFeedbackTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string NoLessons = """{ "lessons": [] }""";

    private static string FeedbackPath(ProjectFixture fixture, string agentKey = "worker", string pipelineId = FlowFixturePack.Postmortem) =>
        $"{fixture.Path}/pipelines/{pipelineId}/agents/{agentKey}/feedback";

    private static string EntryPath(ProjectFixture fixture, Guid feedbackId) => $"{fixture.Path}/agent-feedback/{feedbackId}";

    private static async Task<AgentFeedbackDto> AddAsync(ApiSession session, ProjectFixture fixture, string text) =>
        await ProjectApi.ReadAsync<AgentFeedbackDto>(
            await session.Client.PostAsJsonAsync(FeedbackPath(fixture), new { text }, Ct), HttpStatusCode.Created);

    private static async Task<List<AgentFeedbackDto>> ListAsync(ApiSession session, ProjectFixture fixture) =>
        await ProjectApi.ReadAsync<List<AgentFeedbackDto>>(await session.Client.GetAsync(FeedbackPath(fixture), Ct));

    private static string Line(AgentFeedbackDto entry) =>
        $"- [{entry.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {(entry.Source == AgentFeedbackSource.Stage ? "stage" : "human")}] {entry.Text}";

    private static async Task<Guid> RunAsync(WebApplicationFactory<Program> app, ProjectFixture fixture)
    {
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs",
                new { pipelineId = FlowFixturePack.Postmortem, approvalMode = "advisory", inputs = new { task = "Summarise the report." } },
                Ct),
            HttpStatusCode.Created);
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);
        var done = await ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id, Ct));
        Assert.Equal(GenericRunStatus.Completed, done.Status);
        return run.Id;
    }

    private static async Task<AgentRun> AgentRunAsync(WebApplicationFactory<Program> app, Guid projectId, Guid runId, string agent) =>
        Assert.Single(await AgentJobs.RunsAsync(app, projectId), r => r.PipelineRunId == runId && r.Agent == agent);

    [Fact]
    public async Task FR351_a_human_entry_is_added_listed_and_injected_into_the_next_runs_prompt()
    {
        var worker = new RecordingResponder("worker", "Summary.");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), worker, new ScriptedResponder("reviewer", NoLessons));
        using var fixture = await ProjectApi.FixtureAsync(app);

        var entry = await AddAsync(fixture.Contributor, fixture, "  Cite your sources.  ");
        Assert.Equal(("worker", FlowFixturePack.Postmortem, AgentFeedbackSource.Human, "Cite your sources."),
            (entry.AgentKey, entry.PipelineId, entry.Source, entry.Text));
        Assert.Equal(fixture.Contributor.User!.Id, entry.CreatedBy);
        Assert.Null(entry.RunId);
        Assert.Equal([entry.Id], (await ListAsync(fixture.Viewer, fixture)).Select(e => e.Id));

        var runId = await RunAsync(app, fixture);

        var prompt = Assert.Single(worker.Prompts);
        Assert.Contains("Lessons from earlier runs:\n" + Line(entry), prompt, StringComparison.Ordinal);
        Assert.Equal([entry.Id], (await AgentRunAsync(app, fixture.Project.Id, runId, "worker")).FeedbackIds);

        // The reviewer's stage doesn't inject feedback: its run records none.
        Assert.Null((await AgentRunAsync(app, fixture.Project.Id, runId, "reviewer")).FeedbackIds);
    }

    [Fact]
    public async Task FR351_a_stage_writes_an_entry_per_output_item_and_the_next_run_injects_them()
    {
        var worker = new RecordingResponder("worker", "Summary.");
        var reviewer = new ScriptedResponder("reviewer",
            """{ "lessons": ["Check the units.", { "text": "Name the source." }, "  "] }""", NoLessons);
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), worker, reviewer);
        using var fixture = await ProjectApi.FixtureAsync(app);

        var firstRun = await RunAsync(app, fixture);

        // The first run had nothing to inject.
        Assert.Equal([], (await AgentRunAsync(app, fixture.Project.Id, firstRun, "worker")).FeedbackIds!);
        var review = await AgentRunAsync(app, fixture.Project.Id, firstRun, "reviewer");
        var written = await ProjectApi.WithDbAsync(app, db => db.AgentFeedback.AsNoTracking()
            .Where(f => f.ProjectId == fixture.Project.Id).OrderBy(f => f.CreatedAt).ToListAsync(Ct));
        Assert.Equal(["Check the units.", "Name the source."], written.Select(f => f.Text));
        Assert.All(written, f =>
        {
            Assert.Equal((AgentFeedbackSource.Stage, "worker", FlowFixturePack.Postmortem), (f.Source, f.AgentKey, f.PipelineId));
            Assert.Equal((firstRun, review.Id), (f.RunId, f.AgentRunId));
            Assert.Null(f.CreatedBy);
        });

        var listed = await ListAsync(fixture.Viewer, fixture);
        Assert.Equal(["Name the source.", "Check the units."], listed.Select(e => e.Text));

        var secondRun = await RunAsync(app, fixture);

        Assert.Equal(2, worker.Prompts.Count);
        Assert.Contains(Line(listed[0]) + "\n" + Line(listed[1]), worker.Prompts[1], StringComparison.Ordinal);
        Assert.Equal(listed.Select(e => e.Id), (await AgentRunAsync(app, fixture.Project.Id, secondRun, "worker")).FeedbackIds!);

        // The second postmortem had no lessons: nothing more was written.
        Assert.Equal(2, await ProjectApi.WithDbAsync(app, db => db.AgentFeedback.CountAsync(f => f.ProjectId == fixture.Project.Id, Ct)));
    }

    [Fact]
    public async Task FR351_a_deleted_entry_is_not_listed_or_injected()
    {
        var worker = new RecordingResponder("worker", "Summary.");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), worker, new ScriptedResponder("reviewer", NoLessons));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var removed = await AddAsync(fixture.Contributor, fixture, "Use bullet points.");
        var kept = await AddAsync(fixture.Owner, fixture, "Keep it under 100 words.");

        var deleted = await fixture.Contributor.Client.DeleteAsync(EntryPath(fixture, removed.Id), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal([kept.Id], (await ListAsync(fixture.Viewer, fixture)).Select(e => e.Id));

        var runId = await RunAsync(app, fixture);

        var prompt = Assert.Single(worker.Prompts);
        Assert.Contains(Line(kept), prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Use bullet points.", prompt, StringComparison.Ordinal);
        Assert.Equal([kept.Id], (await AgentRunAsync(app, fixture.Project.Id, runId, "worker")).FeedbackIds);

        // The row is kept (soft delete) and a second delete finds nothing.
        var row = await ProjectApi.WithDbAsync(app, db => db.AgentFeedback.AsNoTracking().SingleAsync(f => f.Id == removed.Id, Ct));
        Assert.NotNull(row.DeletedAt);
        await Problems.AssertAsync(await fixture.Contributor.Client.DeleteAsync(EntryPath(fixture, removed.Id), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR351_only_the_owner_or_the_author_deletes_an_entry()
    {
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit());
        using var fixture = await ProjectApi.FixtureAsync(app);
        var owners = await AddAsync(fixture.Owner, fixture, "Owner's lesson.");
        var contributors = await AddAsync(fixture.Contributor, fixture, "Contributor's lesson.");

        await Problems.AssertAsync(await fixture.Contributor.Client.DeleteAsync(EntryPath(fixture, owners.Id), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Viewer.Client.DeleteAsync(EntryPath(fixture, contributors.Id), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Outsider.Client.DeleteAsync(EntryPath(fixture, contributors.Id), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);

        var deleted = await fixture.Owner.Client.DeleteAsync(EntryPath(fixture, contributors.Id), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal([owners.Id], (await ListAsync(fixture.Owner, fixture)).Select(e => e.Id));
    }

    [Fact]
    public async Task FR351_adding_checks_the_role_the_text_and_the_pipeline_and_agent()
    {
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit());
        using var fixture = await ProjectApi.FixtureAsync(app);

        await Problems.AssertAsync(
            await fixture.Viewer.Client.PostAsJsonAsync(FeedbackPath(fixture), new { text = "x" }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync(FeedbackPath(fixture), new { text = "  " }, Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync(FeedbackPath(fixture), new { text = new string('x', 2001) }, Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await AddAsync(fixture.Contributor, fixture, new string('x', 2000));

        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync(FeedbackPath(fixture, agentKey: "planner"), new { text = "x" }, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await fixture.Viewer.Client.GetAsync(FeedbackPath(fixture, pipelineId: "no-such-pipeline"), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(FeedbackPath(fixture), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }
}
