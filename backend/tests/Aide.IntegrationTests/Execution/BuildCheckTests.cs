using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Aide.Api.Features.BuildChecks;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Gates;
using Aide.Api.Features.Projects;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.IntegrationTests.Artifacts;
using Aide.IntegrationTests.Live;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Execution;

/// <summary>
/// FR-254–FR-259 (0.6.1 part 2, §4.47, §4.48) end to end with the Fake LLM, Local Git and the Fake deployment provider: the
/// build check after execution, its waits, the build-fix agent and its steps, exhaustion, settings, the read endpoint and
/// the live <c>buildCheckUpdated</c> message.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class BuildCheckTests(AideApiFactory factory)
{
    private const string ModuleNotFound = "Module not found: Can't resolve '@/lib/db'";

    private const string ModuleLog = "Running \"npm run build\"\n> next build\nCreating an optimized production build ...\nFailed to compile.\n\n./app/page.tsx\n"
        + ModuleNotFound + "\n\nhttps://nextjs.org/docs/messages/module-not-found\nError: Command \"npm run build\" exited with 1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A project in execution with a 2-step plan (step 2 also writes <paramref name="extraFile"/>), bound to the Fake deploy provider when <paramref name="deploy"/>, started.</summary>
    private static async Task<ProjectFixture> ExecutingAsync(WebApplicationFactory<Program> app, int maxAttempts, bool deploy, string? extraFile = null)
    {
        var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        if (deploy)
        {
            await DeploymentHarness.BindAsync(app, id, await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id), fixture.Owner.User!.Id);
        }

        var plan = await SeedExecutionAsync(app, id, maxAttempts, extraFile);
        _ = plan;
        await ProjectApi.ReadAsync<ExecutionStateDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);
        return fixture;
    }

    /// <summary>Execution with a locked 2-step plan (steps pending, or completed when <paramref name="completed"/>).</summary>
    private static async Task<Artifact> SeedExecutionAsync(WebApplicationFactory<Program> app, Guid id, int maxAttempts, string? extraFile = null, bool completed = false)
    {
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Execution, 4, ApprovalMode.Advisory, maxBuildFixAttempts: maxAttempts);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: "# Todo API\n\n## Summary\nManage todos.");
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, content: "# Spec\n\n## Technical Architecture\nNode.js service.\n\n## API Contracts\nNo HTTP API.\n");
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
                DeclaredFiles = n == 2 && extraFile is not null
                    ? [new DeclaredFile($"src/part{n}.js", FileAction.Create), new DeclaredFile(extraFile, FileAction.Create)]
                    : [new DeclaredFile($"src/part{n}.js", FileAction.Create)],
                Dependencies = n == 1 ? [] : [n - 1],
                Status = completed ? StepStatus.Completed : StepStatus.Pending,
                CompletedAt = completed ? DateTimeOffset.UtcNow : null,
            }));
            await db.SaveChangesAsync(Ct);
        });
        return plan;
    }

    private static Task<List<BuildCheck>> ChecksAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.BuildChecks.AsNoTracking().Where(c => c.ProjectId == id).OrderBy(c => c.ReviewCycle).ThenBy(c => c.Attempt).ToListAsync(Ct));

    private static Task<List<Job>> QueuedAsync(WebApplicationFactory<Program> app, Guid id, string type) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().Where(j => j.ProjectId == id && j.Type == type && j.Status == JobStatus.Queued).OrderBy(j => j.CreatedAt).ToListAsync(Ct));

    private static Task<List<Job>> JobsOfAsync(WebApplicationFactory<Program> app, Guid id, string type) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().Where(j => j.ProjectId == id && j.Type == type).OrderBy(j => j.CreatedAt).ToListAsync(Ct));

    /// <summary>The newest preview deployment fails with <paramref name="log"/> as its stored build log tail.</summary>
    private static Task FailNewestPreviewAsync(WebApplicationFactory<Program> app, Guid id, string log) =>
        ProjectApi.WithDbAsync(app, async db =>
        {
            var newest = await db.Deployments
                .Where(d => d.ProjectId == id && d.Environment == DeploymentEnvironment.Preview)
                .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
                .Select(d => d.Id)
                .FirstAsync(Ct);
            await db.Deployments.Where(d => d.Id == newest)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeploymentStatus.Error).SetProperty(d => d.BuildLogTail, log), Ct);
        });

    /// <summary>Settles the newest preview rollout if it's still queued.</summary>
    private static async Task SettleNewestRolloutAsync(WebApplicationFactory<Program> app, Guid id)
    {
        var rollout = await DeploymentHarness.RolloutJobAsync(app, id);
        if (rollout.Status == JobStatus.Queued)
        {
            await DeploymentHarness.SettleRolloutAsync(app, id, rollout.Id);
        }
    }

    /// <summary>Runs the project's jobs like the worker would over time: queued jobs, the preview rollout, then the build check, until the project is in review.</summary>
    private static async Task DriveToReviewAsync(WebApplicationFactory<Program> app, Guid id, Func<Task>? afterRollout = null, int maxRounds = 12)
    {
        for (var round = 0; round < maxRounds; round++)
        {
            await AgentJobs.RunQueuedAsync(app, id);
            if ((await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase == Phase.Review)
            {
                return;
            }

            await SettleNewestRolloutAsync(app, id);
            if (afterRollout is not null)
            {
                await afterRollout();
            }

            foreach (var check in await QueuedAsync(app, id, JobTypes.BuildCheck))
            {
                await DeploymentHarness.SettleAsync(app, check.Id);
            }
        }

        Assert.Fail($"Project {id} didn't reach review in {maxRounds} rounds.");
    }

    /// <summary>Records the prompts the inner responder answers.</summary>
    private sealed class RecordingResponder(IFakeAgentResponder inner) : IFakeAgentResponder
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _prompts = new();

        public AgentKind Agent => inner.Agent;

        public IReadOnlyList<string> Prompts => [.. _prompts];

        public string Respond(LlmRequest request)
        {
            _prompts.Enqueue(string.Join("\n", request.Messages.Select(m => m.Content)));
            return inner.Respond(request);
        }
    }

    // ---------- FR-254, FR-255 ----------

    [Fact]
    public async Task FR254_FR255_With_a_preview_target_a_check_waits_in_execution_then_passes_and_review_reuses_the_rollout()
    {
        var app = AgentJobs.WithResponders(DeploymentHarness.Fake(factory), new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));
        using var fixture = await ExecutingAsync(app, maxAttempts: 5, deploy: true);
        var id = fixture.Project.Id;

        // FR-254: the last step completes, the PR job queues the rollout and starts attempt 0; the project stays in execution.
        await AgentJobs.RunQueuedAsync(app, id);
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Execution, ProjectStatus.Building), (project.CurrentPhase, project.Status));
        var check = Assert.Single(await ChecksAsync(app, id));
        var rollout = await DeploymentHarness.RolloutJobAsync(app, id);
        Assert.Equal((0, 0, BuildCheckStatus.Waiting), (check.ReviewCycle, check.Attempt, check.Status));
        Assert.Equal(RolloutScheduler(rollout).GitSha, check.HeadSha);
        Assert.Empty(await QueuedAsync(app, id, JobTypes.Review));
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.BuildCheckStarted));

        // FR-255: waiting defers (never a failed attempt); then the rollout succeeds and the check passes.
        var job = Assert.Single(await QueuedAsync(app, id, JobTypes.BuildCheck));
        Assert.True(await DeploymentHarness.RunOnceAsync(app, job.Id));
        var deferred = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct));
        Assert.Equal((JobStatus.Queued, 0), (deferred.Status, deferred.Attempts));
        Assert.Contains("previewWaitSince", deferred.Payload, StringComparison.Ordinal);
        await DeploymentHarness.SettleRolloutAsync(app, id, rollout.Id);
        Assert.False(await DeploymentHarness.RunOnceAsync(app, job.Id));

        check = Assert.Single(await ChecksAsync(app, id));
        Assert.Equal(BuildCheckStatus.Passed, check.Status);
        Assert.NotNull(check.CompletedAt);
        Assert.Contains(check.Sources, s => s.Kind == BuildCheckSourceKind.Preview && s.Conclusion == "success");
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);

        // No new rollout: review scores with the rollout the check passed.
        await AgentJobs.RunQueuedAsync(app, id);
        Assert.Single(await JobsOfAsync(app, id, JobTypes.PreviewRollout));
        var report = await ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        var preview = JsonDocument.Parse(report.ExternalChecks!).RootElement.GetProperty("preview").GetProperty("rollout");
        Assert.Equal(rollout.Id.ToString(), preview.GetProperty("rolloutId").GetString());
        var completed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.BuildCheckCompleted)).Payload);
        Assert.Equal("passed", completed.GetProperty("status").GetString());
    }

    private static Aide.Api.Features.Deployments.RolloutJobPayload RolloutScheduler(Job job) =>
        Aide.Api.Features.Deployments.RolloutScheduler.TryPayload(job.Payload)!;

    [Fact]
    public async Task FR255_With_no_deploy_target_and_no_required_checks_the_check_is_skipped_and_review_starts()
    {
        var app = AgentJobs.WithResponders(factory, new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));
        using var fixture = await ExecutingAsync(app, maxAttempts: 5, deploy: false);
        var id = fixture.Project.Id;

        await AgentJobs.RunQueuedAsync(app, id);

        var check = Assert.Single(await ChecksAsync(app, id));
        Assert.Equal((0, BuildCheckStatus.Skipped), (check.Attempt, check.Status));
        Assert.Empty(await JobsOfAsync(app, id, JobTypes.BuildCheck)); // decided at once, no job
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Review, 1), (project.CurrentPhase, project.ReviewCycle));
    }

    [Fact]
    public async Task FR255_A_rollout_of_an_older_SHA_keeps_the_check_waiting()
    {
        var app = DeploymentHarness.Fake(factory);
        using var fixture = await ExecutingAsync(app, maxAttempts: 5, deploy: true);
        var id = fixture.Project.Id;
        await AgentJobs.RunQueuedAsync(app, id);
        await SettleNewestRolloutAsync(app, id);

        // The check now waits for a newer head than the one the (succeeded) rollout deployed.
        await ProjectApi.WithDbAsync(app, db => db.BuildChecks.Where(c => c.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.HeadSha, "0123456789abcdef0123456789abcdef01234567"), Ct));
        var job = Assert.Single(await QueuedAsync(app, id, JobTypes.BuildCheck));
        Assert.True(await DeploymentHarness.RunOnceAsync(app, job.Id));

        Assert.Equal(BuildCheckStatus.Waiting, Assert.Single(await ChecksAsync(app, id)).Status);
        Assert.Equal(Phase.Execution, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);
    }

    [Fact]
    public async Task FR255_A_preview_that_doesnt_finish_in_30_minutes_fails_the_check_with_the_preview_timeout_failure()
    {
        var app = DeploymentHarness.Fake(factory);
        using var fixture = await ExecutingAsync(app, maxAttempts: 5, deploy: true);
        var id = fixture.Project.Id;
        await AgentJobs.RunQueuedAsync(app, id);

        // The rollout is still running; the check has been waiting for 31 minutes.
        var job = Assert.Single(await QueuedAsync(app, id, JobTypes.BuildCheck));
        var check = Assert.Single(await ChecksAsync(app, id));
        var payload = AideJson.Serialize(new BuildCheckJobPayload(id, check.Id, PreviewWaitSince: DateTimeOffset.UtcNow.AddMinutes(-31)));
        await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Payload, payload), Ct));
        Assert.False(await DeploymentHarness.RunOnceAsync(app, job.Id));

        check = Assert.Single(await ChecksAsync(app, id));
        Assert.Equal(BuildCheckStatus.Failed, check.Status);
        var failure = Assert.Single(check.Failures);
        Assert.Equal("failure:preview:vercel", failure.FailureId);
        Assert.Contains("didn't finish within 30 minutes", failure.Message, StringComparison.Ordinal);
        Assert.Contains(check.Sources, s => s.Conclusion == BuildCheckRules.TimedOutConclusion);
        Assert.Single(await QueuedAsync(app, id, JobTypes.BuildFix));
    }

    // ---------- FR-256 ----------

    [Fact]
    public async Task FR256_A_Vercel_Module_not_found_failure_is_planned_as_a_build_fix_step_with_evidence_run_pushed_and_attempt_1_starts()
    {
        var buildFix = new RecordingResponder(new FakeBuildFixResponder());
        var app = AgentJobs.WithResponders(DeploymentHarness.Fake(factory), buildFix);
        using var fixture = await ExecutingAsync(app, maxAttempts: 5, deploy: true, extraFile: FakeDeploymentRules.FailBuildMarker);
        var id = fixture.Project.Id;
        await AgentJobs.RunQueuedAsync(app, id);
        await SettleNewestRolloutAsync(app, id);
        await FailNewestPreviewAsync(app, id, ModuleLog);
        await DeploymentHarness.SettleAsync(app, Assert.Single(await QueuedAsync(app, id, JobTypes.BuildCheck)).Id);

        var first = Assert.Single(await ChecksAsync(app, id));
        Assert.Equal(BuildCheckStatus.Failed, first.Status);
        var failure = Assert.Single(first.Failures);
        Assert.Equal(("failure:preview:vercel", FailureIds.Preview), (failure.FailureId, failure.Source));
        Assert.Contains(ModuleNotFound, failure.Excerpt, StringComparison.Ordinal);
        Assert.Contains(failure.ErrorSummary, l => l.Contains(ModuleNotFound, StringComparison.Ordinal));

        // The build-fix agent plans; its step runs; the PR job pushes, queues a rollout and starts attempt 1.
        await AgentJobs.RunQueuedAsync(app, id);
        var prompt = Assert.Single(buildFix.Prompts);
        Assert.Contains("failure id failure:preview:vercel (preview deployment)", prompt, StringComparison.Ordinal);
        var summary = prompt[prompt.IndexOf("<error_summary>", StringComparison.Ordinal)..prompt.IndexOf("</error_summary>", StringComparison.Ordinal)];
        Assert.Contains(ModuleNotFound, summary, StringComparison.Ordinal);

        var fix = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().SingleAsync(s => s.ProjectId == id && s.Origin == StepOrigin.BuildFix, Ct));
        Assert.Equal((3, (Guid?)first.Id, StepStatus.Completed), (fix.StepNumber, fix.BuildCheckId, fix.Status));
        Assert.Contains("failure:preview:vercel", fix.Title, StringComparison.Ordinal);
        Assert.Contains("**Failure evidence** (added by Tevrin)\nFailure id: failure:preview:vercel", fix.Task, StringComparison.Ordinal);
        Assert.NotNull(fix.CommitSha);
        var planned = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.BuildFixPlanned)).Payload);
        Assert.Equal([3], planned.GetProperty("stepNumbers").EnumerateArray().Select(e => e.GetInt32()));

        var checks = await ChecksAsync(app, id);
        Assert.Equal([(0, BuildCheckStatus.Failed), (1, BuildCheckStatus.Waiting)], checks.Select(c => (c.Attempt, c.Status)));
        Assert.Equal(fix.CommitSha, checks[1].HeadSha);
        Assert.Equal(2, (await JobsOfAsync(app, id, JobTypes.PreviewRollout)).Count);
        Assert.Equal(Phase.Execution, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);

        // FR-259: the step DTOs carry the origin and the check's attempt.
        var steps = await ProjectApi.ReadAsync<ExecutionStepsDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/execution/steps", UriKind.Relative), Ct));
        var dto = Assert.Single(steps.Steps, s => s.Number == 3);
        Assert.Equal((StepOrigin.BuildFix, (int?)0), (dto.Origin, dto.BuildCheckAttempt));
        Assert.All(steps.Steps.Where(s => s.Number < 3), s => Assert.Equal((StepOrigin.Plan, (int?)null), (s.Origin, s.BuildCheckAttempt)));
    }

    /// <summary>A Local project in execution whose 2 plan steps completed, with failed checks seeded for cycle 0.</summary>
    private static async Task<(ProjectFixture Fixture, Guid PlanId, Guid VersionId)> FailedChecksAsync(WebApplicationFactory<Program> app)
    {
        var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var plan = await SeedExecutionAsync(app, id, maxAttempts: 5, completed: true);
        var versionId = await DeploymentHarness.VersionIdAsync(app, id);
        return (fixture, plan.Id, versionId);
    }

    private static BuildCheck Check(Guid projectId, Guid versionId, int attempt, string log) => new()
    {
        ProjectId = projectId,
        ReleaseVersionId = versionId,
        ReviewCycle = 0,
        Attempt = attempt,
        HeadSha = $"{attempt}123456789abcdef0123456789abcdef0123456",
        Status = BuildCheckStatus.Failed,
        Failures = [new BuildCheckFailure("failure:preview:vercel", FailureFingerprint.Of("failure:preview:vercel", log), "Vercel preview build failed", FailureIds.Preview, log, BuildErrorSummary.Lines(log))],
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<Job> QueueBuildFixAsync(WebApplicationFactory<Program> app, Guid id, Guid checkId) =>
        await ProjectApi.WithDbAsync(app, async db =>
        {
            var job = new Job
            {
                Type = JobTypes.BuildFix,
                Payload = AideJson.Serialize(new BuildFixJobPayload(id, checkId)),
                ProjectId = id,
                Status = JobStatus.Queued,
                RunAfter = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(Ct);
            return job;
        });

    [Fact]
    public async Task FR256_A_failure_seen_by_the_previous_check_too_is_marked_repeated_2_attempts_with_the_previous_fix_step()
    {
        var buildFix = new RecordingResponder(new FakeBuildFixResponder());
        var app = AgentJobs.WithResponders(factory, buildFix);
        var (fixture, planId, versionId) = await FailedChecksAsync(app);
        using var _ = fixture;
        var id = fixture.Project.Id;
        var earlier = Check(id, versionId, 1, "12:01:03 " + ModuleNotFound);
        var current = Check(id, versionId, 2, "12:09:44 " + ModuleNotFound);
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.BuildChecks.AddRange(earlier, current);
            db.ExecutionSteps.Add(new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = planId,
                StepNumber = 3,
                Title = "Fix failure:preview:vercel",
                Task = "Create lib/db.ts so '@/lib/db' resolves (attempt 2's fix).",
                DeclaredFiles = [new DeclaredFile("lib/db.ts", FileAction.Create)],
                Status = StepStatus.Completed,
                Origin = StepOrigin.BuildFix,
                BuildCheckId = earlier.Id,
            });
            await db.SaveChangesAsync(Ct);
        });

        await AgentJobs.RunAsync(app, await QueueBuildFixAsync(app, id, current.Id));

        var prompt = Assert.Single(buildFix.Prompts);
        Assert.Contains("failure id failure:preview:vercel (preview deployment): Vercel preview build failed — repeated (2 attempts)", prompt, StringComparison.Ordinal);
        Assert.Contains("Create lib/db.ts so '@/lib/db' resolves (attempt 2's fix).", prompt, StringComparison.Ordinal);
        var stored = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().Where(s => s.BuildCheckId == current.Id).ToListAsync(Ct));
        Assert.Equal(4, Assert.Single(stored).StepNumber); // after the plan's highest step (3)
        Assert.Single(await QueuedAsync(app, id, JobTypes.Execution));
    }

    [Fact]
    public async Task FR256_A_build_fix_reply_naming_no_failure_id_still_gets_the_failure_evidence()
    {
        var reply = JsonSerializer.Serialize(new
        {
            steps = new[] { new { number = 1, title = "Create the db module", task = "Add lib/db.ts.", files = new[] { new { path = "lib/db.ts", action = "create" } }, dependencies = Array.Empty<int>() } },
            summary = "Adds the missing module.",
        });
        var app = AgentJobs.WithResponders(factory, new ScriptedResponder(AgentKind.BuildFix, reply));
        var (fixture, _, versionId) = await FailedChecksAsync(app);
        using var __ = fixture;
        var id = fixture.Project.Id;
        var current = Check(id, versionId, 0, ModuleLog);
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.BuildChecks.Add(current);
            await db.SaveChangesAsync(Ct);
        });

        await AgentJobs.RunAsync(app, await QueueBuildFixAsync(app, id, current.Id));

        // FR-239: the evidence block goes on a "Fix:" step Tevrin adds, or on step 1 with "Also fix".
        var stored = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().Where(s => s.BuildCheckId == current.Id).OrderBy(s => s.StepNumber).ToListAsync(Ct));
        Assert.NotEmpty(stored);
        Assert.All(stored, s => Assert.Equal(StepOrigin.BuildFix, s.Origin));
        Assert.Contains(stored, s => s.Task.Contains("Failure id: failure:preview:vercel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR258_A_fix_plan_still_invalid_after_one_retry_exhausts_the_check_and_review_starts()
    {
        var scripted = new ScriptedResponder(AgentKind.BuildFix, "not a plan", "still not a plan");
        var app = AgentJobs.WithResponders(factory, scripted);
        var (fixture, _, versionId) = await FailedChecksAsync(app);
        using var __ = fixture;
        var id = fixture.Project.Id;
        var current = Check(id, versionId, 0, ModuleLog);
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.BuildChecks.Add(current);
            await db.SaveChangesAsync(Ct);
        });

        await AgentJobs.RunAsync(app, await QueueBuildFixAsync(app, id, current.Id));

        Assert.Equal(2, scripted.Prompts.Count); // one retry, with the reasons
        Assert.Contains("Your previous fix plan was rejected", scripted.Prompts[1], StringComparison.Ordinal);
        Assert.Equal(BuildCheckStatus.Exhausted, Assert.Single(await ChecksAsync(app, id)).Status);
        var pr = Assert.Single(await QueuedAsync(app, id, JobTypes.PullRequest));
        Assert.Contains(BuildCheckService.ExhaustedTrigger, pr.Payload, StringComparison.Ordinal);

        await AgentJobs.RunQueuedAsync(app, id);
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);
    }

    // ---------- FR-257 ----------

    [Fact]
    public async Task FR257_A_build_fix_step_that_adds_ignoreBuildErrors_is_rejected_and_continue_on_its_escalation_exhausts_the_check()
    {
        var code = JsonSerializer.Serialize(new
        {
            stepNumber = 3,
            files = new[] { new { path = "next.config.ts", content = "export default { typescript: { ignoreBuildErrors: true } };\n" } },
            notes = "Turned off type errors.",
        });
        var app = AgentJobs.WithResponders(factory, new ScriptedResponder(AgentKind.CodeGenerator, code));
        var (fixture, planId, versionId) = await FailedChecksAsync(app);
        using var __ = fixture;
        var id = fixture.Project.Id;
        var current = Check(id, versionId, 0, ModuleLog);
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.BuildChecks.Add(current);
            db.ExecutionSteps.Add(new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = planId,
                StepNumber = 3,
                Title = "Fix failure:preview:vercel",
                Task = "Fix the type errors.",
                DeclaredFiles = [new DeclaredFile("next.config.ts", FileAction.Create)],
                Origin = StepOrigin.BuildFix,
                BuildCheckId = current.Id,
            });
            await db.SaveChangesAsync(Ct);
        });

        await ProjectApi.ReadAsync<ExecutionStateDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(app, id);

        var step = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().SingleAsync(s => s.BuildCheckId == current.Id, Ct));
        Assert.Equal(StepStatus.Blocked, step.Status);
        Assert.Contains(step.Violations!, v => v.Code == ErrorCodes.Guardian.BuildErrorsSuppressed);
        var rejected = LifecycleSeed.Payload((await LifecycleSeed.EventsAsync(app, id, EventTypes.StepRejected))[0].Payload);
        Assert.Contains(ErrorCodes.Guardian.BuildErrorsSuppressed, rejected.GetProperty("codes").EnumerateArray().Select(c => c.GetString()));

        // §4.47: "continue" on the blocked build-fix step exhausts the check; Review starts with the failures.
        var gate = Assert.Single(await LifecycleSeed.GatesAsync(app, id), g => g.Status == GateStatus.Pending);
        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "continue" }));
        Assert.Equal(BuildCheckStatus.Exhausted, Assert.Single(await ChecksAsync(app, id), c => c.Id == current.Id).Status);
        Assert.Equal(StepStatus.Invalidated, (await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().SingleAsync(s => s.Id == step.Id, Ct))).Status);
        await AgentJobs.RunQueuedAsync(app, id);
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);
    }

    // ---------- FR-258 ----------

    [Fact]
    public async Task FR258_With_max_2_and_three_failing_checks_two_fixes_run_the_third_check_is_exhausted_and_review_has_the_failures()
    {
        var review = new RecordingResponder(new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.80m] }));
        var app = AgentJobs.WithResponders(DeploymentHarness.Fake(factory), review);
        using var fixture = await ExecutingAsync(app, maxAttempts: 2, deploy: true, extraFile: FakeDeploymentRules.FailBuildMarker);
        var id = fixture.Project.Id;

        await DriveToReviewAsync(app, id);
        await AgentJobs.RunQueuedAsync(app, id);

        var checks = await ChecksAsync(app, id);
        Assert.Equal([(0, BuildCheckStatus.Failed), (1, BuildCheckStatus.Failed), (2, BuildCheckStatus.Exhausted)], checks.Select(c => (c.Attempt, c.Status)));
        var fixes = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().Where(s => s.ProjectId == id && s.Origin == StepOrigin.BuildFix).OrderBy(s => s.StepNumber).ToListAsync(Ct));
        Assert.Equal([(3, (Guid?)checks[0].Id), (4, (Guid?)checks[1].Id)], fixes.Select(s => (s.StepNumber, s.BuildCheckId)));
        Assert.Equal(3, (await JobsOfAsync(app, id, JobTypes.PreviewRollout)).Count); // no new rollout after the exhausted check

        // Review starts with the failure as a finding, and the prompt says the check was exhausted after 2 attempts.
        var prompt = Assert.Single(review.Prompts);
        var cycle = prompt[prompt.IndexOf("<review_cycle>", StringComparison.Ordinal)..prompt.IndexOf("</review_cycle>", StringComparison.Ordinal)];
        Assert.Contains(BuildCheckText.Exhausted(2), cycle, StringComparison.Ordinal);
        var report = await ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        Assert.Contains(report.Violations, v => v.FailureId == "failure:preview:vercel");

        // FR-259: the endpoint lists the checks newest first, with each failed check's fix step.
        var listed = await ProjectApi.ReadAsync<List<BuildCheckDto>>(await fixture.Viewer.Client.GetAsync(
            new Uri($"{fixture.Path}/versions/{checks[0].ReleaseVersionId}/build-checks", UriKind.Relative), Ct));
        Assert.Equal([2, 1, 0], listed.Select(c => c.Attempt));
        Assert.Equal([[], [4], [3]], listed.Select(c => c.FixStepNumbers.ToArray()));
        Assert.Equal("failure:preview:vercel", Assert.Single(listed[2].Failures).FailureId);
    }

    [Fact]
    public async Task FR258_With_max_0_no_build_check_is_created_and_execution_goes_straight_to_review()
    {
        var app = DeploymentHarness.Fake(factory);
        using var fixture = await ExecutingAsync(app, maxAttempts: 0, deploy: true);
        var id = fixture.Project.Id;

        await AgentJobs.RunQueuedAsync(app, id);

        Assert.Empty(await ChecksAsync(app, id));
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);
    }

    [Fact]
    public async Task FR258_An_optimisation_plan_in_the_next_cycle_starts_again_at_attempt_0()
    {
        var app = AgentJobs.WithResponders(factory, new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.82m, 0.95m], OptimizationStepsByCycle = [1, 0] }));
        using var fixture = await ExecutingAsync(app, maxAttempts: 5, deploy: false);
        var id = fixture.Project.Id;
        await AgentJobs.RunQueuedAsync(app, id);
        var draft = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.OptimizationPlan));

        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve"));
        await AgentJobs.RunQueuedAsync(app, id);

        var checks = await ChecksAsync(app, id);
        Assert.Equal([(0, 0, BuildCheckStatus.Skipped), (1, 0, BuildCheckStatus.Skipped)], checks.Select(c => (c.ReviewCycle, c.Attempt, c.Status)));
        Assert.Equal((Phase.Review, 2), ((await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase, (await LifecycleSeed.ProjectAsync(app, id)).ReviewCycle));
    }

    [Fact]
    public async Task FR258_maxBuildFixAttempts_defaults_to_5_is_edited_like_maxReviewCycles_and_11_is_400_VALIDATION()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        Assert.Equal(5, fixture.Project.MaxBuildFixAttempts);

        var updated = await ProjectApi.ReadAsync<ProjectDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, fixture.Path, new { maxBuildFixAttempts = 2 }));
        Assert.Equal(2, updated.MaxBuildFixAttempts);
        var off = await ProjectApi.ReadAsync<ProjectDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, fixture.Path, new { maxBuildFixAttempts = 0 }));
        Assert.Equal(0, off.MaxBuildFixAttempts);

        await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Owner.Client, fixture.Path, new { maxBuildFixAttempts = 11 }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Owner.Client, fixture.Path, new { maxBuildFixAttempts = -1 }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Viewer.Client, fixture.Path, new { maxBuildFixAttempts = 3 }), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        Assert.Equal(0, (await LifecycleSeed.ProjectAsync(factory, fixture.Project.Id)).MaxBuildFixAttempts);
    }

    // ---------- FR-259: endpoint and live event ----------

    [Fact]
    public async Task FR259_Viewers_list_a_versions_build_checks_and_an_unknown_project_or_version_is_404()
    {
        var app = AgentJobs.WithResponders(factory, new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));
        using var fixture = await ExecutingAsync(app, maxAttempts: 5, deploy: false);
        var id = fixture.Project.Id;
        await AgentJobs.RunQueuedAsync(app, id);
        var versionId = await DeploymentHarness.VersionIdAsync(app, id);

        var listed = await ProjectApi.ReadAsync<List<BuildCheckDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/versions/{versionId}/build-checks", UriKind.Relative), Ct));
        var item = Assert.Single(listed);
        Assert.Equal((versionId, 0, 0, BuildCheckStatus.Skipped), (item.ReleaseVersionId, item.ReviewCycle, item.Attempt, item.Status));
        Assert.Empty(item.Failures);
        Assert.Empty(item.FixStepNumbers);
        Assert.NotNull(item.CompletedAt);

        await Problems.AssertAsync(
            await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/versions/{Guid.NewGuid()}/build-checks", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{Guid.NewGuid()}/versions/{versionId}/build-checks", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR259_A_build_check_change_is_pushed_live_as_buildCheckUpdated_with_the_item()
    {
        var app = AgentJobs.WithResponders(factory, new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(app, id, maxAttempts: 5);
        await using var viewer = await LiveClient.ConnectAsync(app, fixture.Viewer);
        var updates = Channel.CreateUnbounded<JsonElement>();
        viewer.Connection.On<JsonElement>(Aide.Api.Features.Live.ProjectsHub.BuildCheckUpdatedMethod, m => updates.Writer.TryWrite(m));
        await viewer.JoinAsync(id);

        await ProjectApi.ReadAsync<ExecutionStateDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(app, id);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var statuses = new List<string?>();
        while (!statuses.Contains("skipped"))
        {
            var message = await updates.Reader.ReadAsync(timeout.Token);
            Assert.Equal(id.ToString(), message.GetProperty("projectId").GetString());
            statuses.Add(message.GetProperty("check").GetProperty("status").GetString());
            Assert.Equal(0, message.GetProperty("check").GetProperty("attempt").GetInt32());
        }

        Assert.Equal(["waiting", "skipped"], statuses);
    }

    // ---------- §4.47: cancellation, supersession and ended jobs ----------

    private static BuildCheck Seeded(Guid projectId, Guid versionId, int attempt, BuildCheckStatus status) => new()
    {
        ProjectId = projectId,
        ReleaseVersionId = versionId,
        ReviewCycle = 0,
        Attempt = attempt,
        HeadSha = $"{attempt}abcdef0123456789abcdef0123456789abcdef0",
        Status = status,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30 + attempt),
        CompletedAt = status == BuildCheckStatus.Waiting ? null : DateTimeOffset.UtcNow.AddMinutes(-29 + attempt),
    };

    [Fact]
    public async Task FR258_S4_47_After_a_rollback_re_execution_starts_again_at_attempt_0_with_the_whole_fix_budget()
    {
        var app = AgentJobs.WithResponders(factory, new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Review, 6, ApprovalMode.Advisory, reviewCycle: 1, maxBuildFixAttempts: 2);
        (Phase From, Phase To, int Version)[] history =
        [
            (Phase.Refinement, Phase.Specification, 2),
            (Phase.Specification, Phase.Planning, 3),
            (Phase.Planning, Phase.Planning, 4),
            (Phase.Planning, Phase.Execution, 5),
            (Phase.Execution, Phase.Review, 6),
        ];
        for (var i = 0; i < history.Length; i++)
        {
            await LifecycleSeed.AddTransitionAsync(app, id, history[i].From, history[i].To, history[i].Version, t0.AddMinutes(i));
        }

        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, projectVersionAtCreation: 1, content: "# Todo API\n\n## Summary\nManage todos.");
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 2, content: "# Spec\n\n## API Contracts\nNo HTTP API.\n");
        var plan = await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ImplementationPlan, projectVersionAtCreation: 4, content: AgentJobs.PlanMarkdown(2));
        await LifecycleSeed.AddStepsAsync(app, id, plan.Id, 2, StepStatus.Completed);
        var versionId = await DeploymentHarness.VersionIdAsync(app, id);

        // Cycle 0 used its whole budget (two fixes, then exhausted) and a later check still waits.
        var fixedOnce = Seeded(id, versionId, 0, BuildCheckStatus.Failed);
        var fixedTwice = Seeded(id, versionId, 1, BuildCheckStatus.Failed);
        var exhausted = Seeded(id, versionId, 2, BuildCheckStatus.Exhausted);
        var waiting = Seeded(id, versionId, 3, BuildCheckStatus.Waiting);
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.BuildChecks.AddRange(fixedOnce, fixedTwice, exhausted, waiting);
            db.ExecutionSteps.AddRange(new[] { (Check: fixedOnce, Number: 3), (Check: fixedTwice, Number: 4) }.Select(f => new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = plan.Id,
                StepNumber = f.Number,
                Title = "Fix failure:preview:vercel",
                Task = "Fix it.",
                DeclaredFiles = [new DeclaredFile($"docs/fix-{f.Number}.md", FileAction.Create)],
                Status = StepStatus.Completed,
                Origin = StepOrigin.BuildFix,
                BuildCheckId = f.Check.Id,
            }));
            await db.SaveChangesAsync(Ct);
        });

        // Roll back to checkpoint 5 (execution): the waiting check is cancelled and every check in force superseded.
        var rollback = await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/rollback", new { targetCheckpoint = 5, reason = "Try again" }, Ct);
        Assert.True(rollback.IsSuccessStatusCode, await rollback.Content.ReadAsStringAsync(Ct));
        var afterRollback = await ChecksAsync(app, id);
        Assert.All(afterRollback, c => Assert.NotNull(c.SupersededAt));
        Assert.Equal(BuildCheckStatus.Cancelled, afterRollback.Single(c => c.Id == waiting.Id).Status);
        Assert.Equal(BuildCheckStatus.Exhausted, afterRollback.Single(c => c.Id == exhausted.Id).Status); // history kept
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Execution, 0), (project.CurrentPhase, project.ReviewCycle));

        // Re-execution: the PR job starts a new check at attempt 0 (not "advance" on the stale exhausted one), with no fix used.
        var retry = await fixture.Owner.Client.PostAsync(new Uri($"{fixture.Path}/git/pull-request", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        await AgentJobs.RunQueuedAsync(app, id);

        var fresh = Assert.Single(await ChecksAsync(app, id), c => c.SupersededAt is null);
        Assert.Equal((0, 0, BuildCheckStatus.Skipped), (fresh.ReviewCycle, fresh.Attempt, fresh.Status));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<BuildCheckService>().FixesUsedAsync(fresh, Ct));
        }

        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);
    }

    [Fact]
    public async Task S4_47_Invalidating_the_versions_work_cancels_a_waiting_check_and_supersedes_the_checks_in_force()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Execution, 5, ApprovalMode.Advisory, maxBuildFixAttempts: 5);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 3, projectVersionAtCreation: 2);
        var plan = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, projectVersionAtCreation: 3);
        await LifecycleSeed.AddStepsAsync(factory, id, plan.Id, 2, StepStatus.Completed);
        var versionId = await DeploymentHarness.VersionIdAsync(factory, id);
        var exhausted = Seeded(id, versionId, 0, BuildCheckStatus.Exhausted);
        var waiting = Seeded(id, versionId, 1, BuildCheckStatus.Waiting);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            db.BuildChecks.AddRange(exhausted, waiting);
            await db.SaveChangesAsync(Ct);
        });

        var spec4 = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, "technical_specification", "# Spec v4"), HttpStatusCode.Created);
        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, spec4.Id, "approve"));

        var checks = await ChecksAsync(factory, id);
        Assert.Equal(BuildCheckStatus.Cancelled, checks.Single(c => c.Id == waiting.Id).Status);
        Assert.Equal(BuildCheckStatus.Exhausted, checks.Single(c => c.Id == exhausted.Id).Status);
        Assert.All(checks, c => Assert.NotNull(c.SupersededAt));
        var cancelled = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.BuildCheckCompleted)).Payload);
        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
    }

    [Fact]
    public async Task S4_47_A_check_waiting_when_the_project_leaves_execution_is_cancelled()
    {
        var app = DeploymentHarness.Fake(factory);
        using var fixture = await ExecutingAsync(app, maxAttempts: 5, deploy: true);
        var id = fixture.Project.Id;
        await AgentJobs.RunQueuedAsync(app, id);
        var job = Assert.Single(await QueuedAsync(app, id, JobTypes.BuildCheck));

        // A writer advanced to review by hand while the check waited (execution stopped).
        await ProjectApi.WithDbAsync(app, db => db.Projects.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CurrentPhase, Phase.Review).SetProperty(p => p.Status, ProjectStatus.Reviewing), Ct));
        Assert.False(await DeploymentHarness.RunOnceAsync(app, job.Id));

        var check = Assert.Single(await ChecksAsync(app, id));
        Assert.Equal(BuildCheckStatus.Cancelled, check.Status);
        Assert.NotNull(check.CompletedAt);
    }

    [Theory]
    [InlineData(JobTypes.BuildCheck, BuildCheckStatus.Waiting)]
    [InlineData(JobTypes.BuildFix, BuildCheckStatus.Failed)]
    public async Task S4_47_A_dead_lettered_build_check_or_fix_job_exhausts_the_check_and_queues_the_advance_to_review(string jobType, BuildCheckStatus status)
    {
        var (fixture, _, versionId) = await FailedChecksAsync(factory);
        using var __ = fixture;
        var id = fixture.Project.Id;
        var check = Seeded(id, versionId, 0, status);
        var job = new Job
        {
            Type = jobType,
            Payload = AideJson.Serialize(new BuildCheckJobPayload(id, check.Id)),
            ProjectId = id,
            Status = JobStatus.Dead,
            RunAfter = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            db.BuildChecks.Add(check);
            db.Jobs.Add(job);
            await db.SaveChangesAsync(Ct);
        });

        // Like the worker: the ended hooks run with its context, which it then saves.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Aide.Infrastructure.Persistence.AideDbContext>();
            foreach (var hook in scope.ServiceProvider.GetServices<Aide.Api.Jobs.IJobEndedHook>())
            {
                await hook.OnJobEndedAsync(db, new Aide.Api.Jobs.JobEnded(job.Id, jobType, id, DeadLettered: true, "boom"), Ct);
            }

            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(BuildCheckStatus.Exhausted, Assert.Single(await ChecksAsync(factory, id)).Status);
        var pr = Assert.Single(await QueuedAsync(factory, id, JobTypes.PullRequest));
        Assert.Contains(BuildCheckService.ExhaustedTrigger, pr.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S4_47_Worked_example_attempts_0_and_1_fail_with_different_errors_attempt_2_passes_3_checks_2_fixes_then_review_with_no_new_rollout()
    {
        var app = AgentJobs.WithResponders(DeploymentHarness.Fake(factory), new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));
        using var fixture = await ExecutingAsync(app, maxAttempts: 5, deploy: true);
        var id = fixture.Project.Id;
        var errors = new[]
        {
            ModuleLog,
            "> next build\n./app/page.tsx:4:10\nType error: Property 'id' does not exist on type 'Todo'.\nError: Command \"npm run build\" exited with 1",
        };

        // Attempt 0 and 1: the head's preview fails (each with its own error); a fix is planned and run after each.
        await AgentJobs.RunQueuedAsync(app, id);
        foreach (var log in errors)
        {
            await SettleNewestRolloutAsync(app, id);
            await FailNewestPreviewAsync(app, id, log);
            await DeploymentHarness.SettleAsync(app, Assert.Single(await QueuedAsync(app, id, JobTypes.BuildCheck)).Id);
            await AgentJobs.RunQueuedAsync(app, id);
        }

        // Attempt 2: the preview builds; the check passes and review starts.
        await SettleNewestRolloutAsync(app, id);
        await DeploymentHarness.SettleAsync(app, Assert.Single(await QueuedAsync(app, id, JobTypes.BuildCheck)).Id);
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);
        await AgentJobs.RunQueuedAsync(app, id);

        var checks = await ChecksAsync(app, id);
        Assert.Equal([(0, BuildCheckStatus.Failed), (1, BuildCheckStatus.Failed), (2, BuildCheckStatus.Passed)], checks.Select(c => (c.Attempt, c.Status)));
        Assert.NotEqual(Assert.Single(checks[0].Failures).Fingerprint, Assert.Single(checks[1].Failures).Fingerprint);
        Assert.Contains(ModuleNotFound, checks[0].Failures[0].Excerpt, StringComparison.Ordinal);
        Assert.Contains("Type error: Property 'id' does not exist", checks[1].Failures[0].Excerpt, StringComparison.Ordinal);
        var fixes = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().Where(s => s.ProjectId == id && s.Origin == StepOrigin.BuildFix).OrderBy(s => s.StepNumber).ToListAsync(Ct));
        Assert.Equal([(3, (Guid?)checks[0].Id), (4, (Guid?)checks[1].Id)], fixes.Select(s => (s.StepNumber, s.BuildCheckId)));

        // Three rollouts (one per checked commit) and none after the pass; review used the third.
        var rollouts = await JobsOfAsync(app, id, JobTypes.PreviewRollout);
        Assert.Equal(3, rollouts.Count);
        var report = await ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        var used = JsonDocument.Parse(report.ExternalChecks!).RootElement.GetProperty("preview").GetProperty("rollout").GetProperty("rolloutId").GetString();
        Assert.Equal(rollouts[^1].Id.ToString(), used);
    }

    [Fact]
    public async Task FR258_Creating_a_project_with_maxBuildFixAttempts_11_is_400_VALIDATION()
    {
        using var session = await ApiSession.RegisterAsync(factory);
        var response = await session.Client.PostAsJsonAsync(
            "/api/projects",
            new { name = ProjectApi.UniqueName(), idea = "Build a todo API.", git = new { provider = "local" }, maxBuildFixAttempts = 11 },
            Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }
}
