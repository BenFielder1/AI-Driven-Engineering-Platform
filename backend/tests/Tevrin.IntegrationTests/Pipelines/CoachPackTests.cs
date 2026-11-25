using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.IntegrationTests.Support;
using Tevrin.Pack.Coach;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-339, FR-341, §4.63 (M67): the Impulse coaching pack end to end on the Fake provider. The athlete profile is a
/// locked project-scope artifact (last actual week 50 km, no race); the session log is the run's input. The planner's
/// attempts are scripted (<see cref="CoachScript"/>): the worked example escalates after 3 attempts, the passing variant
/// reaches the coach's approval and the message. Agent stages of the coach pipeline keep the FR-26 project budget.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class CoachPackTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ProfilePath(ProjectFixture fixture) =>
        $"{fixture.Path}/pipelines/{CoachPack.PipelineId}/artifacts/{CoachArtifacts.AthleteProfile}";

    /// <summary>The last actual week, Mon 2026-11-16 to Sun 2026-11-22: 10 + 15 + 25 = 50 km.</summary>
    private static object SessionLog() => new
    {
        sessions = new object[]
        {
            new { date = "2026-11-17", type = "intervals", intensity = "hard", distanceKm = 10 },
            new { date = "2026-11-19", type = "easy run", intensity = "easy", distanceKm = 15 },
            new { date = "2026-11-22", type = "long run", intensity = "easy", distanceKm = 25 },
        },
    };

    private static object Start() => new { pipelineId = CoachPack.PipelineId, approvalMode = "strict", inputs = new { session_log = SessionLog() } };

    private WebApplicationFactory<Program> App(CoachScript script) =>
        AgentJobs.WithResponders(
            factory,
            new FixedResponder(CoachAgents.PerformanceAnalyst, """{"summary":"A steady week.","lastWeekVolume":50,"fatigue":"low"}"""),
            new PlannerResponder(script),
            new FixedResponder(CoachAgents.CoachVoice, """{"subject":"Your next two weeks","body":"Build gently: two quality days, a rest day each week."}"""));

    private static async Task<PipelineRunDto> StartRunAsync(ProjectFixture fixture)
    {
        var profile = await ProjectApi.ReadAsync<PipelineArtifactVersionDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{ProfilePath(fixture)}/versions",
                new { content = new { name = "Sam", baselineWeeklyKm = 45 } },
                Ct),
            HttpStatusCode.Created);
        await ProjectApi.ReadAsync<PipelineArtifactVersionDto>(
            await fixture.Owner.Client.PostAsync($"{ProfilePath(fixture)}/versions/{profile.Version}/lock", null, Ct));

        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", Start(), Ct), HttpStatusCode.Created);
        Assert.Equal(CoachArtifacts.AthleteProfile, Assert.Single(run.PinnedArtifacts).Key);
        return run;
    }

    [Fact]
    public async Task FR339_FR341_the_4_63_worked_example_retries_the_plan_twice_with_the_findings_then_escalates()
    {
        var script = new CoachScript(CoachScript.Plan(61, secondHardDay: 2), CoachScript.Plan(58, secondHardDay: 3), CoachScript.Plan(56, secondHardDay: 3));
        await using var app = App(script);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await StartRunAsync(fixture);

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var agentRuns = await AgentJobs.RunsAsync(app, fixture.Project.Id);
        Assert.Equal(1, agentRuns.Count(r => r.PipelineRunId == run.Id && r.Stage == "analyse"));
        Assert.Equal(3, agentRuns.Count(r => r.PipelineRunId == run.Id && r.Stage == "plan"));
        Assert.Equal(0, agentRuns.Count(r => r.PipelineRunId == run.Id && r.Stage == "write"));

        // Attempts 2 and 3 carry the previous attempt's findings as feedback.
        var prompts = script.Prompts.ToArray();
        Assert.Equal(3, prompts.Length);
        Assert.DoesNotContain("R1: week 2 volume", prompts[0], StringComparison.Ordinal);
        Assert.Contains("R1: week 2 volume 61.0 km is +22% over week 1's 50.0 km (max +10%)", prompts[1], StringComparison.Ordinal);
        Assert.Contains("R2: hard sessions on 2026-12-01 and 2026-12-02 are 24 h apart (min 48 h)", prompts[1], StringComparison.Ordinal);
        Assert.Contains("R1: week 2 volume 58.0 km is +16% over week 1's 50.0 km (max +10%)", prompts[2], StringComparison.Ordinal);

        var gates = await GatesAsync(app, fixture.Project.Id, run.Id);
        var escalation = Assert.Single(gates);
        Assert.Equal((EngineGates.Escalation, GateStatus.Pending), (escalation.Type, escalation.Status));
        Assert.Empty(await AgentJobs.ArtifactsAsync(app, fixture.Project.Id, CoachArtifacts.CoachMessage));
        Assert.NotEqual(GenericRunStatus.Completed, (await GetRunAsync(fixture, run.Id)).Status);
    }

    [Fact]
    public async Task FR339_FR341_a_passing_attempt_2_waits_for_plan_approval_then_writes_the_message_and_completes()
    {
        var script = new CoachScript(CoachScript.Plan(61, secondHardDay: 2), CoachScript.Plan(55, secondHardDay: 3));
        await using var app = App(script);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await StartRunAsync(fixture);

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        Assert.Equal(2, script.Prompts.Count);
        var gate = Assert.Single(await GatesAsync(app, fixture.Project.Id, run.Id));
        Assert.Equal((CoachGates.PlanApproval, GateStatus.Pending), (gate.Type, gate.Status));
        Assert.Equal("review", (await GetRunAsync(fixture, run.Id)).CurrentStage);
        Assert.Empty(await AgentJobs.ArtifactsAsync(app, fixture.Project.Id, CoachArtifacts.CoachMessage));

        await ProjectApi.ReadAsync<PipelineGateDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"/api/pipeline-runs/{run.Id}/gates/{gate.Id}/approve", new { comment = "Looks right" }, Ct));
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var message = Assert.Single(await AgentJobs.ArtifactsAsync(app, fixture.Project.Id, CoachArtifacts.CoachMessage));
        Assert.Equal(run.Id, message.PipelineRunId);
        using (var body = JsonDocument.Parse(message.Content))
        {
            Assert.Equal("Your next two weeks", body.RootElement.GetProperty("subject").GetString());
        }

        var done = await GetRunAsync(fixture, run.Id);
        Assert.Equal(GenericRunStatus.Completed, done.Status);
        var agentRuns = await AgentJobs.RunsAsync(app, fixture.Project.Id);
        Assert.Equal(["analyse", "plan", "plan", "write"], agentRuns.Where(r => r.PipelineRunId == run.Id).Select(r => r.Stage).Order(StringComparer.Ordinal));

        // FR-341: the coach run's tokens count against the project like an SDLC agent's.
        var used = await ProjectApi.WithDbAsync(app, db => db.Projects.Where(p => p.Id == fixture.Project.Id).Select(p => p.TokensUsed).SingleAsync(Ct));
        Assert.True(used > 0);
    }

    [Fact]
    public async Task FR341_FR26_a_coach_agent_stage_over_the_project_budget_fails_with_BUDGET_EXCEEDED()
    {
        var script = new CoachScript(CoachScript.Plan(55, secondHardDay: 3));
        await using var app = App(script);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await StartRunAsync(fixture);
        await ProjectApi.WithDbAsync(app, db => db.Projects.Where(p => p.Id == fixture.Project.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TokenBudget, (long?)100_000L).SetProperty(p => p.TokensUsed, 100_000L), Ct));

        var job = Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
        var ex = await Assert.ThrowsAsync<JobFailedException>(() => AgentJobs.RunAsync(app, job));

        Assert.Equal((false, ErrorCodes.BudgetExceeded), (ex.Transient, ex.ErrorCode));
        Assert.Empty(script.Prompts);
        Assert.Equal(0, await ProjectApi.WithDbAsync(app, db => db.AgentRuns.CountAsync(r => r.ProjectId == fixture.Project.Id && r.PipelineRunId == run.Id, Ct)));
    }

    private static Task<List<Engine.Domain.Entities.ApprovalGate>> GatesAsync(WebApplicationFactory<Program> app, Guid projectId, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.ApprovalGates.AsNoTracking().Where(g => g.ProjectId == projectId && g.PipelineRunId == runId).ToListAsync(Ct));

    private static async Task<PipelineRunDto> GetRunAsync(ProjectFixture fixture, Guid runId) =>
        await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Owner.Client.GetAsync($"/api/pipeline-runs/{runId}", Ct));

    /// <summary>A coach agent that always answers with the same JSON.</summary>
    private sealed class FixedResponder(string agent, string json) : IFakeAgentResponder
    {
        public string Agent => agent;

        public string Respond(LlmRequest request) => json;
    }

    /// <summary>The programme planner: the script's plans in order (the last repeats), recording each prompt.</summary>
    private sealed class PlannerResponder(CoachScript script) : IFakeAgentResponder
    {
        public string Agent => CoachAgents.ProgrammePlanner;

        public string Respond(LlmRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var prompt = request.System + "\n" + string.Join("\n", request.Messages.Select(m => m.Content));
            script.Prompts.Enqueue(prompt);
            return script.Plans[Math.Min(script.Prompts.Count, script.Plans.Length) - 1];
        }
    }

    /// <summary>The planner's scripted <c>plan_draft</c> replies (§4.63) and the prompts it saw.</summary>
    private sealed class CoachScript(params string[] plans)
    {
        private static readonly DateOnly Week1Monday = new(2026, 11, 23);

        public string[] Plans { get; } = plans;

        public ConcurrentQueue<string> Prompts { get; } = new();

        /// <summary>
        /// §4.63: week 1 = 50 km (Mon easy 8, Tue hard 10, Thu easy 10, Sat long 22); week 2 (from Mon 2026-11-30) = Mon
        /// easy 8, Tue hard 12, a second hard 12 on <paramref name="secondHardDay"/> (2 = Wed, 3 = Thu), Fri easy 7 and a
        /// Sun long run making <paramref name="week2"/> km. Each week keeps at least one rest day.
        /// </summary>
        public static string Plan(double week2, int secondHardDay)
        {
            var monday2 = Week1Monday.AddDays(7);
            var plan = new
            {
                weeks = new object[]
                {
                    new
                    {
                        week = 1,
                        sessions = new object[]
                        {
                            Session(Week1Monday, "easy run", "easy", 8),
                            Session(Week1Monday.AddDays(1), "intervals", "hard", 10),
                            Session(Week1Monday.AddDays(3), "easy run", "easy", 10),
                            Session(Week1Monday.AddDays(5), "long run", "easy", 22),
                        },
                    },
                    new
                    {
                        week = 2,
                        sessions = new object[]
                        {
                            Session(monday2, "easy run", "easy", 8),
                            Session(monday2.AddDays(1), "intervals", "hard", 12),
                            Session(monday2.AddDays(secondHardDay), "tempo", "hard", 12),
                            Session(monday2.AddDays(4), "easy run", "easy", 7),
                            Session(monday2.AddDays(6), "long run", "easy", week2 - 39),
                        },
                    },
                },
                rationale = "Two weeks building from the last actual week.",
            };
            return JsonSerializer.Serialize(plan);
        }

        private static object Session(DateOnly date, string type, string intensity, double distanceKm) =>
            new { date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), type, intensity, distanceKm };
    }
}
