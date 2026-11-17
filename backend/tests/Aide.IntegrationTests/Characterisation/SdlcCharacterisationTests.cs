using System.Net;
using Aide.Domain.Artifacts;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Agents;
using Aide.IntegrationTests.Releases;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Tevrin.Pack.Sdlc;

namespace Aide.IntegrationTests.Characterisation;

/// <summary>
/// FR-260, FR-261, §4.58 (0.7.0 M54): the SDLC characterisation scenarios. Each drives one project end to end through the
/// HTTP API and the queued jobs on the Fake LLM, Local Git (GitHub as <see cref="FakeGitHubApi"/> over a bare repository),
/// Fake deploy and Fake tracker providers, records its snapshot and compares it with the committed
/// <c>Characterisation/Snapshots/&lt;scenario&gt;.json</c>. Every later 0.7.0 milestone must leave these files unchanged
/// (see README.md in this folder).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class SdlcCharacterisationTests(AideApiFactory factory)
{
    private const string GitHubToken = "ghp_characterisationToken0123456789abcd";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The app whose Fake Code Review scores every category <paramref name="scores"/>[cycle − 1] (the last repeats).</summary>
    private WebApplicationFactory<Program> Reviewing(params decimal[] scores) =>
        AgentJobs.WithResponders(factory, new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = scores }));

    private static async Task AssertSnapshotAsync(SdlcFlow flow, string scenario) =>
        SnapshotAssert.Match(scenario, await flow.SnapshotAsync(scenario));

    /// <summary>Creation → refinement → specification → planning → execution → review (release-eligible) → release → released.</summary>
    private static async Task HappyPathAsync(SdlcFlow flow)
    {
        await flow.ToReviewAsync();
        await flow.PendingGateAsync(SdlcGates.PreRelease);
        await flow.ReleaseAsync();
    }

    // ---------- (1)–(3) the happy path in each approval mode ----------

    [Fact]
    public async Task FR260_strict_happy_path()
    {
        await using var app = Reviewing(0.93m);
        using var flow = await SdlcFlow.CreateAsync(factory, app, "strict");

        await HappyPathAsync(flow);

        // FR-260 acceptance: every checkpoint from refinement (1) to release (6), and the three strict gates approved.
        var transitions = await LifecycleSeed.TransitionsAsync(flow.App, flow.Id);
        Assert.Equal([1, 2, 3, 4, 5, 6], transitions.Select(t => t.Checkpoint).Distinct().Order());
        Assert.Equal((Phase.Release, 6), (transitions[^1].ToPhase, transitions[^1].Checkpoint));
        var gates = await LifecycleSeed.GatesAsync(flow.App, flow.Id);
        Assert.Equal(
            [(SdlcGates.SpecApproval, GateStatus.Approved), (SdlcGates.PlanApproval, GateStatus.Approved), (SdlcGates.PreRelease, GateStatus.Approved)],
            gates.Select(g => (g.Type, g.Status)));
        await AssertSnapshotAsync(flow, "strict_happy_path");
    }

    [Fact]
    public async Task FR260_advisory_happy_path()
    {
        await using var app = Reviewing(0.93m);
        using var flow = await SdlcFlow.CreateAsync(factory, app, "advisory");

        await HappyPathAsync(flow);

        await AssertSnapshotAsync(flow, "advisory_happy_path");
    }

    [Fact]
    public async Task FR260_autonomous_happy_path()
    {
        await using var app = Reviewing(0.93m);
        using var flow = await SdlcFlow.CreateAsync(factory, app, "autonomous");

        await HappyPathAsync(flow);

        await AssertSnapshotAsync(flow, "autonomous_happy_path");
    }

    /// <summary>FR-260 acceptance: the same scenario on two projects gives byte-identical snapshots (nothing run-specific leaks in).</summary>
    [Fact]
    public async Task FR260_A_scenario_recorded_twice_gives_byte_identical_snapshots()
    {
        await using var app = Reviewing(0.93m);
        var snapshots = new List<string>();
        for (var run = 0; run < 2; run++)
        {
            using var flow = await SdlcFlow.CreateAsync(factory, app, "strict");
            await HappyPathAsync(flow);
            snapshots.Add(await flow.SnapshotAsync("strict_happy_path"));
        }

        if (!string.Equals(snapshots[0], snapshots[1], StringComparison.Ordinal))
        {
            Assert.Fail("Two runs of the strict scenario gave different snapshots:\n" + SnapshotAssert.Diff("first run", snapshots[0], snapshots[1]));
        }
    }

    // ---------- (4), (5) rejections ----------

    [Fact]
    public async Task FR260_gate_rejected_then_approved()
    {
        using var flow = await SdlcFlow.CreateAsync(factory, factory, "advisory");
        await flow.ToSpecificationAsync();
        await flow.ApproveDraftAsync(SdlcArtifacts.TechnicalSpecification);
        Assert.True(await flow.AdvanceAsync(), "Advisory mode gates the entry to planning.");

        // FR-13: the rejection re-runs the spec generator with the comment; the new draft goes through the gate again.
        await flow.DecideGateAsync(SdlcGates.SpecApproval, "reject", new { comment = "Add rate limits to every endpoint." });
        await flow.RunJobsAsync();
        await flow.ToPlanningAsync();

        await AssertSnapshotAsync(flow, "gate_rejected_then_approved");
    }

    [Fact]
    public async Task FR260_draft_rejected()
    {
        using var flow = await SdlcFlow.CreateAsync(factory, factory, "advisory");
        await flow.ToSpecificationAsync();

        // FR-17: rejecting the agent's draft re-runs it with the comment.
        await flow.RejectDraftAsync(SdlcArtifacts.TechnicalSpecification, "Add authentication to every endpoint.");
        await flow.RunJobsAsync();
        await flow.ToPlanningAsync();

        await AssertSnapshotAsync(flow, "draft_rejected");
    }

    // ---------- (6), (7) review cycles and escalations ----------

    [Fact]
    public async Task FR260_two_review_cycles_then_release_eligible()
    {
        // The default Fake review: cycle 1 scores 0.86 (review_required, a one-step optimisation plan), cycle 2 0.93.
        using var flow = await SdlcFlow.CreateAsync(factory, factory, "advisory");
        await flow.ToReviewAsync();

        await flow.ApproveDraftAsync(SdlcArtifacts.OptimizationPlan);
        await flow.RunJobsAsync();

        await flow.ExpectPhaseAsync(Phase.Review);
        await flow.PendingGateAsync(SdlcGates.PreRelease);
        await AssertSnapshotAsync(flow, "two_review_cycles_then_release_eligible");
    }

    [Fact]
    public async Task FR260_escalation_continue()
    {
        await using var app = Reviewing(0.70m, 0.93m);
        using var flow = await SdlcFlow.CreateAsync(factory, app, "advisory");
        await flow.ToReviewAsync();

        // FR-14: continue allows one more cycle; the optimisation plan re-enters execution, and cycle 2 is release-eligible.
        await flow.DecideGateAsync(EngineGates.Escalation, "approve", new { decision = "continue" });
        await flow.ApproveDraftAsync(SdlcArtifacts.OptimizationPlan);
        await flow.RunJobsAsync();

        await flow.PendingGateAsync(SdlcGates.PreRelease);
        await AssertSnapshotAsync(flow, "escalation_continue");
    }

    [Fact]
    public async Task FR260_escalation_rollback()
    {
        await using var app = Reviewing(0.70m);
        using var flow = await SdlcFlow.CreateAsync(factory, app, "advisory");
        await flow.ToReviewAsync();

        // FR-14: rollback to checkpoint 3 (planning).
        await flow.DecideGateAsync(EngineGates.Escalation, "approve", new { decision = "rollback", targetCheckpoint = 3, comment = "Re-plan the API." });
        await flow.RunJobsAsync();

        await flow.ExpectPhaseAsync(Phase.Planning);
        await AssertSnapshotAsync(flow, "escalation_rollback");
    }

    [Fact]
    public async Task FR260_escalation_accept()
    {
        await using var app = Reviewing(0.70m);
        using var flow = await SdlcFlow.CreateAsync(factory, app, "advisory");
        await flow.ToReviewAsync();

        // FR-14: accept makes the project release-eligible through the pre_release gate.
        await flow.DecideGateAsync(EngineGates.Escalation, "approve", new { decision = "accept", comment = "Good enough for a first release." });
        await flow.ReleaseAsync();

        await AssertSnapshotAsync(flow, "escalation_accept");
    }

    // ---------- (8), (9) cascade and rollback ----------

    [Fact]
    public async Task FR260_new_project_request_approved_mid_execution()
    {
        using var flow = await SdlcFlow.CreateAsync(factory, factory, "advisory");
        await flow.ToSpecificationAsync();
        await flow.ToPlanningAsync();
        await flow.ToExecutionAsync();
        await flow.StartExecutionAsync();

        // Step 1 runs; the next execution job is queued when the request changes.
        await AgentJobs.RunAsync(factory, await flow.QueuedJobAsync(JobTypes.Execution));

        // FR-19: a new request version is approved: the cascade invalidates the downstream work and falls back.
        await flow.SaveDraftAsync("project_request", "# Todo API\n\n## Summary\nA todo API for small teams, with due dates and reminders.\n");
        await flow.ApproveDraftAsync(SdlcArtifacts.ProjectRequest);

        // §4.4: the spec and plan are invalidated and the project falls back to specification (their producing phase).
        await flow.ExpectPhaseAsync(Phase.Specification);
        Assert.All(await AgentJobs.ArtifactsAsync(factory, flow.Id, SdlcArtifacts.TechnicalSpecification), a => Assert.Equal(ArtifactStatus.Invalidated, a.Status));
        Assert.All(await AgentJobs.ArtifactsAsync(factory, flow.Id, SdlcArtifacts.ImplementationPlan), a => Assert.Equal(ArtifactStatus.Invalidated, a.Status));
        await flow.RunJobsAsync();

        await AssertSnapshotAsync(flow, "new_project_request_approved_mid_execution");
    }

    [Fact]
    public async Task FR260_rollback_to_checkpoint_2()
    {
        await using var app = Reviewing(0.93m);
        using var flow = await SdlcFlow.CreateAsync(factory, app, "advisory");
        await flow.ToReviewAsync();
        await flow.PendingGateAsync(SdlcGates.PreRelease);

        // FR-44: from review (checkpoint 5) back to specification (checkpoint 2).
        await flow.RollbackAsync(2, "Back to the specification.");
        await flow.RunJobsAsync();

        await flow.ExpectPhaseAsync(Phase.Specification);
        await AssertSnapshotAsync(flow, "rollback_to_checkpoint_2");
    }

    // ---------- (10) awaiting merge (GitHub fake over a bare repository; execution seeded as in MergeFlowTests) ----------

    [Fact]
    public async Task FR260_release_refused_awaiting_merge_then_merged()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var github = new MergeFlowTests.GitHubState(remote) { ReviewDecision = "REVIEW_REQUIRED", MergeStateStatus = "BLOCKED", RequiredApprovals = 1 };
        github.MergeResponse = _ => (HttpStatusCode.MethodNotAllowed, """{"message":"At least 1 approving review is required by reviewers with write access."}""");
        await using var app = remote.App(Reviewing(0.93m), github.Api, out _);
        using var flow = await GitHubFlowAsync(app, remote);
        github.Branch = MergeFlowTests.BranchOf(flow.Fixture);
        await ReleaseFlowTests.StartExecutionAsync(app, flow.Fixture);
        await flow.RunJobsAsync();

        // FR-65: branch protection refuses the merge: awaiting_merge.
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, flow.Fixture);
        await flow.RunJobsAsync();
        await flow.ExpectPhaseAsync(Phase.Release, ProjectStatus.AwaitingMerge);

        // A reviewer merges on GitHub; the merge poll tags it and the release completes.
        github.MergedAs(ReleaseFlowTests.MergeInBare(remote, github.Branch));
        await AgentJobs.RunCoreAsync(app, await flow.QueuedJobAsync(JobTypes.MergePoll));

        await flow.ExpectPhaseAsync(Phase.Release, ProjectStatus.Released);
        await AssertSnapshotAsync(flow, "release_refused_awaiting_merge_then_merged");
    }

    [Fact]
    public async Task FR260_merge_abandoned()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var github = new MergeFlowTests.GitHubState(remote);
        await using var app = remote.App(Reviewing(0.93m), github.Api, out _);
        using var flow = await GitHubFlowAsync(app, remote);
        github.Branch = MergeFlowTests.BranchOf(flow.Fixture);
        await flow.SetMergeStrategyAsync("manual");
        await ReleaseFlowTests.StartExecutionAsync(app, flow.Fixture);
        await flow.RunJobsAsync();
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, flow.Fixture);
        await flow.RunJobsAsync();
        await flow.ExpectPhaseAsync(Phase.Release, ProjectStatus.AwaitingMerge);

        // FR-65: the PR is closed without merging: PR_CLOSED, awaiting_merge → releasing.
        github.State = "closed";
        await AgentJobs.RunCoreAsync(app, await flow.QueuedJobAsync(JobTypes.MergePoll));

        await flow.ExpectPhaseAsync(Phase.Release, ProjectStatus.Releasing);
        await AssertSnapshotAsync(flow, "merge_abandoned");
    }

    // ---------- (11) new version, (12) stack inference ----------

    [Fact]
    public async Task FR260_new_version_after_release()
    {
        await using var app = Reviewing(0.93m);
        using var flow = await SdlcFlow.CreateAsync(factory, app, "advisory");
        await HappyPathAsync(flow);

        // FR-60: 0.2.0 starts in refinement at checkpoint 1 on the released code.
        await flow.StartVersionAsync("0.2.0", "Add CSV export of todos.");
        await flow.RunJobsAsync();
        await flow.ExpectPhaseAsync(Phase.Refinement);
        await flow.RefineAsync();

        await AssertSnapshotAsync(flow, "new_version_after_release");
    }

    [Fact]
    public async Task FR260_stack_inference_analysis_then_refinement()
    {
        var importRoot = Path.Combine(Path.GetTempPath(), "aide-tests-characterisation", Guid.NewGuid().ToString("N"));
        try
        {
            await CreateVueSupabaseRepositoryAsync(Path.Combine(importRoot, "vue-supabase"));
            await using var app = factory.WithWebHostBuilder(b => b.UseSetting("Git:Local:ImportRoot", importRoot));
            using var flow = await SdlcFlow.CreateAsync(factory, app, new Dictionary<string, object?>
            {
                ["name"] = "Notes",
                ["idea"] = "Add sharing of notes.",
                ["source"] = "import",
                ["git"] = new { mode = "local", localSourcePath = "vue-supabase" },
                ["import"] = new { releasedTag = (string?)null, developmentVersion = "0.1.0" },
                ["stackSource"] = "infer",
            });
            flow.AddLiteral(importRoot, "<import-root>");
            await flow.ExpectPhaseAsync(Phase.Analysis);

            // FR-180–FR-185: the Repository Analyst, then the Stack Analyst's profile draft; approved, then on to refinement.
            await flow.RunJobsAsync();
            await flow.ApproveDraftAsync(SdlcArtifacts.StackProfile);
            Assert.False(await flow.AdvanceAsync(), "Entering refinement needs no gate.");
            await flow.ExpectPhaseAsync(Phase.Refinement);
            await flow.RunJobsAsync();

            await AssertSnapshotAsync(flow, "stack_inference_analysis_then_refinement");
        }
        finally
        {
            TestGit.DeleteTree(importRoot);
        }
    }

    // ---------- (13) build check (Fake deploy; execution seeded as in BuildCheckTests) ----------

    [Fact]
    public async Task FR260_build_check_failed_once_then_fixed()
    {
        await using var app = AgentJobs.WithResponders(DeploymentHarness.Fake(factory), new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));
        using var flow = await SdlcFlow.CreateAsync(factory, app);
        var owner = flow.Fixture.Owner.User!.Id;
        await DeploymentHarness.BindAsync(app, flow.Id, await DeploymentHarness.SeedConnectionAsync(app, owner), owner);
        await SeedExecutionAsync(app, flow.Id, FakeDeploymentRules.FailBuildMarker);
        await flow.StartExecutionAsync();

        // FR-254–FR-256: attempt 0's preview builds a commit with the Fake failure marker and fails; a fix is planned.
        await flow.RunJobsAsync();
        await SettleNewestRolloutAsync(app, flow.Id);
        await DeploymentHarness.SettleAsync(app, (await flow.QueuedJobAsync(JobTypes.BuildCheck)).Id);
        await flow.QueuedJobAsync(JobTypes.BuildFix);

        // The cause is removed on the branch (as a person would) before the fix step runs, so attempt 1 passes.
        var workingCopy = Path.Combine(factory.WorkspacesRoot, flow.Id.ToString("D"));
        await TestGit.RunAsync(workingCopy, Ct, "rm", "-q", FakeDeploymentRules.FailBuildMarker);
        await TestGit.RunAsync(workingCopy, Ct, "commit", "-q", "-m", "Remove the Fake build failure marker");
        await DriveToReviewAsync(app, flow);

        await AssertSnapshotAsync(flow, "build_check_failed_once_then_fixed");
    }

    // ---------- (14) integrity ----------

    [Fact]
    public async Task FR260_corrupted_artifact_regenerated()
    {
        using var flow = await SdlcFlow.CreateAsync(factory, factory, "advisory");
        await flow.ToSpecificationAsync();

        // FR-20: the spec draft is altered behind the API's back; the next read finds it corrupted and regenerates it.
        await flow.TamperDraftAsync(SdlcArtifacts.TechnicalSpecification, "## 1. Overview\nEdited behind the API's back.");
        await flow.ReadArtifactAsync("/technical_specification/latest");
        await flow.RunJobsAsync();
        var versions = await AgentJobs.ArtifactsAsync(factory, flow.Id, SdlcArtifacts.TechnicalSpecification);
        Assert.Equal([(1, ArtifactStatus.Corrupted), (2, ArtifactStatus.Draft)], versions.Select(v => (v.Version, v.Status)));
        await flow.ToPlanningAsync();

        await AssertSnapshotAsync(flow, "corrupted_artifact_regenerated");
    }

    // ---------- helpers ----------

    private async Task<SdlcFlow> GitHubFlowAsync(WebApplicationFactory<Program> app, BareRemote remote)
    {
        var flow = await SdlcFlow.CreateAsync(factory, app, new Dictionary<string, object?>
        {
            ["name"] = SdlcFlow.ProjectName,
            ["idea"] = SdlcFlow.Idea,
            ["approvalMode"] = "advisory",
            ["git"] = new { provider = "github_pat", repoUrl = BareRemote.RepoUrl, defaultBranch = "main", token = GitHubToken },
        });
        flow.AddLiteral(remote.BaseUrl, "<remote-url>/");
        flow.AddLiteral(remote.Root, "<remote>");
        return flow;
    }

    /// <summary>As <c>StackInferenceTests</c>: a Vue frontend in <c>frontend/</c> and Supabase migrations, one commit.</summary>
    private static async Task CreateVueSupabaseRepositoryAsync(string repo)
    {
        Directory.CreateDirectory(Path.Combine(repo, "frontend", "src"));
        Directory.CreateDirectory(Path.Combine(repo, "supabase", "migrations"));
        await TestGit.RunAsync(repo, Ct, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# Notes\n\nA Vue app on Supabase.\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "frontend", "package.json"), """{ "name": "notes", "dependencies": { "vue": "^3.5.13" }, "devDependencies": { "vite": "^7.0.0" } }""", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "frontend", "index.html"), "<div id=\"app\"></div>\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "frontend", "src", "main.ts"), "import { createApp } from 'vue';\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "supabase", "config.toml"), "project_id = \"notes\"\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "supabase", "migrations", "20260101000000_init.sql"), "create table notes (id uuid primary key);\n", Ct);
        await TestGit.RunAsync(repo, Ct, "add", ".");
        await TestGit.RunAsync(repo, Ct, "commit", "-q", "-m", "First");
    }

    /// <summary>As <c>BuildCheckTests</c>: execution at checkpoint 4 with a locked 2-step plan whose step 2 also writes <paramref name="extraFile"/>.</summary>
    private static async Task SeedExecutionAsync(WebApplicationFactory<Program> app, Guid id, string extraFile)
    {
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Execution, 4, ApprovalMode.Advisory, maxBuildFixAttempts: 5);
        await LifecycleSeed.AddArtifactAsync(app, id, SdlcArtifacts.ProjectRequest, content: "# Todo API\n\n## Summary\nManage todos.");
        await LifecycleSeed.AddArtifactAsync(app, id, SdlcArtifacts.TechnicalSpecification, content: "# Spec\n\n## Technical Architecture\nNode.js service.\n\n## API Contracts\nNo HTTP API.\n");
        var plan = await LifecycleSeed.AddArtifactAsync(app, id, SdlcArtifacts.ImplementationPlan, content: AgentJobs.PlanMarkdown(2));
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.ExecutionSteps.AddRange(Enumerable.Range(1, 2).Select(n => new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = plan.Id,
                StepNumber = n,
                Title = $"Step {n}",
                Task = $"Build part {n}.",
                DeclaredFiles = n == 2
                    ? [new DeclaredFile($"src/part{n}.js", FileAction.Create), new DeclaredFile(extraFile, FileAction.Create)]
                    : [new DeclaredFile($"src/part{n}.js", FileAction.Create)],
                Dependencies = n == 1 ? [] : [n - 1],
            }));
            await db.SaveChangesAsync(Ct);
        });
    }

    /// <summary>Settles the newest preview rollout if it's still queued.</summary>
    private static async Task SettleNewestRolloutAsync(WebApplicationFactory<Program> app, Guid id)
    {
        var rollout = await DeploymentHarness.RolloutJobAsync(app, id);
        if (rollout.Status == JobStatus.Queued)
        {
            await DeploymentHarness.SettleRolloutAsync(app, id, rollout.Id);
        }
    }

    /// <summary>As <c>BuildCheckTests</c>: queued jobs, the preview rollout, then the build checks, until review has run.</summary>
    private static async Task DriveToReviewAsync(WebApplicationFactory<Program> app, SdlcFlow flow, int maxRounds = 12)
    {
        for (var round = 0; round < maxRounds; round++)
        {
            await flow.RunJobsAsync();
            if ((await LifecycleSeed.ProjectAsync(app, flow.Id)).CurrentPhase == Phase.Review)
            {
                return;
            }

            await SettleNewestRolloutAsync(app, flow.Id);
            var checks = (await LifecycleSeed.JobsAsync(app, flow.Id)).Where(j => j.Type == JobTypes.BuildCheck && j.Status == JobStatus.Queued).ToList();
            foreach (var check in checks)
            {
                await DeploymentHarness.SettleAsync(app, check.Id);
            }
        }

        Assert.Fail($"The project didn't reach review in {maxRounds} rounds. {await flow.DescribeAsync()}");
    }
}
