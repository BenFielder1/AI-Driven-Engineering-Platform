using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Gates;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Review;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.Infrastructure.Agents;
using Aide.IntegrationTests.Artifacts;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Review;

/// <summary>
/// FR-38–FR-40, FR-14 in review, §4.7 end to end with the Fake LLM (a scripted <see cref="FakeCodeReviewResponder"/>) and the
/// Local Git provider: execution completes, the PR job advances to review, the review job runs.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ReviewCycleTests(AideApiFactory factory)
{
    private const string Spec = "# Spec\n\n## Technical Architecture\nNode.js service.\n\n## API Contracts\nNo HTTP API.\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> App(WebApplicationFactory<Program> factory, FakeCodeReviewScript script) =>
        AgentJobs.WithResponders(factory, new FakeCodeReviewResponder(script));

    /// <summary>Execution with a 2-step plan, started and run until the project is in review and cycle 1 has run.</summary>
    private static async Task<ProjectFixture> ReviewedAsync(WebApplicationFactory<Program> app, string spec = Spec, bool reviewSucceeds = true)
    {
        var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Execution, 4, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: "# Todo API\n\n## Summary\nManage todos.");
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, content: spec);
        var plan = await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ImplementationPlan, content: AgentJobs.PlanMarkdown(2));
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.ExecutionSteps.AddRange(Enumerable.Range(1, 2).Select(n => new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = plan.Id,
                StepNumber = n,
                Title = $"Step {n}",
                Task = $"Build part {n}.",
                DeclaredFiles = [new DeclaredFile($"src/part{n}.js", FileAction.Create)],
                Dependencies = n == 1 ? [] : [n - 1],
            }));
            await db.SaveChangesAsync(Ct);
        });
        await ProjectApi.ReadAsync<ExecutionStateDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);
        var jobs = await AgentJobs.RunQueuedAsync(app, id);
        Assert.True(jobs.Any(j => j.Type == JobTypes.Review && (j.Status == JobStatus.Succeeded) == reviewSucceeds), string.Join("; ", jobs.Select(j => $"{j.Type}:{j.Status}:{j.LastError}")));
        return fixture;
    }

    private static Task<List<ComplianceReport>> ReportsAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().Where(r => r.ProjectId == id).OrderBy(r => r.CreatedAt).ToListAsync(Ct));

    private static async Task<T> GetAsync<T>(HttpClient client, string path) =>
        await ProjectApi.ReadAsync<T>(await client.GetAsync(new Uri(path, UriKind.Relative), Ct));

    [Fact]
    public async Task FR38_Entering_review_runs_a_cycle_that_records_a_report_and_an_optimisation_plan_draft()
    {
        var app = App(factory, FakeCodeReviewScript.Default);
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;

        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Review, 1, 5), (project.CurrentPhase, project.ReviewCycle, project.Checkpoint));
        var report = Assert.Single(await ReportsAsync(app, id));
        Assert.Equal((1, 5, 0.860m, ReviewDecision.ReviewRequired, (EscalationReason?)null, 0.90m), (report.ReviewCycle, report.Checkpoint, report.Score, report.Decision, report.EscalationReason, report.Threshold));
        var draft = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan));
        Assert.Equal((ArtifactStatus.Draft, (Guid?)draft.Id, (Guid?)report.AgentRunId), (draft.Status, report.OptimizationPlanArtifactId, draft.AgentRunId));

        var latest = await GetAsync<ComplianceReportDto>(fixture.Viewer.Client, $"{fixture.Path}/compliance/latest");
        Assert.Equal((0.860m, ReviewDecision.ReviewRequired, (Guid?)draft.Id, DriftLevel.None), (latest.OverallScore, latest.Decision, latest.OptimizationPlanArtifactId, latest.DriftLevel));
        Assert.Equal(6, latest.CategoryScores.Count);
        Assert.All(latest.CategoryScores, c => Assert.Equal((0.86m, 0m, 0.86m), (c.AgentScore, c.Penalty, c.Score)));
        Assert.Equal(0.30m, latest.CategoryScores.Single(c => c.Category == ReviewCategory.FeatureCoverage).Weight);
        Assert.All(latest.CategoryScores, c => Assert.NotEmpty(c.Evidence));
        Assert.Equal(FindingSource.Agent, Assert.Single(latest.Violations).Source);

        var trend = await GetAsync<ComplianceTrendDto>(fixture.Viewer.Client, $"{fixture.Path}/compliance/trend");
        Assert.Equal((0.90m, 1, 0.90m), (trend.Threshold, trend.Series.Count, trend.Series[0].Threshold));
        var drift = await GetAsync<ComplianceDriftDto>(fixture.Viewer.Client, $"{fixture.Path}/compliance/drift");
        Assert.Equal((DriftLevel.None, 1, 5), (drift.DriftLevel, drift.Cycle, drift.Checkpoint));

        var started = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReviewStarted)).Payload);
        Assert.Equal((1, "phase_entered"), (started.GetProperty("cycle").GetInt32(), started.GetProperty("trigger").GetString()));
        var completed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReviewCompleted)).Payload);
        Assert.Equal((1, 0.860m, "review_required"), (completed.GetProperty("cycle").GetInt32(), completed.GetProperty("overallScore").GetDecimal(), completed.GetProperty("decision").GetString()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, completed.GetProperty("decisionReason").ValueKind);
        Assert.Equal(EventCategory.Review, EventTypes.CategoryOf(EventTypes.ReviewCompleted));

        var state = await GetAsync<ProjectStateDto>(fixture.Viewer.Client, $"{fixture.Path}/state");
        Assert.Equal((1, 3, ReviewRunStatus.Completed, (decimal?)0.860m), (state.Review.Cycle, state.Review.MaxCycles, state.Review.Status, state.Review.LatestScore));
        Assert.Empty(state.PendingGates);
    }

    [Fact]
    public async Task FR39_A_release_eligible_review_creates_the_pre_release_gate_and_no_optimisation_plan()
    {
        var app = App(factory, new FakeCodeReviewScript { ScoresByCycle = [0.93m] });
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;

        Assert.Equal(ReviewDecision.ReleaseEligible, Assert.Single(await ReportsAsync(app, id)).Decision);
        Assert.Empty(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan));
        var gate = Assert.Single(await LifecycleSeed.GatesAsync(app, id));
        Assert.Equal((GateType.PreRelease, GateStatus.Pending), (gate.Type, gate.Status));

        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve"));
        Assert.Equal(Phase.Release, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);
    }

    [Fact]
    public async Task FR39_A_low_score_escalates_with_its_reason_and_keeps_the_optimisation_plan_for_continue()
    {
        var app = App(factory, new FakeCodeReviewScript { ScoresByCycle = [0.70m] });
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;

        var report = Assert.Single(await ReportsAsync(app, id));
        Assert.Equal((ReviewDecision.EscalationRequired, (EscalationReason?)EscalationReason.LowScore), (report.Decision, report.EscalationReason));
        var gates = await ProjectApi.ReadAsync<List<GateDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/gates", UriKind.Relative), Ct));
        var gate = Assert.Single(gates);
        Assert.Equal((GateType.Escalation, (EscalationReason?)EscalationReason.LowScore), (gate.Type, gate.EscalationReason));
        Assert.NotNull(report.OptimizationPlanArtifactId);
        var completed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReviewCompleted)).Payload);
        Assert.Equal("low_score", completed.GetProperty("decisionReason").GetString());
    }

    [Fact]
    public async Task S4_7_A_spec_endpoint_missing_from_the_code_is_a_major_deterministic_penalty_on_api_contracts()
    {
        var app = App(factory, new FakeCodeReviewScript { ScoresByCycle = [0.95m] });
        using var fixture = await ReviewedAsync(app, Spec.Replace("No HTTP API.", "| GET | /api/widgets |", StringComparison.Ordinal));

        var latest = await GetAsync<ComplianceReportDto>(fixture.Viewer.Client, $"{fixture.Path}/compliance/latest");
        var api = latest.CategoryScores.Single(c => c.Category == ReviewCategory.ApiContracts);
        Assert.Equal((0.95m, 0.10m, 0.85m), (api.AgentScore, api.Penalty, api.Score));
        Assert.Equal(0.930m, latest.OverallScore); // 0.95 − 0.10 × 0.20
        var violation = Assert.Single(latest.Violations, v => v.Source == FindingSource.Deterministic);
        Assert.Equal((ReviewCategory.ApiContracts, Severity.Major), (violation.Category, violation.Severity));
        Assert.Contains("/api/widgets", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR40_Approving_an_optimisation_plan_with_3_steps_runs_them_and_review_cycle_2_starts_automatically()
    {
        var app = App(factory, new FakeCodeReviewScript { ScoresByCycle = [0.82m, 0.95m], OptimizationStepsByCycle = [3, 0] });
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;
        var draft = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan));

        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve"));

        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Execution, 5), (project.CurrentPhase, project.Checkpoint)); // FR-39: re-entered, version kept
        var current = await GetAsync<ExecutionStepsDto>(fixture.Viewer.Client, $"{fixture.Path}/execution/steps");
        Assert.Equal((ArtifactType.OptimizationPlan, 1, 3), (current.Plan!.Type, current.Plan.Version, current.Steps.Count));

        await AgentJobs.RunQueuedAsync(app, id);

        project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Review, 2, 6), (project.CurrentPhase, project.ReviewCycle, project.Checkpoint));
        var steps = await GetAsync<ExecutionStepsDto>(fixture.Viewer.Client, $"{fixture.Path}/execution/steps");
        Assert.All(steps.Steps, s => Assert.Equal(StepStatus.Completed, s.Status));
        var reports = await ReportsAsync(app, id);
        Assert.Equal([(1, ReviewDecision.ReviewRequired), (2, ReviewDecision.ReleaseEligible)], reports.Select(r => (r.ReviewCycle, r.Decision)));
        var trend = await GetAsync<ComplianceTrendDto>(fixture.Viewer.Client, $"{fixture.Path}/compliance/trend");
        Assert.Equal([0.820m, 0.950m], trend.Series.Select(p => p.OverallScore));
        Assert.Equal(2, (await LifecycleSeed.EventsAsync(app, id, EventTypes.PullRequestOpened)).Count); // one per plan, same branch
        Assert.Equal(GateType.PreRelease, Assert.Single(await LifecycleSeed.GatesAsync(app, id), g => g.Status == GateStatus.Pending).Type);

        var implementation = await GetAsync<ExecutionStepsDto>(fixture.Viewer.Client, $"{fixture.Path}/execution/steps?planType=implementation_plan");
        Assert.Equal(ArtifactType.ImplementationPlan, implementation.Plan!.Type);
    }

    [Fact]
    public async Task FR39_A_second_cycle_that_does_not_improve_escalates_no_improvement()
    {
        var app = App(factory, new FakeCodeReviewScript { ScoresByCycle = [0.84m, 0.83m], OptimizationStepsByCycle = [1] });
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;
        var draft = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan));

        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve"));
        await AgentJobs.RunQueuedAsync(app, id);

        var last = (await ReportsAsync(app, id))[^1];
        Assert.Equal((2, ReviewDecision.EscalationRequired, (EscalationReason?)EscalationReason.NoImprovement), (last.ReviewCycle, last.Decision, last.EscalationReason));
    }

    [Fact]
    public async Task FR14_Continue_in_review_allows_one_more_cycle_and_approving_the_plan_re_enters_execution()
    {
        var app = App(factory, new FakeCodeReviewScript { ScoresByCycle = [0.70m] });
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;
        var gate = Assert.Single(await LifecycleSeed.GatesAsync(app, id));

        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "continue" }));
        Assert.Equal((Phase.Review, 1), ((await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase, (await LifecycleSeed.ProjectAsync(app, id)).ExtraReviewCycles));

        var draft = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan));
        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve"));

        Assert.Equal(Phase.Execution, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);
        Assert.Contains(await LifecycleSeed.JobsAsync(app, id), j => j.Type == JobTypes.Execution && j.Status == JobStatus.Queued);
    }

    [Fact]
    public async Task FR14_Continue_without_an_optimisation_plan_queues_another_review_cycle()
    {
        var app = App(factory, new FakeCodeReviewScript { ScoresByCycle = [0.70m, 0.93m], OptimizationStepsByCycle = [0] });
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;
        var gate = Assert.Single(await LifecycleSeed.GatesAsync(app, id));

        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "continue" }));
        await AgentJobs.RunQueuedAsync(app, id);

        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((2, 1), (project.ReviewCycle, project.ExtraReviewCycles));
        Assert.Equal(ReviewDecision.ReleaseEligible, (await ReportsAsync(app, id))[^1].Decision);
    }

    [Fact]
    public async Task FR14_Accept_in_review_makes_the_project_release_eligible_through_the_pre_release_gate()
    {
        var app = App(factory, new FakeCodeReviewScript { ScoresByCycle = [0.70m] });
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;
        var gate = Assert.Single(await LifecycleSeed.GatesAsync(app, id));

        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "accept", comment = "Good enough" }));
        var preRelease = Assert.Single(await LifecycleSeed.GatesAsync(app, id), g => g.Status == GateStatus.Pending);
        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, preRelease.Id, "approve"));

        Assert.Equal(Phase.Release, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);
    }

    [Fact]
    public async Task FR38_POST_review_queues_a_cycle_REVIEW_IN_PROGRESS_while_one_is_queued_and_supersedes_the_old_decision_gate()
    {
        var app = App(factory, new FakeCodeReviewScript { ScoresByCycle = [0.93m, 0.95m] });
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;
        var oldGate = Assert.Single(await LifecycleSeed.GatesAsync(app, id));

        var started = await ProjectApi.ReadAsync<ReviewStartedDto>(await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/review", new { }, Ct), HttpStatusCode.Accepted);
        Assert.Equal(2, started.Cycle);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/review", new { }, Ct), HttpStatusCode.Conflict, ErrorCodes.ReviewInProgress);
        Assert.Equal(ReviewRunStatus.Running, (await GetAsync<ProjectStateDto>(fixture.Viewer.Client, $"{fixture.Path}/state")).Review.Status);
        await Problems.AssertAsync(await fixture.Viewer.Client.PostAsJsonAsync($"{fixture.Path}/review", new { }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Outsider.Client.PostAsJsonAsync($"{fixture.Path}/review", new { }, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);

        await AgentJobs.RunQueuedAsync(app, id);

        var gates = await LifecycleSeed.GatesAsync(app, id);
        Assert.Equal(GateStatus.Cancelled, gates.Single(g => g.Id == oldGate.Id).Status);
        Assert.Single(gates, g => g.Status == GateStatus.Pending && g.Type == GateType.PreRelease);
        Assert.Equal([1, 2], (await ReportsAsync(app, id)).Select(r => r.ReviewCycle));
        Assert.Equal(started.JobId, (await LifecycleSeed.JobsAsync(app, id)).Last(j => j.Type == JobTypes.Review).Id);
    }

    [Fact]
    public async Task FR38_POST_review_outside_the_review_phase_is_WRONG_PHASE()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/review", new { }, Ct), HttpStatusCode.Conflict, ErrorCodes.WrongPhase);
        await Problems.AssertAsync(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/compliance/latest", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/compliance/drift", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Empty((await GetAsync<ComplianceTrendDto>(fixture.Viewer.Client, $"{fixture.Path}/compliance/trend")).Series);
        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(new Uri($"{fixture.Path}/compliance/trend", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR38_A_redelivered_review_job_records_one_report_one_cycle_and_calls_the_provider_once()
    {
        var counting = new CountingResponder(new FakeCodeReviewResponder(FakeCodeReviewScript.Default));
        var app = AgentJobs.WithResponders(factory, counting);
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;
        var job = Assert.Single(await LifecycleSeed.JobsAsync(app, id), j => j.Type == JobTypes.Review);

        await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued), Ct));
        await AgentJobs.RunAsync(app, await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct)));

        Assert.Single(await ReportsAsync(app, id));
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReviewStarted));
        Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan));
        Assert.Equal(1, (await LifecycleSeed.ProjectAsync(app, id)).ReviewCycle);
        Assert.Equal(1, counting.Calls);
    }

    [Fact]
    public async Task FR38_An_invalid_reply_is_re_run_once_with_the_reasons_as_feedback()
    {
        var valid = new FakeCodeReviewResponder(FakeCodeReviewScript.Default).Respond(new LlmRequest
        {
            Model = "fake-1",
            System = "s",
            Messages = [new LlmMessage(LlmRole.User, "<review_cycle>1</review_cycle>")],
            MaxTokens = 10,
            Agent = AgentKind.CodeReview,
        });
        var scripted = new ScriptedResponder(AgentKind.CodeReview, """{"categoryScores": []}""", valid);
        var app = AgentJobs.WithResponders(factory, scripted);
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;

        Assert.Single(await ReportsAsync(app, id));
        Assert.Equal(2, scripted.Prompts.Count);
        Assert.Contains("INVALID_OUTPUT", scripted.Prompts[1], StringComparison.Ordinal);
        Assert.Equal(2, (await AgentJobs.RunsAsync(app, id)).Count(r => r.Agent == AgentKind.CodeReview));
    }

    [Fact]
    public async Task FR38_A_review_that_stays_invalid_fails_records_REVIEW_FAILED_for_its_cycle_and_shows_failed()
    {
        var app = App(factory, new FakeCodeReviewScript { RawReply = "not a review" });
        using var fixture = await ReviewedAsync(app, reviewSucceeds: false);
        var id = fixture.Project.Id;

        Assert.Empty(await ReportsAsync(app, id));
        Assert.Equal(JobStatus.Failed, Assert.Single(await LifecycleSeed.JobsAsync(app, id), j => j.Type == JobTypes.Review).Status);

        // The test runner doesn't run the worker's ended hooks; run the hook as the worker would.
        var job = Assert.Single(await LifecycleSeed.JobsAsync(app, id), j => j.Type == JobTypes.Review);
        await ProjectApi.WithDbAsync(app, async db =>
        {
            await new ReviewJobEndedHook(TimeProvider.System).OnJobEndedAsync(
                db, new Aide.Api.Jobs.JobEnded(job.Id, job.Type, id, DeadLettered: false, "INVALID_OUTPUT: The review is invalid: sk-ant-api03-SECRETSECRETSECRETSECRET"), Ct);
            await db.SaveChangesAsync(Ct);
        });

        var failed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReviewFailed)).Payload);
        Assert.Equal(1, failed.GetProperty("cycle").GetInt32());
        Assert.DoesNotContain("SECRETSECRET", failed.GetProperty("reason").GetString(), StringComparison.Ordinal);
        var state = await GetAsync<ProjectStateDto>(fixture.Viewer.Client, $"{fixture.Path}/state");
        Assert.Equal((1, ReviewRunStatus.Failed), (state.Review.Cycle, state.Review.Status));
    }

    [Fact]
    public async Task FR17_Rejecting_the_optimisation_plan_revises_it_for_the_same_cycle()
    {
        var app = App(factory, FakeCodeReviewScript.Default);
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;
        var draft = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan));

        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "reject", "Split it"));
        await AgentJobs.RunQueuedAsync(app, id);

        var report = Assert.Single(await ReportsAsync(app, id));
        var versions = await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan);
        Assert.Equal([ArtifactStatus.Rejected, ArtifactStatus.Draft], versions.Select(a => a.Status));
        Assert.Equal(versions[1].Id, report.OptimizationPlanArtifactId);
        Assert.Equal(1, (await LifecycleSeed.ProjectAsync(app, id)).ReviewCycle);
    }

    [Fact]
    public async Task S4_8_Reports_above_the_current_version_are_hidden_after_a_rollback()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 5, ApprovalMode.Advisory, reviewCycle: 1);
        await LifecycleSeed.AddReportAsync(factory, id, 5, ReviewDecision.ReviewRequired);
        await LifecycleSeed.AddReportAsync(factory, id, 6, ReviewDecision.ReleaseEligible);

        var latest = await GetAsync<ComplianceReportDto>(fixture.Viewer.Client, $"{fixture.Path}/compliance/latest");
        var trend = await GetAsync<ComplianceTrendDto>(fixture.Viewer.Client, $"{fixture.Path}/compliance/trend");

        Assert.Equal((5, ReviewDecision.ReviewRequired), (latest.Checkpoint, latest.Decision));
        Assert.Equal([5], trend.Series.Select(p => p.Checkpoint));
    }

    // ---------- FR-14 continue at max cycles after an executed optimisation plan ----------

    /// <summary>
    /// Cycles 1 and 2 are review_required (0.82, 0.84); each plan is approved and run. Cycle 3 (0.86) escalates max_cycles
    /// with a new optimisation plan draft (v3); the plan of cycle 2 (v2) is locked with every step completed.
    /// </summary>
    private static async Task<ProjectFixture> EscalatedAtMaxCyclesAsync(WebApplicationFactory<Program> app)
    {
        var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;
        for (var cycle = 1; cycle <= 2; cycle++)
        {
            var draft = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan), a => a.Status == ArtifactStatus.Draft);
            await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve"));
            await AgentJobs.RunQueuedAsync(app, id);
        }

        var last = (await ReportsAsync(app, id))[^1];
        Assert.Equal((3, ReviewDecision.EscalationRequired, (EscalationReason?)EscalationReason.MaxCycles), (last.ReviewCycle, last.Decision, last.EscalationReason));
        var plans = (await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan)).OrderBy(p => p.Version).ToList();
        Assert.Equal((3, ArtifactStatus.Locked, ArtifactStatus.Draft), (plans.Count, plans[1].Status, plans[2].Status));
        return fixture;
    }

    private static WebApplicationFactory<Program> MaxCyclesApp(WebApplicationFactory<Program> factory) =>
        App(factory, new FakeCodeReviewScript { ScoresByCycle = [0.82m, 0.84m, 0.86m, 0.88m], OptimizationStepsByCycle = [1, 1, 1, 1] });

    [Fact]
    public async Task FR14_Continue_at_cycle_3_after_an_executed_plan_waits_for_the_new_draft_then_runs_its_steps_and_reviews_cycle_4()
    {
        var app = MaxCyclesApp(factory);
        using var fixture = await EscalatedAtMaxCyclesAsync(app);
        var id = fixture.Project.Id;
        var gate = Assert.Single(await LifecycleSeed.GatesAsync(app, id), g => g.Status == GateStatus.Pending);

        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "continue" }));

        // The executed plan of cycle 2 doesn't count: the project waits in review for the cycle 3 draft, with no job.
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Review, 3, 1), (project.CurrentPhase, project.ReviewCycle, project.ExtraReviewCycles));
        Assert.DoesNotContain(await LifecycleSeed.JobsAsync(app, id), j => j.Status == JobStatus.Queued);

        var draft = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan), a => a.Status == ArtifactStatus.Draft);
        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve"));
        Assert.Equal(Phase.Execution, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);
        await AgentJobs.RunQueuedAsync(app, id);

        var steps = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().Where(s => s.PlanArtifactId == draft.Id).ToListAsync(Ct));
        Assert.All(steps, s => Assert.Equal(StepStatus.Completed, s.Status));
        Assert.NotEmpty(steps);
        project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Review, 4), (project.CurrentPhase, project.ReviewCycle));
        Assert.Equal(4, (await ReportsAsync(app, id))[^1].ReviewCycle);
    }

    [Fact]
    public async Task FR14_Continue_at_cycle_3_with_the_new_draft_already_approved_re_enters_execution_for_its_steps()
    {
        var app = MaxCyclesApp(factory);
        using var fixture = await EscalatedAtMaxCyclesAsync(app);
        var id = fixture.Project.Id;
        var draft = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan), a => a.Status == ArtifactStatus.Draft);

        // Approved before the escalation is decided: no review_required decision and no continue yet, so it waits in review.
        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve"));
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);

        var gate = Assert.Single(await LifecycleSeed.GatesAsync(app, id), g => g.Status == GateStatus.Pending);
        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "continue" }));

        Assert.Equal(Phase.Execution, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);
        Assert.Contains(await LifecycleSeed.JobsAsync(app, id), j => j.Type == JobTypes.Execution && j.Status == JobStatus.Queued);
        await AgentJobs.RunQueuedAsync(app, id);

        var steps = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().Where(s => s.PlanArtifactId == draft.Id).ToListAsync(Ct));
        Assert.All(steps, s => Assert.Equal(StepStatus.Completed, s.Status));
        Assert.Equal((Phase.Review, 4), ((await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase, (await LifecycleSeed.ProjectAsync(app, id)).ReviewCycle));
    }

    [Fact]
    public async Task FR36_An_execution_job_with_every_step_of_the_current_plan_completed_runs_the_completion_and_returns_to_review()
    {
        var app = App(factory, new FakeCodeReviewScript { ScoresByCycle = [0.93m] });
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Execution, 5, ApprovalMode.Advisory, reviewCycle: 1);
        await ProjectApi.WithDbAsync(app, db => db.ApprovalGates.Where(g => g.ProjectId == id && g.Status == GateStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GateStatus.Cancelled), Ct));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Aide.Infrastructure.Persistence.AideDbContext>();
            await scope.ServiceProvider.GetRequiredService<Aide.Api.Jobs.JobEnqueuer>()
                .EnqueueAsync(JobTypes.Execution, new ExecutionJobPayload(id, ExecutionJobPayload.ReviewCycle), id, Ct);
            await db.SaveChangesAsync(Ct);
        }

        var jobs = await AgentJobs.RunQueuedAsync(app, id);

        Assert.Equal(2, jobs.Count(j => j.Type == JobTypes.PullRequest && j.Status == JobStatus.Succeeded));
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Review, 6), (project.CurrentPhase, project.Checkpoint));
    }

    // ---------- review_required without an optimisation plan ----------

    private static string ReviewReply(decimal score, int steps) =>
        new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [score], OptimizationStepsByCycle = [steps] }).Respond(new LlmRequest
        {
            Model = "fake-1",
            System = "s",
            Messages = [new LlmMessage(LlmRole.User, "<review_cycle>1</review_cycle>")],
            MaxTokens = 10,
            Agent = AgentKind.CodeReview,
        });

    [Fact]
    public async Task FR38_A_review_required_reply_without_optimisation_steps_is_re_run_with_the_reason_and_the_fixed_reply_drafts_the_plan()
    {
        var scripted = new ScriptedResponder(AgentKind.CodeReview, ReviewReply(0.82m, 0), ReviewReply(0.82m, 2));
        var app = AgentJobs.WithResponders(factory, scripted);
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;

        Assert.Equal(2, scripted.Prompts.Count);
        Assert.Contains("\"optimizationPlan\" has no steps", scripted.Prompts[1], StringComparison.Ordinal);
        var report = Assert.Single(await ReportsAsync(app, id));
        Assert.Equal(ReviewDecision.ReviewRequired, report.Decision);
        var draft = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan));
        Assert.Equal(draft.Id, report.OptimizationPlanArtifactId);
    }

    [Fact]
    public async Task FR39_A_review_required_reply_still_without_optimisation_steps_escalates_no_optimization_plan()
    {
        var scripted = new ScriptedResponder(AgentKind.CodeReview, ReviewReply(0.82m, 0));
        var app = AgentJobs.WithResponders(factory, scripted);
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;

        Assert.Equal(2, scripted.Prompts.Count);
        var report = Assert.Single(await ReportsAsync(app, id));
        Assert.Equal((ReviewDecision.EscalationRequired, (EscalationReason?)EscalationReason.NoOptimizationPlan, (Guid?)null), (report.Decision, report.EscalationReason, report.OptimizationPlanArtifactId));
        var gates = await ProjectApi.ReadAsync<List<GateDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/gates", UriKind.Relative), Ct));
        var gate = Assert.Single(gates);
        Assert.Equal((GateType.Escalation, (EscalationReason?)EscalationReason.NoOptimizationPlan), (gate.Type, gate.EscalationReason));
        var latest = await GetAsync<ComplianceReportDto>(fixture.Viewer.Client, $"{fixture.Path}/compliance/latest");
        Assert.Equal(EscalationReason.NoOptimizationPlan, latest.DecisionReason);

        // Continue: there is no plan to run, so the extra cycle is a new review.
        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "continue" }));
        Assert.Contains(await LifecycleSeed.JobsAsync(app, id), j => j.Type == JobTypes.Review && j.Status == JobStatus.Queued);
    }

    // ---------- §4.8 review timeline ----------

    [Fact]
    public async Task S4_8_After_rolling_back_to_v3_and_returning_to_v5_the_pre_rollback_v5_reports_stay_hidden()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var start = DateTimeOffset.UtcNow.AddHours(-3);
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 5, ApprovalMode.Advisory, reviewCycle: 1);
        await LifecycleSeed.AddReportAsync(factory, id, 5, ReviewDecision.EscalationRequired, EscalationReason.LowScore, createdAt: start);

        // Rolled back to v3 an hour later, then the project climbed back to v5 (review, cycle 0 → nothing reviewed yet).
        await ProjectApi.WithDbAsync(factory, db => db.Projects.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastRollbackAt, start.AddHours(1)).SetProperty(p => p.ReviewCycle, 0), Ct));

        await Problems.AssertAsync(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/compliance/latest", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/compliance/drift", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Empty((await GetAsync<ComplianceTrendDto>(fixture.Viewer.Client, $"{fixture.Path}/compliance/trend")).Series);
        var state = await GetAsync<ProjectStateDto>(fixture.Viewer.Client, $"{fixture.Path}/state");
        Assert.Equal(((decimal?)null, (ReviewDecision?)null), (state.Review.LatestScore, state.Review.LatestDecision));

        var after = await LifecycleSeed.AddReportAsync(factory, id, 5, ReviewDecision.ReleaseEligible, createdAt: start.AddHours(2));
        var latest = await GetAsync<ComplianceReportDto>(fixture.Viewer.Client, $"{fixture.Path}/compliance/latest");
        Assert.Equal((after.Id, ReviewDecision.ReleaseEligible), (latest.Id, latest.Decision));
        var trend = await GetAsync<ComplianceTrendDto>(fixture.Viewer.Client, $"{fixture.Path}/compliance/trend");
        Assert.Equal([ReviewDecision.ReleaseEligible], trend.Series.Select(p => p.Decision));
    }

    // ---------- NFR-SEC: no secret in the review prompt ----------

    [Fact]
    public async Task FR38_A_secret_on_the_branch_is_flagged_but_redacted_from_the_prompt_the_provider_receives()
    {
        var recording = new RecordingResponder(new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));
        var app = AgentJobs.WithResponders(factory, recording);
        using var fixture = await ReviewedAsync(app);
        var id = fixture.Project.Id;
        var awsKey = "AKIA" + "Z7Q3EXAMPLE4K2LM";
        var branch = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == id).Select(v => v.BranchName!).SingleAsync(Ct));
        var stepId = Guid.NewGuid();
        await app.Services.GetRequiredService<Aide.Infrastructure.Git.IWorkingCopies>().CommitStepAsync(
            id, branch, stepId, [new ProducedFile("src/config.js", $"const region = 'eu';\nconst key = '{awsKey}';\n")], CodeGeneration.CommitMessage(9, "Config", ["src/config.js"], stepId), Ct);

        await ProjectApi.ReadAsync<ReviewStartedDto>(await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/review", new { }, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(app, id);

        var prompt = recording.Prompts[^1];
        Assert.Contains("<review_cycle>\n2\n</review_cycle>", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(awsKey, prompt, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:aws_access_key_id]", prompt, StringComparison.Ordinal);
        var latest = await GetAsync<ComplianceReportDto>(fixture.Viewer.Client, $"{fixture.Path}/compliance/latest");
        var secret = Assert.Single(latest.Violations, v => v.Source == FindingSource.Deterministic && v.Category == ReviewCategory.Security);
        Assert.Equal((Severity.Critical, "src/config.js"), (secret.Severity, secret.File));
    }

    /// <summary>Wraps a responder and records the user message of every request it answers.</summary>
    private sealed class RecordingResponder(IFakeAgentResponder inner) : IFakeAgentResponder
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _prompts = new();

        public AgentKind Agent => inner.Agent;

        public IReadOnlyList<string> Prompts => [.. _prompts];

        public string Respond(LlmRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            _prompts.Enqueue(request.Messages[0].Content);
            return inner.Respond(request);
        }
    }
}
