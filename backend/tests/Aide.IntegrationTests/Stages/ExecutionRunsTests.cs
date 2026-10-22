using System.Net;
using System.Text.Json;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Review;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;
using Aide.Domain.RepoTools;
using Aide.Domain.Review;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Stages;

/// <summary>
/// FR-57 execution runs and their attempt history (D-V2-14), FR-56 review cycles, and the §8 performance bound on both
/// endpoints (10 cycles × 30 steps).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionRunsTests(AideApiFactory factory)
{
    private const long TokensPerStep = 150;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- FR-57 ----------

    [Fact]
    public async Task FR57_A_development_run_of_8_steps_and_review_cycles_with_3_and_2_fix_steps_list_three_runs_with_attempt_history()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 20, ApprovalMode.Advisory, reviewCycle: 2);
        var seeded = await SeedRunsAsync(id, 8, 3, 2);

        var runs = await RunsAsync(fixture.Viewer.Client, $"{fixture.Path}/execution/runs");

        Assert.Equal(
            [(ArtifactType.ImplementationPlan, 1, (int?)null, 8), (ArtifactType.OptimizationPlan, 1, 1, 3), (ArtifactType.OptimizationPlan, 2, 2, 2)],
            runs.Select(r => (r.PlanType, r.PlanVersion, r.Cycle, r.StepCount)));
        Assert.Equal([seeded.Development.Id, seeded.FixPlans[0].Id, seeded.FixPlans[1].Id], runs.Select(r => r.Id));
        Assert.All(runs, r => Assert.Equal((PlanRunStatus.Completed, false, (string?)null, r.StepCount, r.StepCount), (r.Status, r.Superseded, r.SupersededReason, r.CompletedCount, r.CommitCount)));
        Assert.All(runs, r => Assert.Equal(r.StepCount * TokensPerStep, r.Tokens));
        Assert.All(runs, r => Assert.Equal(fixture.Project.ReleaseVersionId!.Value, r.ReleaseVersionId));
        Assert.All(runs, r => Assert.True(r.StartedAt <= r.CompletedAt));
        var step = runs[1].Steps[0];
        Assert.Equal((1, "Fix 1.1", 1, "abc-fix1-1"), (step.Number, step.Title, step.Attempt, step.CommitSha));
        Assert.Equal(["src/fix1-1.js"], step.DeclaredFiles.Select(f => f.Path));
        Assert.Equal(["src/fix1-1.js"], step.ProducedFiles.Select(f => f.Path));
        Assert.Null(step.CommitUrl);

        foreach (var s in runs.SelectMany(r => r.Steps))
        {
            var attempts = await AttemptsAsync(fixture.Viewer.Client, id, s.Id);
            var attempt = Assert.Single(attempts);
            Assert.Equal((1, StepAttemptOutcome.Accepted), (attempt.Attempt, attempt.Outcome));
            Assert.Equal((AgentKind.CodeGenerator, "fake-1", 100, 50), (attempt.Run!.Agent, attempt.Run.Model, attempt.Run.InputTokens, attempt.Run.OutputTokens));
        }
    }

    [Fact]
    public async Task FR57_The_run_of_an_invalidated_plan_is_listed_as_superseded_with_its_reason()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Execution, 9, ApprovalMode.Advisory);
        var old = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, ArtifactStatus.Invalidated, version: 1);
        await LifecycleSeed.AddStepsAsync(factory, id, old.Id, 2, StepStatus.Invalidated);
        var current = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, version: 2);
        await LifecycleSeed.AddStepsAsync(factory, id, current.Id, 3);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            db.AddEvent(
                EventTypes.ArtifactInvalidated,
                id,
                new { artifactId = old.Id, type = old.Type, version = 1, cause = "cascade", approvedType = ArtifactType.TechnicalSpecification, approvedVersion = 4 },
                "seed",
                DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        });

        var runs = await RunsAsync(fixture.Viewer.Client, $"{fixture.Path}/execution/runs");

        Assert.Equal([(1, PlanRunStatus.Superseded, true), (2, PlanRunStatus.Pending, false)], runs.Select(r => (r.PlanVersion, r.Status, r.Superseded)));
        Assert.Equal("implementation_plan v1 was invalidated when technical_specification v4 was approved.", runs[0].SupersededReason);
        Assert.Equal((0, (DateTimeOffset?)null), (runs[1].CompletedCount, runs[1].StartedAt));
    }

    [Fact]
    public async Task FR57_Runs_are_for_members_scoped_by_release_version()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        Assert.Empty(await RunsAsync(fixture.Viewer.Client, $"{fixture.Path}/execution/runs"));
        Assert.Empty(await RunsAsync(fixture.Viewer.Client, $"{fixture.Path}/execution/runs?releaseVersionId={fixture.Project.ReleaseVersionId}"));
        await Problems.AssertAsync(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/execution/runs?releaseVersionId={Guid.NewGuid()}"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await GetAsync(fixture.Outsider.Client, $"{fixture.Path}/execution/runs"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR57_Attempts_of_an_unknown_step_or_another_projects_step_are_NOT_FOUND()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        using var other = await ProjectApi.FixtureAsync(factory);
        var plan = await LifecycleSeed.AddArtifactAsync(factory, other.Project.Id, ArtifactType.ImplementationPlan);
        var steps = await LifecycleSeed.AddStepsAsync(factory, other.Project.Id, plan.Id, 1);

        await Problems.AssertAsync(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/execution/steps/{Guid.NewGuid()}/attempts"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/execution/steps/{steps[0].Id}/attempts"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Empty(await AttemptsAsync(other.Viewer.Client, other.Project.Id, steps[0].Id));
    }

    [Fact]
    public async Task D_V2_14_The_step_runner_records_each_attempt_with_its_run_outcome_violations_and_produced_files()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Execution, 4, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, content: "# Todo API\n\n## Summary\nManage todos.");
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, content: "# Spec\n\nExpress API.");
        PlanStep[] plan = [new(1, "Step 1", "Build part 1.", [new DeclaredFile("src/part1.js", FileAction.Create)], [])];
        var planArtifact = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, content: PlanDocument.Render(ArtifactType.ImplementationPlan, plan));
        var step = (await ProjectApi.WithDbAsync(factory, async db =>
        {
            var s = new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = planArtifact.Id,
                StepNumber = 1,
                Title = "Step 1",
                Task = "Build part 1.",
                DeclaredFiles = plan[0].Files,
                Dependencies = [],
            };
            db.ExecutionSteps.Add(s);
            await db.SaveChangesAsync(Ct);
            return s;
        })).Id;
        var start = await fixture.Contributor.Client.PostAsync(new Uri($"{fixture.Path}/execution/start", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var app = AgentJobs.WithResponders(
            factory,
            new ScriptedResponder(AgentKind.CodeGenerator, Reply(1, ("src/undeclared.js", "x")), Reply(1, ("src/part1.js", "export const one = 1;\n"))));

        await AgentJobs.RunQueuedAsync(app, id);

        var attempts = await AttemptsAsync(fixture.Viewer.Client, id, step);
        Assert.Equal([(1, StepAttemptOutcome.Rejected), (2, StepAttemptOutcome.Accepted)], attempts.Select(a => (a.Attempt, a.Outcome)));
        Assert.Contains(attempts[0].Violations, v => v.Code == ErrorCodes.Guardian.PlanAlignment && v.Severity == ViolationSeverity.Error);
        Assert.Empty(attempts[0].ProducedFiles);
        Assert.Empty(attempts[1].Violations);
        Assert.Equal(["src/part1.js"], attempts[1].ProducedFiles.Select(f => f.Path));
        Assert.All(attempts, a => Assert.Equal((AgentKind.CodeGenerator, AgentRunStatus.Succeeded), (a.Run!.Agent, a.Run.Status)));
        Assert.NotEqual(attempts[0].Run!.Id, attempts[1].Run!.Id);
        // FR-71: each attempt's run used the repo tools on the project branch (the Fake's default script).
        Assert.All(attempts, a => Assert.Equal([RepoToolNames.ListFiles, RepoToolNames.ReadFile], a.ToolCalls.Select(c => c.Tool)));
        var runs = (await AgentJobs.RunsAsync(factory, id)).Where(r => r.Agent == AgentKind.CodeGenerator).ToList();
        Assert.Equal(runs.Select(r => r.Id).Order(), attempts.Select(a => a.Run!.Id).Order());

        var listed = Assert.Single(await RunsAsync(fixture.Viewer.Client, $"{fixture.Path}/execution/runs"));
        Assert.Equal((PlanRunStatus.Completed, 2, 1), (listed.Status, listed.Steps[0].Attempt, listed.CommitCount));
        Assert.Equal(runs.Sum(r => (long)r.InputTokens + r.OutputTokens), listed.Tokens);
    }

    [Fact]
    public void FR71_Tool_calls_are_read_from_the_runs_tool_call_array()
    {
        var calls = ExecutionRunReader.ToolCalls("""[{"tool":"read_file","arguments":{"path":"a.js"},"bytes":12,"truncated":true},{"nope":1},"x"]""");

        var call = Assert.Single(calls);
        Assert.Equal(("read_file", 12, true, "a.js"), (call.Tool, call.Bytes, call.Truncated, call.Arguments.GetProperty("path").GetString()));
        Assert.Empty(ExecutionRunReader.ToolCalls("[]"));
        Assert.Empty(ExecutionRunReader.ToolCalls("not json"));
    }

    // ---------- FR-56 ----------

    [Fact]
    public async Task FR56_Review_cycles_are_listed_newest_first_and_each_cycle_has_its_full_report_plan_and_run()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 20, ApprovalMode.Advisory, reviewCycle: 3);
        var seeded = await SeedRunsAsync(id, 2, 1, 1);
        var third = await SeedReportAsync(id, 3, DateTimeOffset.UtcNow, ReviewDecision.EscalationRequired, EscalationReason.MaxCycles);

        var list = await ProjectApi.ReadAsync<List<ComplianceReportSummaryDto>>(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/compliance/reports"));
        var cycle1 = await ProjectApi.ReadAsync<ComplianceReportDetailDto>(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/compliance/reports/1"));
        var cycle3 = await ProjectApi.ReadAsync<ComplianceReportDetailDto>(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/compliance/reports/3?releaseVersionId={fixture.Project.ReleaseVersionId}"));

        Assert.Equal([3, 2, 1], list.Select(r => r.Cycle));
        Assert.Equal((0.700m, 0.850m, DriftLevel.Low, ReviewDecision.ReviewRequired, 6), (list[2].Score, list[2].Threshold, list[2].Drift, list[2].Decision, list[2].Checkpoint));

        Assert.Equal((seeded.Reports[0].Id, 1, 0.700m), (cycle1.Id, cycle1.Cycle, cycle1.OverallScore));
        Assert.Equal([(ReviewCategory.Security, 0.9m, 0.1m, 0.8m)], cycle1.CategoryScores.Select(c => (c.Category, c.AgentScore, c.Penalty, c.Score)));
        Assert.Equal([FindingSource.Deterministic, FindingSource.Agent], cycle1.Violations.Select(v => v.Source));
        Assert.Equal(["Secret in config"], cycle1.DeterministicFindings.Select(v => v.Message));
        Assert.Equal("const key = ...", cycle1.Violations[0].Evidence);
        Assert.Equal(["CSV export"], cycle1.MissingFeatures);
        Assert.Equal(["auth"], cycle1.DriftAreas.Select(d => d.Area));
        Assert.Equal([("FR-3", ReviewCategory.FeatureCoverage)], cycle1.Regressions.Select(r => (r.RequirementId, r.Category)));
        Assert.Equal(
            [new ExternalCheckDto("ci / test", CheckRunConclusion.Failure, true, "https://github.com/acme/widgets/runs/1")],
            cycle1.ExternalChecks!.Checks);
        Assert.Equal((90, false), (cycle1.ExternalChecks.ChecksWaitedSeconds, cycle1.ExternalChecks.ChecksTimedOut));
        Assert.Null(cycle1.ExternalChecks.Preview);
        Assert.Equal(new ReportPlanDto(seeded.FixPlans[0].Id, 1, ArtifactStatus.Locked), cycle1.OptimizationPlan);
        Assert.Equal(seeded.FixPlans[0].Id, cycle1.ExecutionRunId);
        Assert.Null(cycle1.EscalationReason);

        Assert.Equal((third.Id, EscalationReason.MaxCycles), (cycle3.Id, cycle3.EscalationReason));
        Assert.Null(cycle3.OptimizationPlan);
        Assert.Null(cycle3.ExecutionRunId);
        Assert.Null(cycle3.ExternalChecks);
        Assert.Empty(cycle3.Regressions);
    }

    [Fact]
    public async Task FR56_Two_cycle_1_reports_of_a_version_with_a_rollback_are_both_listed_retrievable_by_id_and_linked_to_their_own_runs()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.1.0");
        var id = fixture.Project.Id;
        var (versionId, before, after, firstPlan, secondPlan) = await ProjectApi.WithDbAsync(factory, async db =>
        {
            // A released 1.0.0 whose review was rolled back after cycle 1, so cycle numbering restarted at 1.
            var start = DateTimeOffset.UtcNow.AddDays(-10);
            var version = new ReleaseVersion
            {
                ProjectId = id,
                Version = "1.0.0",
                Status = ReleaseVersionStatus.Released,
                Tag = "v1.0.0",
                Request = "First release",
                CreatedAt = start,
                ReleasedAt = start.AddDays(2),
            };
            db.ReleaseVersions.Add(version);
            var first = PlanArtifact(id, ArtifactType.OptimizationPlan, 1, start.AddHours(1).AddMinutes(1), version.Id);
            first.Status = ArtifactStatus.Invalidated;
            var second = PlanArtifact(id, ArtifactType.OptimizationPlan, 2, start.AddHours(5).AddMinutes(1), version.Id);
            db.Artifacts.AddRange(first, second);

            var r1 = AddReport(db, id, 1, start.AddHours(1), ReviewDecision.ReviewRequired, null, first.Id);
            var r2 = AddReport(db, id, 1, start.AddHours(5), ReviewDecision.ReviewRequired, null, second.Id);
            r1.ReleaseVersionId = version.Id;
            r2.ReleaseVersionId = version.Id;
            AddCompletedSteps(db, id, first, 2, "Fix a.", start.AddHours(1).AddMinutes(2), "a-");
            AddCompletedSteps(db, id, second, 3, "Fix b.", start.AddHours(5).AddMinutes(2), "b-");
            foreach (var step in db.ChangeTracker.Entries<ExecutionStep>().Select(e => e.Entity))
            {
                step.ReleaseVersionId = version.Id;
            }

            await db.SaveChangesAsync(Ct);
            return (version.Id, r1, r2, first, second);
        });
        var path = $"{fixture.Path}/compliance/reports";

        var list = await ProjectApi.ReadAsync<List<ComplianceReportSummaryDto>>(await GetAsync(fixture.Viewer.Client, $"{path}?releaseVersionId={versionId}"));
        var newest = await ProjectApi.ReadAsync<ComplianceReportDetailDto>(await GetAsync(fixture.Viewer.Client, $"{path}/1?releaseVersionId={versionId}"));
        var older = await ProjectApi.ReadAsync<ComplianceReportDetailDto>(await GetAsync(fixture.Viewer.Client, $"{path}/1?releaseVersionId={versionId}&reportId={before.Id}"));
        var exact = await ProjectApi.ReadAsync<ComplianceReportDetailDto>(await GetAsync(fixture.Viewer.Client, $"{path}/1?releaseVersionId={versionId}&reportId={after.Id}"));
        var runs = await RunsAsync(fixture.Viewer.Client, $"{fixture.Path}/execution/runs?releaseVersionId={versionId}");

        Assert.Equal([(1, after.Id), (1, before.Id)], list.Select(r => (r.Cycle, r.Id)));
        Assert.Equal((after.Id, secondPlan.Id), (newest.Id, newest.ExecutionRunId));
        Assert.Equal((before.Id, firstPlan.Id, ArtifactStatus.Invalidated), (older.Id, older.ExecutionRunId, older.OptimizationPlan!.Status));
        Assert.Equal((after.Id, secondPlan.Id), (exact.Id, exact.ExecutionRunId));
        Assert.Equal(
            [(firstPlan.Id, (int?)1, (Guid?)before.Id, 2, PlanRunStatus.Superseded), (secondPlan.Id, 1, after.Id, 3, PlanRunStatus.Completed)],
            runs.Select(r => (r.Id, r.Cycle, r.ComplianceReportId, r.StepCount, r.Status)));
        await Problems.AssertAsync(await GetAsync(fixture.Viewer.Client, $"{path}/2?releaseVersionId={versionId}&reportId={before.Id}"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await GetAsync(fixture.Viewer.Client, $"{path}/1?releaseVersionId={versionId}&reportId={Guid.NewGuid()}"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR56_An_unknown_cycle_is_NOT_FOUND_and_reports_are_for_members_scoped_by_release_version()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var client = fixture.Viewer.Client;

        Assert.Empty(await ProjectApi.ReadAsync<List<ComplianceReportSummaryDto>>(await GetAsync(client, $"{fixture.Path}/compliance/reports")));
        await Problems.AssertAsync(await GetAsync(client, $"{fixture.Path}/compliance/reports/1"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await GetAsync(client, $"{fixture.Path}/compliance/reports/0"), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await GetAsync(client, $"{fixture.Path}/compliance/reports?releaseVersionId={Guid.NewGuid()}"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await GetAsync(fixture.Outsider.Client, $"{fixture.Path}/compliance/reports/1"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    // ---------- §8 performance ----------

    [Fact]
    public async Task NFR_PERF2_FR57_FR56_Runs_and_cycles_take_a_fixed_number_of_queries_and_p95_under_300ms_for_10_cycles_of_30_steps()
    {
        var counter = new DbCommandCounter();
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.ConfigureDbContext<AideDbContext>(o => o.AddInterceptors(counter))));
        using var small = await ProjectApi.FixtureAsync(app);
        using var large = await ProjectApi.FixtureAsync(app);
        await LifecycleSeed.SetProjectAsync(factory, small.Project.Id, Phase.Review, 20, ApprovalMode.Advisory, reviewCycle: 1);
        await LifecycleSeed.SetProjectAsync(factory, large.Project.Id, Phase.Review, 20, ApprovalMode.Advisory, reviewCycle: 10);
        await SeedRunsAsync(small.Project.Id, 1, 1);
        await SeedRunsAsync(large.Project.Id, 30, [.. Enumerable.Repeat(30, 10)]);

        var (smallRuns, smallRunQueries) = await CountAsync(counter, small.Viewer.Client, $"{small.Path}/execution/runs");
        var (largeRuns, largeRunQueries) = await CountAsync(counter, large.Viewer.Client, $"{large.Path}/execution/runs");
        var (_, smallCycleQueries) = await CountAsync(counter, small.Viewer.Client, $"{small.Path}/compliance/reports");
        var (_, largeCycleQueries) = await CountAsync(counter, large.Viewer.Client, $"{large.Path}/compliance/reports");

        Assert.Equal((2, 11, 330), (JsonDocument.Parse(smallRuns).RootElement.GetArrayLength(), JsonDocument.Parse(largeRuns).RootElement.GetArrayLength(), RunsStepCount(largeRuns)));
        Assert.Equal(smallRunQueries, largeRunQueries);
        Assert.Equal(smallCycleQueries, largeCycleQueries);
        Assert.InRange(largeRunQueries, 1, 16);

        // §8: p95 over 20 sequential requests of each endpoint (after the warm-up above).
        foreach (var path in new[] { $"{large.Path}/execution/runs", $"{large.Path}/compliance/reports" })
        {
            var samples = await Latency.SampleAsync(20, async () => Assert.Equal(HttpStatusCode.OK, (await GetAsync(large.Viewer.Client, path)).StatusCode));
            var p95 = Latency.Percentile(samples, 95);
            Assert.True(p95 < TimeSpan.FromMilliseconds(300), $"p95 of {path} took {p95.TotalMilliseconds} ms.");
        }
    }

    // ---------- helpers ----------

    private sealed record SeededRuns(Artifact Development, IReadOnlyList<Artifact> FixPlans, IReadOnlyList<ComplianceReport> Reports);

    /// <summary>
    /// A completed development run of <paramref name="developmentSteps"/> steps, then one review cycle per entry of
    /// <paramref name="fixSteps"/> whose report drafted an optimisation plan with that many completed steps. Each step has one
    /// accepted attempt with a Code Generator run of 100 + 50 tokens.
    /// </summary>
    private Task<SeededRuns> SeedRunsAsync(Guid id, int developmentSteps, params int[] fixSteps) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var start = DateTimeOffset.UtcNow.AddHours(-(fixSteps.Length + 2));
            var development = PlanArtifact(id, ArtifactType.ImplementationPlan, 1, start);
            db.Artifacts.Add(development);
            AddCompletedSteps(db, id, development, developmentSteps, "Step", start);
            var plans = new List<Artifact>();
            var reports = new List<ComplianceReport>();
            for (var i = 0; i < fixSteps.Length; i++)
            {
                var at = start.AddHours(i + 1);
                var plan = PlanArtifact(id, ArtifactType.OptimizationPlan, i + 1, at.AddMinutes(1));
                db.Artifacts.Add(plan);
                reports.Add(AddReport(db, id, i + 1, at, ReviewDecision.ReviewRequired, null, plan.Id));
                AddCompletedSteps(db, id, plan, fixSteps[i], $"Fix {i + 1}.", at.AddMinutes(2), $"fix{i + 1}-");
                plans.Add(plan);
            }

            await db.SaveChangesAsync(Ct);
            return new SeededRuns(development, plans, reports);
        });

    private Task<ComplianceReport> SeedReportAsync(Guid id, int cycle, DateTimeOffset at, ReviewDecision decision, EscalationReason? reason) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var report = AddReport(db, id, cycle, at, decision, reason, null);
            report.Regressions = "[]";
            report.ExternalChecks = null;
            await db.SaveChangesAsync(Ct);
            return report;
        });

    private static ComplianceReport AddReport(AideDbContext db, Guid id, int cycle, DateTimeOffset at, ReviewDecision decision, EscalationReason? reason, Guid? planId)
    {
        var run = new AgentRun
        {
            ProjectId = id,
            Agent = AgentKind.CodeReview,
            Phase = Phase.Review,
            Provider = LlmProvider.Fake,
            Model = "fake-1",
            PromptVersion = "code-review@1",
            Status = AgentRunStatus.Succeeded,
            CreatedAt = at,
        };
        var report = new ComplianceReport
        {
            ProjectId = id,
            ReviewCycle = cycle,
            Checkpoint = cycle + 5,
            Score = 0.700m,
            Threshold = 0.850m,
            CategoryScores = [new CategoryScoreEntry(ReviewCategory.Security, 0.8m, 0.9m, 0.1m, [new ScoreEvidence("src/config.js", "Secret in config")])],
            Violations =
            [
                new ReviewFinding(ReviewCategory.Security, Severity.Major, "Secret in config", "src/config.js", FindingSource.Deterministic, "const key = ..."),
                new ReviewFinding(ReviewCategory.Testing, Severity.Minor, "Few tests", null, FindingSource.Agent),
            ],
            MissingFeatures = ["CSV export"],
            DriftLevel = DriftLevel.Low,
            DriftAreas = [new DriftAreaEntry("auth", Severity.Minor, "Session handling differs from the spec.")],
            Decision = decision,
            EscalationReason = reason,
            AgentRunId = run.Id,
            OptimizationPlanArtifactId = planId,
            Regressions = """[{"requirementId":"FR-3","category":"feature_coverage","description":"Export broke","evidence":"src/export.js"}]""",
            ExternalChecks = """{"checks":[{"name":"ci / test","conclusion":"failure","required":true,"detailsUrl":"https://github.com/acme/widgets/runs/1"}],"checksWaitedSeconds":90,"checksTimedOut":false,"preview":null}""",
            CreatedAt = at,
        };
        db.AgentRuns.Add(run);
        db.ComplianceReports.Add(report);
        return report;
    }

    private static Artifact PlanArtifact(Guid id, ArtifactType type, int version, DateTimeOffset at, Guid releaseVersionId = default) => new()
    {
        ReleaseVersionId = releaseVersionId,
        ProjectId = id,
        Type = type,
        Version = version,
        Content = $"# {type} v{version}",
        ContentHash = ContentHash.Compute($"# {type} v{version}"),
        Status = ArtifactStatus.Locked,
        CheckpointAtCreation = 3,
        CreatedAt = at,
    };

    private static void AddCompletedSteps(AideDbContext db, Guid id, Artifact plan, int count, string title, DateTimeOffset at, string prefix = "")
    {
        for (var n = 1; n <= count; n++)
        {
            var path = prefix.Length == 0 ? $"src/part{n}.js" : $"src/{prefix}{n}.js";
            var step = new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = plan.Id,
                StepNumber = n,
                Title = prefix.Length == 0 ? $"{title} {n}" : $"{title}{n}",
                Task = "Do it.",
                DeclaredFiles = [new DeclaredFile(path, FileAction.Create)],
                Status = StepStatus.Completed,
                Attempt = 1,
                CommitSha = $"abc-{prefix}{n}",
                FileManifest = [new FileManifestEntry(path, FileAction.Create, ContentHash.Compute(path))],
                StartedAt = at.AddSeconds(n),
                CompletedAt = at.AddSeconds(n + 1),
            };
            var run = new AgentRun
            {
                ProjectId = id,
                Agent = AgentKind.CodeGenerator,
                Phase = Phase.Execution,
                StepId = step.Id,
                Provider = LlmProvider.Fake,
                Model = "fake-1",
                PromptVersion = "code-generator@1",
                Status = AgentRunStatus.Succeeded,
                InputTokens = 100,
                OutputTokens = 50,
                CreatedAt = at.AddSeconds(n),
            };
            db.ExecutionSteps.Add(step);
            db.AgentRuns.Add(run);
            db.ExecutionStepAttempts.Add(new ExecutionStepAttempt
            {
                StepId = step.Id,
                Attempt = 1,
                AgentRunId = run.Id,
                Outcome = StepAttemptOutcome.Accepted,
                ProducedFiles = step.FileManifest,
                CreatedAt = at.AddSeconds(n + 1),
            });
        }
    }

    private static int RunsStepCount(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().Sum(r => r.GetProperty("steps").GetArrayLength());

    private static async Task<(string Body, int Queries)> CountAsync(DbCommandCounter counter, HttpClient client, string path)
    {
        // Warm up (first-call model and plan caches), then count one request.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, path)).StatusCode);
        var before = counter.Count;
        var response = await GetAsync(client, path);
        var queries = counter.Count - before;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadAsStringAsync(Ct), queries);
    }

    private static string Reply(int stepNumber, params (string Path, string Content)[] files) =>
        JsonSerializer.Serialize(new { stepNumber, files = files.Select(f => new { path = f.Path, content = f.Content }), notes = "scripted" });

    private static async Task<List<ExecutionRunDto>> RunsAsync(HttpClient client, string path) =>
        await ProjectApi.ReadAsync<List<ExecutionRunDto>>(await GetAsync(client, path));

    private static async Task<List<StepAttemptDto>> AttemptsAsync(HttpClient client, Guid id, Guid stepId) =>
        await ProjectApi.ReadAsync<List<StepAttemptDto>>(await GetAsync(client, $"/api/projects/{id}/execution/steps/{stepId}/attempts"));

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path) =>
        client.GetAsync(new Uri(path, UriKind.Relative), Ct);
}
