using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-356, §4.69 (M70): conversation stages in the run driver, end to end on the Fake provider with <c>fixture-callsim</c>
/// (a customer simulator opens, a support agent simulator replies, until "goodbye" or 6 turns) and
/// <c>fixture-callsim-guard</c> (stopped by the <c>flow.call-resolved</c> guard). Each turn is one <c>stage.run</c> job
/// and one agent run.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed partial class ConversationDriverTests(TevrinApiFactory factory)
{
    private const string Scenario = "The customer's home router keeps dropping the connection.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> StartAsync(ProjectFixture fixture, string pipelineId = FlowFixturePack.CallSim)
    {
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs",
                new { pipelineId, approvalMode = "advisory", inputs = new { scenario = Scenario } },
                Ct),
            HttpStatusCode.Created);
        return run.Id;
    }

    private static Task<PipelineRun> RunAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct));

    private static Task<List<AgentRun>> TurnRunsAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.AgentRuns.AsNoTracking()
            .Where(r => r.PipelineRunId == runId && r.TurnIndex != null).OrderBy(r => r.TurnIndex).ToListAsync(Ct));

    private static Task<List<Artifact>> TranscriptsAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking()
            .Where(a => a.PipelineRunId == runId && a.Type == "transcript").ToListAsync(Ct));

    /// <summary>Runs the project's queued <c>stage.run</c> jobs one at a time, oldest first, until none is left; returns how
    /// many ran.</summary>
    private static async Task<int> DriveAsync(WebApplicationFactory<Program> app, Guid projectId, int limit = 50)
    {
        for (var ran = 0; ran < limit; ran++)
        {
            var queued = (await FixtureLinearApp.QueuedDriverJobsAsync(app, projectId)).OrderBy(j => j.CreatedAt).ToList();
            if (queued.Count == 0)
            {
                return ran;
            }

            await AgentJobs.RunAsync(app, queued[0]);
        }

        Assert.Fail("The conversation did not settle.");
        return limit;
    }

    /// <summary>Runs the single queued <c>stage.run</c> job and returns it.</summary>
    private static async Task<Job> RunNextAsync(WebApplicationFactory<Program> app, Guid projectId)
    {
        var job = Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, projectId));
        await AgentJobs.RunAsync(app, job);
        return job;
    }

    private static TurnResponder Customer(params string[] answers) => new("customer-sim", answers);

    private static TurnResponder Support(params string[] answers) => new("support-sim", answers);

    [Fact]
    public async Task FR356_section_4_69_example_the_agent_says_Goodbye_at_turn_4_and_the_conversation_stops_there()
    {
        var customer = Customer("My router keeps dropping.", "Yes, it works now.", "unused");
        var support = Support("Have you restarted it?", "Great. Goodbye!", "unused");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), customer, support);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);

        // One stage.run job per turn: customer, agent, customer, agent.
        Assert.Equal(4, await DriveAsync(app, fixture.Project.Id));

        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Completed, "call"), (run.Status, run.CurrentStage));
        var turns = await TurnRunsAsync(app, runId);
        Assert.Equal([1, 2, 3, 4], turns.Select(t => t.TurnIndex!.Value));
        Assert.Equal(["customer", "agent", "customer", "agent"], turns.Select(t => t.TurnRole));
        Assert.Equal(["customer-sim", "support-sim", "customer-sim", "support-sim"], turns.Select(t => t.Agent));
        Assert.Equal((2, 2), (customer.Prompts.Count, support.Prompts.Count));
        var transcript = Assert.Single(await TranscriptsAsync(app, runId));
        Assert.Equal(ArtifactStatus.Locked, transcript.Status);
        Assert.Contains("Great. Goodbye!", transcript.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("unused", transcript.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR356_the_stop_guard_ends_the_conversation_when_it_passes_on_the_transcript()
    {
        var customer = Customer("My router keeps dropping.");
        var support = Support("I've reset your line [resolved]");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), customer, support);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.CallSimGuard);

        Assert.Equal(2, await DriveAsync(app, fixture.Project.Id));

        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
        Assert.Equal(["customer", "agent"], (await TurnRunsAsync(app, runId)).Select(t => t.TurnRole));
        Assert.Contains("[resolved]", Assert.Single(await TranscriptsAsync(app, runId)).Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR356_without_the_phrase_the_conversation_stops_at_max_turns_6()
    {
        var customer = Customer("It's still broken.");
        var support = Support("Let me check again.");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), customer, support);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);

        Assert.Equal(6, await DriveAsync(app, fixture.Project.Id));

        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
        Assert.Equal([1, 2, 3, 4, 5, 6], (await TurnRunsAsync(app, runId)).Select(t => t.TurnIndex!.Value));
        Assert.Equal((3, 3), (customer.Prompts.Count, support.Prompts.Count));
        Assert.Single(await TranscriptsAsync(app, runId));
    }

    [Fact]
    public async Task FR356_the_transcript_is_turns_of_role_agentKey_text_and_agentRunId_and_a_text_json_reply_is_read_as_its_text()
    {
        // The support simulator answers { "text": … } JSON; the customer answers plain text.
        var customer = Customer("My router keeps dropping.");
        var support = Support("{\"text\": \"Have you restarted it? Goodbye.\"}");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), customer, support);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);

        await DriveAsync(app, fixture.Project.Id);

        var runs = await TurnRunsAsync(app, runId);
        using var doc = JsonDocument.Parse(Assert.Single(await TranscriptsAsync(app, runId)).Content);
        Assert.Equal(["turns"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        var turns = doc.RootElement.GetProperty("turns").EnumerateArray().ToList();
        Assert.Equal(2, turns.Count);
        Assert.All(turns, t => Assert.Equal(["role", "agentKey", "text", "agentRunId"], t.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(
            [("customer", "customer-sim", "My router keeps dropping."), ("agent", "support-sim", "Have you restarted it? Goodbye.")],
            turns.Select(t => (t.GetProperty("role").GetString(), t.GetProperty("agentKey").GetString(), t.GetProperty("text").GetString())));
        Assert.Equal(runs.Select(r => r.Id), turns.Select(t => t.GetProperty("agentRunId").GetGuid()));
        Assert.Equal([(1, "customer"), (2, "agent")], runs.Select(r => (r.TurnIndex!.Value, r.TurnRole!)));
        Assert.All(runs, r => Assert.Null(r.ItemIndex));
    }

    [Fact]
    public async Task FR356_each_participant_gets_only_its_own_persona_prompt_its_role_the_scenario_and_the_transcript_so_far()
    {
        var customer = Customer("My router keeps dropping.", "Yes, it works now.");
        var support = Support("Have you restarted it?", "Great. Goodbye!");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), customer, support);
        using var fixture = await ProjectApi.FixtureAsync(app);
        await StartAsync(fixture);

        await DriveAsync(app, fixture.Project.Id);

        Assert.All(customer.Prompts, p =>
        {
            Assert.Contains("You are a customer calling support.", p, StringComparison.Ordinal);
            Assert.DoesNotContain("You are a support agent.", p, StringComparison.Ordinal);
            Assert.Contains("You are the customer.", p, StringComparison.Ordinal);
            Assert.Contains(Scenario, p, StringComparison.Ordinal);
        });
        Assert.All(support.Prompts, p =>
        {
            Assert.Contains("You are a support agent.", p, StringComparison.Ordinal);
            Assert.DoesNotContain("You are a customer calling support.", p, StringComparison.Ordinal);
            Assert.Contains("You are the agent.", p, StringComparison.Ordinal);
        });

        // The opener starts with no turns; each later turn sees exactly the turns before it.
        Assert.Contains(ConversationRules.EmptyTranscript, customer.Prompts[0], StringComparison.Ordinal);
        Assert.Contains("### turn 1 (customer)\nMy router keeps dropping.", support.Prompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("### turn 2", support.Prompts[0], StringComparison.Ordinal);
        Assert.Contains("### turn 2 (agent)\nHave you restarted it?", customer.Prompts[1], StringComparison.Ordinal);
        Assert.DoesNotContain("### turn 3", customer.Prompts[1], StringComparison.Ordinal);
        Assert.Contains("### turn 3 (customer)\nYes, it works now.", support.Prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR356_a_redelivered_turn_job_creates_no_duplicate_turn_agent_run_or_next_job()
    {
        var customer = Customer("My router keeps dropping.", "Yes, it works now.");
        var support = Support("Have you restarted it?", "Great. Goodbye!");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), customer, support);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);

        // Turn 1, delivered twice: one model call, one turn run, one next job.
        var first = await RunNextAsync(app, fixture.Project.Id);
        await AgentJobs.RunAsync(app, first);
        Assert.Single(customer.Prompts);
        Assert.Single(await TurnRunsAsync(app, runId));
        var second = Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));

        // Turn 2 runs; turn 1's job delivered again afterwards is stale (a later turn started): nothing more.
        await AgentJobs.RunAsync(app, second);
        await AgentJobs.RunAsync(app, first);
        await AgentJobs.RunAsync(app, second);
        Assert.Equal((1, 1), (customer.Prompts.Count, support.Prompts.Count));
        Assert.Equal([1, 2], (await TurnRunsAsync(app, runId)).Select(t => t.TurnIndex!.Value));
        Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));

        // The rest, then the last job again once the run is complete: one transcript, four turns.
        await DriveAsync(app, fixture.Project.Id);
        var jobs = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == fixture.Project.Id && j.Type == EngineJobTypes.StageRun).OrderBy(j => j.CreatedAt).ToListAsync(Ct));
        await AgentJobs.RunAsync(app, jobs[^1]);
        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
        Assert.Equal(4, jobs.Count);
        Assert.Equal(4, (await TurnRunsAsync(app, runId)).Count);
        Assert.Equal((2, 2), (customer.Prompts.Count, support.Prompts.Count));
        Assert.Single(await TranscriptsAsync(app, runId));
    }

    [Fact]
    public async Task FR356_cancelling_mid_conversation_stops_further_turns()
    {
        var customer = Customer("It's still broken.");
        var support = Support("Let me check again.");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), customer, support);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);
        await RunNextAsync(app, fixture.Project.Id);
        await RunNextAsync(app, fixture.Project.Id);
        var third = Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));

        var cancelled = await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Owner.Client.PostAsync($"/api/pipeline-runs/{runId}/cancel", null, Ct));

        Assert.Equal(GenericRunStatus.Cancelled, cancelled.Status);
        Assert.Empty(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));

        // The third turn's job, delivered anyway (it was claimed before the cancel), finds the run final: no call, no turn.
        await AgentJobs.RunAsync(app, third);
        Assert.Equal(0, await DriveAsync(app, fixture.Project.Id));
        Assert.Equal((1, 1), (customer.Prompts.Count, support.Prompts.Count));
        Assert.Equal(2, (await TurnRunsAsync(app, runId)).Count);
        Assert.Empty(await TranscriptsAsync(app, runId));
        Assert.Equal(GenericRunStatus.Cancelled, (await RunAsync(app, runId)).Status);
        var executions = await ProjectApi.WithDbAsync(app, db => db.StageExecutions.AsNoTracking().Where(e => e.PipelineRunId == runId).ToListAsync(Ct));
        Assert.All(executions, e => Assert.NotNull(e.ExitedAt));
    }

    [Fact]
    public async Task FR356_turn_runs_of_another_entrys_jobs_are_not_this_entrys_turns()
    {
        var customer = Customer("My router keeps dropping.", "Yes, it works now.");
        var support = Support("Have you restarted it?", "Great. Goodbye!");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), customer, support);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);
        await RunNextAsync(app, fixture.Project.Id);

        // A straggler from an earlier entry (a rollback or loop-back raced its job) records turns 1 and 5 in this stage,
        // after this entry began, under ids no job of this entry derives: they must neither replace turn 1 nor make the
        // next turn's job stale.
        await ProjectApi.WithDbAsync(app, async db =>
        {
            var original = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == runId && r.TurnIndex == 1, Ct);
            foreach (var turn in (int[])[1, 5])
            {
                var straggler = new AgentRun();
                db.Entry(straggler).CurrentValues.SetValues(original);
                straggler.Id = Guid.CreateVersion7();
                straggler.TurnIndex = turn;
                straggler.Output = original.Output!.Replace("My router keeps dropping.", "A straggler reply.", StringComparison.Ordinal);
                straggler.CreatedAt = original.CreatedAt.AddSeconds(1);
                db.AgentRuns.Add(straggler);
            }

            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(3, await DriveAsync(app, fixture.Project.Id));

        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
        Assert.Equal((2, 2), (customer.Prompts.Count, support.Prompts.Count));
        var transcript = Assert.Single(await TranscriptsAsync(app, runId)).Content;
        Assert.Contains("My router keeps dropping.", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("straggler", transcript, StringComparison.Ordinal);
        Assert.All(support.Prompts, p => Assert.DoesNotContain("straggler", p, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR356_a_conversation_waiting_on_its_input_runs_its_first_turn_once_the_input_is_submitted()
    {
        var customer = Customer("My router keeps dropping.");
        var support = Support("Goodbye!");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), customer, support);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs", new { pipelineId = FlowFixturePack.CallSim, approvalMode = "advisory" }, Ct),
            HttpStatusCode.Created);

        await DriveAsync(app, fixture.Project.Id);
        Assert.Equal(GenericRunStatus.WaitingOnInput, (await RunAsync(app, run.Id)).Status);
        Assert.Empty(customer.Prompts);

        // The entry is planned once: with the input present it reaches the conversation, which runs its turns.
        await ProjectApi.ReadAsync<PipelineArtifactVersionDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"/api/pipeline-runs/{run.Id}/inputs/scenario", Scenario, Ct), HttpStatusCode.Created);
        Assert.Equal(2, await DriveAsync(app, fixture.Project.Id));

        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, run.Id)).Status);
        Assert.Contains(Scenario, Assert.Single(customer.Prompts), StringComparison.Ordinal);
        Assert.Empty(await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == fixture.Project.Id && j.Status == JobStatus.Failed).ToListAsync(Ct)));
    }
}
