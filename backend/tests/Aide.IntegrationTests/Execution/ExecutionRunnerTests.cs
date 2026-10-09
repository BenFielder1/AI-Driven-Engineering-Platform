using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Gates;
using Aide.Api.Features.Projects;
using Aide.Api.Jobs;
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
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Execution;

/// <summary>
/// FR-31–FR-35, FR-37 against PostgreSQL with the Fake LLM provider and the Local Git provider: start/pause/resume, the
/// step job (ordering, inputs, Guardian, retries, blocking, commits) and redelivery idempotency.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionRunnerTests(AideApiFactory factory)
{
    private const string InitialCommit = "Initial commit";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- FR-35 / FR-31: start ----------

    [Fact]
    public async Task FR35_Starting_a_Local_project_prepares_a_repo_with_the_project_branch_checked_out_and_queues_one_job()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(id, Chain(2));

        var state = await ControlAsync(fixture.Contributor.Client, id, "start");
        var again = await ControlAsync(fixture.Contributor.Client, id, "start");

        Assert.Equal((ExecutionRunStatus.Running, false, 2, 2), (state.Status, state.Paused, state.Counts.Total, state.Counts.Pending));
        Assert.Equal(ExecutionRunStatus.Running, again.Status);
        var branch = ExecutionRules.BranchName(fixture.Project.Slug, id);
        Assert.Equal(branch, await GitAsync(id, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal(InitialCommit, await GitAsync(id, "log", "-1", "--format=%s", "main"));
        Assert.Equal(branch, await ProjectApi.WithDbAsync(factory, db => db.ProjectGitSettings.Where(g => g.ProjectId == id).Select(g => g.BranchName).SingleAsync(Ct)));
        var job = Assert.Single(await LifecycleSeed.JobsAsync(factory, id));
        Assert.Equal((JobTypes.Execution, JobStatus.Queued), (job.Type, job.Status));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ExecutionStarted));
        var projectState = await ProjectApi.ReadAsync<ProjectStateDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/state", UriKind.Relative), Ct));
        Assert.Equal(ExecutionRunStatus.Running, projectState.ExecutionStatus);
    }

    [Fact]
    public async Task FR31_Start_outside_execution_is_PHASE_PRECONDITION_FAILED_and_Viewers_are_forbidden()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        await Problems.AssertAsync(await PostAsync(fixture.Contributor.Client, id, "start"), HttpStatusCode.Conflict, ErrorCodes.PhasePreconditionFailed);
        await Problems.AssertAsync(await PostAsync(fixture.Contributor.Client, id, "pause"), HttpStatusCode.Conflict, ErrorCodes.PhasePreconditionFailed);
        await Problems.AssertAsync(await PostAsync(fixture.Contributor.Client, id, "resume"), HttpStatusCode.Conflict, ErrorCodes.PhasePreconditionFailed);
        await Problems.AssertAsync(await PostAsync(fixture.Viewer.Client, id, "start"), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await PostAsync(fixture.Outsider.Client, id, "pause"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Empty(await LifecycleSeed.JobsAsync(factory, id));
    }

    [Fact]
    public async Task FR26_Starting_with_budget_100k_and_100k_used_is_BUDGET_EXCEEDED_and_queues_nothing()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(id, Chain(1));
        await ProjectApi.WithDbAsync(factory, db => db.Projects.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TokenBudget, 100_000L).SetProperty(p => p.TokensUsed, 100_000L), Ct));

        await Problems.AssertAsync(await PostAsync(fixture.Contributor.Client, id, "start"), HttpStatusCode.Conflict, ErrorCodes.BudgetExceeded);

        Assert.Empty(await LifecycleSeed.JobsAsync(factory, id));
    }

    [Fact]
    public async Task FR35_Advancing_planning_to_execution_prepares_the_working_copy_first()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(id, Chain(2), Phase.Planning);

        var response = await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Phase.Execution, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);
        Assert.Equal(ExecutionRules.BranchName(fixture.Project.Slug, id), await GitAsync(id, "symbolic-ref", "--short", "HEAD"));
    }

    // ---------- FR-31 / FR-34: running steps ----------

    [Fact]
    public async Task FR34_Execution_with_Fake_and_Local_commits_each_step_on_the_project_branch_and_completes()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(id, Chain(3));
        await ControlAsync(fixture.Contributor.Client, id, "start");

        await AgentJobs.RunQueuedAsync(factory, id);

        var listed = await StepsAsync(fixture.Viewer.Client, id);
        var branch = ExecutionRules.BranchName(fixture.Project.Slug, id);
        Assert.All(listed.Steps, s =>
        {
            Assert.Equal((StepStatus.Completed, 1, branch), (s.Status, s.Attempt, s.BranchName));
            Assert.Matches("^[0-9a-f]{40}$", s.CommitSha!);
            Assert.Null(s.CommitUrl);
        });
        var subjects = (await GitAsync(id, "log", "--reverse", "--format=%s", branch)).Split('\n');
        Assert.Equal([InitialCommit, "[AIDE][Phase:execution][Step:1] Step 1", "[AIDE][Phase:execution][Step:2] Step 2", "[AIDE][Phase:execution][Step:3] Step 3"], subjects);
        var step2 = listed.Steps[1];
        Assert.Equal(step2.CommitSha, await GitAsync(id, "rev-parse", $"{branch}~1"));
        var body = await GitAsync(id, "log", "-1", "--format=%b", step2.CommitSha!);
        Assert.Contains("Files:\n- src/part2.js", body, StringComparison.Ordinal);
        Assert.Contains($"AIDE-Step-Id: {step2.Id:D}", body, StringComparison.Ordinal);
        var manifest = Assert.Single(step2.FileManifest!);
        var onDisk = await File.ReadAllTextAsync(Path.Combine(WorkingCopy(id), "src", "part2.js"), Ct);
        Assert.Equal(new FileManifestEntryDto("src/part2.js", FileAction.Create, ContentHash.Compute(onDisk)), manifest);

        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ExecutionCompleted));
        Assert.Equal(3, (await LifecycleSeed.EventsAsync(factory, id, EventTypes.StepCompleted)).Count);
        Assert.All(await LifecycleSeed.JobsAsync(factory, id), j => Assert.Equal(JobStatus.Succeeded, j.Status));
        var state = await ProjectApi.ReadAsync<ProjectStateDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/state", UriKind.Relative), Ct));
        Assert.Equal((ExecutionRunStatus.Completed, 3), (state.ExecutionStatus, state.Execution.Completed));
        Assert.Equal(Phase.Execution, state.Phase);
    }

    [Fact]
    public async Task FR31_With_steps_1_to_3_completed_step_4_runs_next_with_the_declared_files_current_content()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        PlanStep[] plan =
        [
            .. Chain(3),
            new PlanStep(4, "Extend part 2", "Extend part 2.", [new DeclaredFile("src/part2.js", FileAction.Modify)], [2]),
            new PlanStep(5, "Docs", "Write docs.", [new DeclaredFile("docs/api.md", FileAction.Create)], [4]),
        ];
        await SeedExecutionAsync(id, plan, completedThrough: 3);
        await ControlAsync(fixture.Contributor.Client, id, "start");
        Directory.CreateDirectory(Path.Combine(WorkingCopy(id), "src"));
        await File.WriteAllTextAsync(Path.Combine(WorkingCopy(id), "src", "part2.js"), "export const part2 = 'original';\n", Ct);
        // Committed on the project branch: uncommitted files are discarded before a step, so they never reach a prompt.
        await GitAsync(id, "add", "src/part2.js");
        await GitAsync(id, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "Steps 1-3");
        var responder = new ScriptedResponder(AgentKind.CodeGenerator, Reply(4, ("src/part2.js", "export const part2 = 'extended';\n")));
        var app = AgentJobs.WithResponders(factory, responder);

        await AgentJobs.RunAsync(app, Assert.Single(await LifecycleSeed.JobsAsync(factory, id)));

        var prompt = Assert.Single(responder.Prompts);
        Assert.Contains("<step number=\"4\">", prompt, StringComparison.Ordinal);
        Assert.Contains("----- BEGIN src/part2.js -----\nexport const part2 = 'original';\n----- END src/part2.js -----", prompt, StringComparison.Ordinal);
        Assert.Contains("## Step 3 [completed]: Step 3", prompt, StringComparison.Ordinal);
        Assert.Contains("## Step 4 [in_progress]: Extend part 2", prompt, StringComparison.Ordinal);
        Assert.Contains("## Step 5 [pending]: Docs", prompt, StringComparison.Ordinal);
        Assert.Contains("<technical_specification>\n# Spec", prompt, StringComparison.Ordinal);
        Assert.Contains("at most 20 files", prompt, StringComparison.Ordinal);
        var steps = await StepsAsync(fixture.Viewer.Client, id);
        Assert.Equal(StepStatus.Completed, steps.Steps[3].Status);
        Assert.Equal(StepStatus.Pending, steps.Steps[4].Status);
        Assert.Equal(FileAction.Modify, Assert.Single(steps.Steps[3].FileManifest!).Action);
        var run = Assert.Single(await AgentJobs.RunsAsync(factory, id));
        Assert.Equal((AgentKind.CodeGenerator, (Guid?)steps.Steps[3].Id), (run.Agent, run.StepId));
        Assert.Contains(run.InputArtifacts, a => a.Type == ArtifactType.ImplementationPlan);

        // The finished job queued the next one (§4.9), and only one.
        var queued = (await LifecycleSeed.JobsAsync(factory, id)).Where(j => j.Status == JobStatus.Queued).ToList();
        Assert.Single(queued);
    }

    // ---------- FR-32 / FR-33: Guardian, retries, blocking ----------

    [Fact]
    public async Task FR32_The_Guardian_rejects_a_step_and_records_every_violation_without_committing()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(id, Chain(2));
        await ControlAsync(fixture.Contributor.Client, id, "start");
        var bad = Reply(2, ("src/other.js", "const key = 'AKIAIOSFODNN7EXAMPLE';\n"), ("src/part1.js", "ok"));
        var app = AgentJobs.WithResponders(factory, new ScriptedResponder(AgentKind.CodeGenerator, bad));

        await AgentJobs.RunAsync(app, Assert.Single(await LifecycleSeed.JobsAsync(factory, id)));

        var step = (await StepsAsync(fixture.Viewer.Client, id)).Steps[0];
        Assert.Equal((StepStatus.Rejected, 1), (step.Status, step.Attempt));
        var codes = step.Violations!.Where(v => v.Severity == ViolationSeverity.Error).Select(v => v.Code).ToHashSet();
        Assert.Superset(new HashSet<string> { ErrorCodes.Guardian.SingleStep, ErrorCodes.Guardian.PlanAlignment, ErrorCodes.Guardian.SecretDetected }, codes);
        Assert.DoesNotContain(step.Violations!, v => v.Message.Contains("AKIA", StringComparison.Ordinal));
        Assert.Null(step.CommitSha);
        Assert.Equal(InitialCommit, await GitAsync(id, "log", "-1", "--format=%s"));
        var rejected = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.StepRejected)).Payload);
        Assert.True(rejected.GetProperty("retry").GetBoolean());
    }

    [Fact]
    public async Task FR33_A_step_rejected_three_times_is_blocked_on_the_fourth_with_feedback_each_retry_and_an_escalation_gate()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(id, Chain(2));
        await ControlAsync(fixture.Contributor.Client, id, "start");
        var responder = new ScriptedResponder(AgentKind.CodeGenerator, Reply(1, ("src/undeclared.js", "x")));
        var app = AgentJobs.WithResponders(factory, responder);

        await AgentJobs.RunQueuedAsync(app, id);

        Assert.Equal(4, responder.Prompts.Count);
        Assert.DoesNotContain("rejected by the Execution Guardian", responder.Prompts[0], StringComparison.Ordinal);
        Assert.All(responder.Prompts.Skip(1), p => Assert.Contains("[PLAN_ALIGNMENT] 'src/undeclared.js' is not declared for this step.", p, StringComparison.Ordinal));
        var step = (await StepsAsync(fixture.Viewer.Client, id)).Steps[0];
        Assert.Equal((StepStatus.Blocked, 4), (step.Status, step.Attempt));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.StepBlocked));
        Assert.Equal(4, (await LifecycleSeed.EventsAsync(factory, id, EventTypes.StepRejected)).Count);
        Assert.DoesNotContain(await LifecycleSeed.JobsAsync(factory, id), j => j.Status == JobStatus.Queued);
        var gates = await ProjectApi.ReadAsync<List<GateDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/gates?status=pending", UriKind.Relative), Ct));
        var gate = Assert.Single(gates);
        Assert.Equal((GateType.Escalation, EscalationReason.StepBlocked, step.Id, 1), (gate.Type, gate.EscalationReason!.Value, gate.ExecutionStepId!.Value, gate.StepNumber!.Value));
        await Problems.AssertAsync(await PostAsync(fixture.Contributor.Client, id, "start"), HttpStatusCode.Conflict, ErrorCodes.PhasePreconditionFailed);
        var state = await ProjectApi.ReadAsync<ProjectStateDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/state", UriKind.Relative), Ct));
        Assert.Equal(ExecutionRunStatus.Blocked, state.ExecutionStatus);

        // A blocked step's escalation has no review to accept: accept is refused and the gate stays pending.
        await Problems.AssertAsync(
            await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "accept" }),
            HttpStatusCode.BadRequest,
            ErrorCodes.InvalidDecision);
        Assert.Equal(GateStatus.Pending, (await LifecycleSeed.GatesAsync(factory, id)).Single(g => g.Id == gate.Id).Status);

        // FR-14 continue: the step gets fresh attempts and execution is queued again.
        var decided = await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "continue" });
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        var retried = (await StepsAsync(fixture.Viewer.Client, id)).Steps[0];
        Assert.Equal((StepStatus.Pending, 0), (retried.Status, retried.Attempt));
        Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Status == JobStatus.Queued);
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.StepUnblocked));
    }

    [Fact]
    public async Task FR44_Escalation_rollback_to_the_planning_version_requeues_the_blocked_step_so_execution_starts_again()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedExecutionAsync(id, Chain(2));
        await LifecycleSeed.AddTransitionAsync(factory, id, Phase.Refinement, Phase.Specification, 2, t0);
        await LifecycleSeed.AddTransitionAsync(factory, id, Phase.Specification, Phase.Planning, 3, t0.AddMinutes(1));
        await LifecycleSeed.AddTransitionAsync(factory, id, Phase.Planning, Phase.Execution, 4, t0.AddMinutes(2));
        await ControlAsync(fixture.Contributor.Client, id, "start");
        var app = AgentJobs.WithResponders(factory, new ScriptedResponder(AgentKind.CodeGenerator, Reply(1, ("src/undeclared.js", "x"))));
        await AgentJobs.RunQueuedAsync(app, id);
        var gate = Assert.Single(await LifecycleSeed.GatesAsync(factory, id), g => g.Status == GateStatus.Pending);

        var decided = await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "rollback", targetVersion = 3 });

        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        Assert.Equal(Phase.Planning, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);
        var step = (await StepsAsync(fixture.Viewer.Client, id)).Steps[0];
        Assert.Equal((StepStatus.Pending, 0), (step.Status, step.Attempt));
        var record = await ProjectApi.WithDbAsync(factory, db => db.RollbackEvents.SingleAsync(r => r.ProjectId == id, Ct));
        Assert.Contains(step.Id, record.ResetStepIds);

        Assert.Equal(HttpStatusCode.OK, (await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id)).StatusCode);
        var started = await ControlAsync(fixture.Contributor.Client, id, "start");
        Assert.Equal(ExecutionRunStatus.Running, started.Status);
        await AgentJobs.RunQueuedAsync(factory, id);
        Assert.All((await StepsAsync(fixture.Viewer.Client, id)).Steps, s => Assert.Equal(StepStatus.Completed, s.Status));
    }

    // ---------- §4.9 / FR-45: a failing execution job never leaves a step in progress ----------

    [Fact]
    public async Task FR35_A_working_copy_that_cannot_be_prepared_fails_the_job_before_the_step_starts()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(id, Chain(1));
        await ControlAsync(fixture.Contributor.Client, id, "start");
        await ProjectApi.WithDbAsync(factory, db => db.ProjectGitSettings.Where(g => g.ProjectId == id).ExecuteDeleteAsync(Ct));

        await AgentJobs.RunAsync(factory, Assert.Single(await LifecycleSeed.JobsAsync(factory, id)));

        Assert.Equal(JobStatus.Dead, Assert.Single(await LifecycleSeed.JobsAsync(factory, id)).Status);
        var step = Assert.Single((await StepsAsync(fixture.Viewer.Client, id)).Steps);
        Assert.Equal((StepStatus.Pending, 0), (step.Status, step.Attempt));
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.StepStarted));
    }

    [Fact]
    public async Task FR45_An_execution_job_that_ends_dead_lettered_returns_its_in_progress_step_to_rejected_so_start_works()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var steps = await SeedExecutionAsync(id, Chain(2));
        await ControlAsync(fixture.Contributor.Client, id, "start");
        var job = Assert.Single(await LifecycleSeed.JobsAsync(factory, id));
        await ProjectApi.WithDbAsync(factory, db => db.ExecutionSteps.Where(s => s.Id == steps[0].Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StepStatus.InProgress).SetProperty(x => x.Attempt, 2), Ct));
        await ProjectApi.WithDbAsync(factory, db => db.Jobs.Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running), Ct));

        // Like the worker: the job's final status and the ended hooks in one transaction.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            var row = await db.Jobs.SingleAsync(j => j.Id == job.Id, Ct);
            row.Status = JobStatus.Dead;
            foreach (var hook in scope.ServiceProvider.GetServices<IJobEndedHook>())
            {
                await hook.OnJobEndedAsync(db, new JobEnded(job.Id, job.Type, id, DeadLettered: true, "lease expired"), Ct);
            }

            await db.SaveChangesAsync(Ct);
        }

        var step = (await StepsAsync(fixture.Viewer.Client, id)).Steps[0];
        Assert.Equal((StepStatus.Rejected, 2), (step.Status, step.Attempt));
        var rejected = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.StepRejected)).Payload);
        Assert.True(rejected.GetProperty("interrupted").GetBoolean());

        var restarted = await ControlAsync(fixture.Contributor.Client, id, "start");
        Assert.Equal(ExecutionRunStatus.Running, restarted.Status);
        await AgentJobs.RunQueuedAsync(factory, id);
        var retried = (await StepsAsync(fixture.Viewer.Client, id)).Steps[0];
        Assert.Equal((StepStatus.Completed, 3), (retried.Status, retried.Attempt));
    }

    // ---------- FR-37: pause and resume ----------

    [Fact]
    public async Task FR37_Paused_during_step_1_the_step_finishes_and_step_2_waits_until_resumed()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var steps = await SeedExecutionAsync(id, Chain(2));
        await ControlAsync(fixture.Contributor.Client, id, "start");
        var job = Assert.Single(await LifecycleSeed.JobsAsync(factory, id));
        // Step 1 is running when the pause arrives.
        await ProjectApi.WithDbAsync(factory, db => db.ExecutionSteps.Where(s => s.Id == steps[0].Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StepStatus.InProgress).SetProperty(x => x.Attempt, 1), Ct));

        var paused = await ControlAsync(fixture.Contributor.Client, id, "pause");
        var pausedAgain = await ControlAsync(fixture.Contributor.Client, id, "pause");
        await AgentJobs.RunAsync(factory, job);

        Assert.Equal((ExecutionRunStatus.Paused, true), (paused.Status, paused.Paused));
        Assert.True(pausedAgain.Paused);
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ExecutionPaused));
        var afterPause = await StepsAsync(fixture.Viewer.Client, id);
        Assert.Equal((StepStatus.Completed, StepStatus.Pending), (afterPause.Steps[0].Status, afterPause.Steps[1].Status));
        Assert.DoesNotContain(await LifecycleSeed.JobsAsync(factory, id), j => j.Status == JobStatus.Queued);

        var resumed = await ControlAsync(fixture.Contributor.Client, id, "resume");
        Assert.Equal((ExecutionRunStatus.Running, false), (resumed.Status, resumed.Paused));
        await AgentJobs.RunQueuedAsync(factory, id);

        Assert.All((await StepsAsync(fixture.Viewer.Client, id)).Steps, s => Assert.Equal(StepStatus.Completed, s.Status));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ExecutionResumed));
    }

    [Fact]
    public async Task FR37_A_job_queued_before_a_pause_starts_no_new_step()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(id, Chain(1));
        await ControlAsync(fixture.Contributor.Client, id, "start");
        await ControlAsync(fixture.Contributor.Client, id, "pause");

        await AgentJobs.RunQueuedAsync(factory, id);

        Assert.Equal(StepStatus.Pending, Assert.Single((await StepsAsync(fixture.Viewer.Client, id)).Steps).Status);
        Assert.Empty(await AgentJobs.RunsAsync(factory, id));
    }

    // ---------- D17: redelivery ----------

    [Fact]
    public async Task FR34_A_redelivered_job_after_the_commit_completes_the_step_from_it_without_a_second_commit_or_attempt()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var steps = await SeedExecutionAsync(id, Chain(2));
        await ControlAsync(fixture.Contributor.Client, id, "start");
        var counting = new CountingResponder(new FakeCodeGeneratorResponder());
        var app = AgentJobs.WithResponders(factory, counting);
        var job = Assert.Single(await LifecycleSeed.JobsAsync(factory, id));
        await AgentJobs.RunAsync(app, job);
        var committed = (await StepsAsync(fixture.Viewer.Client, id)).Steps[0];

        // The worker died after the commit, before the step's completion was saved: the step is still in progress.
        await ProjectApi.WithDbAsync(factory, db => db.ExecutionSteps.Where(s => s.Id == steps[0].Id).ExecuteUpdateAsync(
            s => s.SetProperty(x => x.Status, StepStatus.InProgress).SetProperty(x => x.CommitSha, (string?)null).SetProperty(x => x.FileManifest, (IReadOnlyList<FileManifestEntry>?)null),
            Ct));
        await ProjectApi.WithDbAsync(factory, db => db.Jobs.Where(j => j.ProjectId == id && j.Status == JobStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled), Ct));
        await AgentJobs.RunAsync(app, job);

        var again = (await StepsAsync(fixture.Viewer.Client, id)).Steps[0];
        Assert.Equal((StepStatus.Completed, 1, committed.CommitSha), (again.Status, again.Attempt, again.CommitSha));
        Assert.Equal(committed.FileManifest, again.FileManifest);
        Assert.Equal(1, counting.Calls);
        var stepCommits = (await GitAsync(id, "log", "--format=%s", ExecutionRules.BranchName(fixture.Project.Slug, id)))
            .Split('\n').Count(s => s.Contains("[Step:1]", StringComparison.Ordinal));
        Assert.Equal(1, stepCommits);
        Assert.Equal(2, (await LifecycleSeed.EventsAsync(factory, id, EventTypes.StepCompleted)).Count);
    }

    [Fact]
    public async Task FR34_A_redelivered_job_whose_run_succeeded_reuses_the_run_instead_of_calling_the_provider_again()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(id, Chain(1));
        await ControlAsync(fixture.Contributor.Client, id, "start");
        var counting = new CountingResponder(new ScriptedResponder(AgentKind.CodeGenerator, Reply(1, ("src/nope.js", "x"))));
        var app = AgentJobs.WithResponders(factory, counting);
        var job = Assert.Single(await LifecycleSeed.JobsAsync(factory, id));
        await AgentJobs.RunAsync(app, job);

        // Redelivery of the same job (attempt 1's run already succeeded) while step 1 is still that attempt.
        await ProjectApi.WithDbAsync(factory, db => db.ExecutionSteps.Where(s => s.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StepStatus.InProgress), Ct));
        await ProjectApi.WithDbAsync(factory, db => db.Jobs.Where(j => j.ProjectId == id && j.Status == JobStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled), Ct));
        await AgentJobs.RunAsync(app, job);

        Assert.Equal(1, counting.Calls);
        var step = Assert.Single((await StepsAsync(fixture.Viewer.Client, id)).Steps);
        Assert.Equal((StepStatus.Rejected, 1), (step.Status, step.Attempt));
        Assert.Single(await AgentJobs.RunsAsync(factory, id));
    }

    // ---------- helpers ----------

    private static PlanStep[] Chain(int count) =>
    [
        .. Enumerable.Range(1, count).Select(n => new PlanStep(
            n, $"Step {n}", $"Build part {n}.", [new DeclaredFile($"src/part{n}.js", FileAction.Create)], n == 1 ? [] : [n - 1])),
    ];

    /// <summary>The project in <paramref name="phase"/> with request, spec and a plan of <paramref name="plan"/> locked and its steps.</summary>
    private async Task<IReadOnlyList<ExecutionStep>> SeedExecutionAsync(Guid id, PlanStep[] plan, Phase phase = Phase.Execution, int completedThrough = 0)
    {
        await LifecycleSeed.SetProjectAsync(factory, id, phase, 4, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, content: "# Todo API\n\n## Summary\nManage todos.");
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, content: "# Spec\n\nExpress API.");
        var planArtifact = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, content: PlanDocument.Render(ArtifactType.ImplementationPlan, plan));
        return await ProjectApi.WithDbAsync<IReadOnlyList<ExecutionStep>>(factory, async db =>
        {
            var steps = plan.Select(p => new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = planArtifact.Id,
                StepNumber = p.Number,
                Title = p.Title,
                Task = p.Task,
                DeclaredFiles = p.Files,
                Dependencies = p.Dependencies,
                Status = p.Number <= completedThrough ? StepStatus.Completed : StepStatus.Pending,
                Attempt = p.Number <= completedThrough ? 1 : 0,
            }).ToList();
            db.ExecutionSteps.AddRange(steps);
            await db.SaveChangesAsync(Ct);
            return steps;
        });
    }

    private static string Reply(int stepNumber, params (string Path, string Content)[] files) =>
        JsonSerializer.Serialize(new { stepNumber, files = files.Select(f => new { path = f.Path, content = f.Content }), notes = "scripted" });

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string action) =>
        client.PostAsJsonAsync($"/api/projects/{id}/execution/{action}", new { }, Ct);

    private static async Task<ExecutionStateDto> ControlAsync(HttpClient client, Guid id, string action) =>
        await ProjectApi.ReadAsync<ExecutionStateDto>(await PostAsync(client, id, action), HttpStatusCode.Accepted);

    private static async Task<ExecutionStepsDto> StepsAsync(HttpClient client, Guid id) =>
        await ProjectApi.ReadAsync<ExecutionStepsDto>(await client.GetAsync(new Uri($"/api/projects/{id}/execution/steps", UriKind.Relative), Ct));

    private string WorkingCopy(Guid id) => Path.Combine(factory.WorkspacesRoot, id.ToString("D"));

    /// <summary>Reads the working copy with the git CLI (output trimmed).</summary>
    private async Task<string> GitAsync(Guid id, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = WorkingCopy(id),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("safe.directory=*");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(Ct);
        var error = await process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim().ReplaceLineEndings("\n");
    }
}
