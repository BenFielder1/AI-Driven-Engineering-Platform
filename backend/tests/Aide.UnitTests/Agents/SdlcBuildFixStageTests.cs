using System.Globalization;
using Aide.Api.Agents;
using Aide.Domain.Execution;
using Aide.Domain.RepoTools;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.Infrastructure.RepoTools;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-318, FR-319 (FR-256, §4.48), D-0.7.0-42: <c>build_fix</c>, bound on <c>execution</c>, on the engine runner records
/// what 0.6.1's <c>BuildFixJobHandler</c> got from <c>AgentRunner</c>: the prompt from the check (<c>FAILURES</c>,
/// <c>ERROR_SUMMARY</c>, <c>PLAN_STEPS</c>, <c>REPO_FILES</c>), runs keyed by the check (<c>build-fix-&lt;check&gt;-1</c>,
/// <c>-2</c>), the one corrective re-run whose reply is accepted whatever it says (a still invalid plan exhausts the check
/// in the job, no run fails), the repo tools on the working tree, and redelivery. The run stores nothing.
/// </summary>
public class SdlcBuildFixStageTests
{
    private const string Invalid = "Not a plan.";

    private static readonly Guid CheckId = Guid.CreateVersion7();

    private static readonly BuildFixPlanCheck PlanCheck = new(3, 5, null, [], null);

    private static string RunKey(int attempt) => string.Create(CultureInfo.InvariantCulture, $"build-fix-{CheckId:N}-{attempt}");

    /// <summary>The build fix's request and spec, as <c>BuildFixJobHandler</c> builds them (no failures recorded, no stack).</summary>
    private static SdlcAgentStageCall FixCall(SdlcAgentStageHarness harness, Tevrin.Engine.Runtime.Jobs.JobExecution job)
    {
        AgentRunRequest Request(int attempt, string? feedback) => new()
        {
            ProjectId = harness.Data.Project.Id,
            Agent = SdlcAgents.BuildFix,
            Job = job,
            RunKey = RunKey(attempt),
            Inputs = new Dictionary<string, string?>
            {
                ["FAILURES"] = BuildFixPlanning.RenderFailures([]),
                ["ERROR_SUMMARY"] = BuildFixPlanning.RenderErrorSummary([]),
                ["PLAN_STEPS"] = BuildFixPlanning.RenderPlanSteps([new BuildFixStepText(1, "API", "Build the API."), new BuildFixStepText(3, "Docs", "Write docs.")]),
                ["MAX_FILES_PER_STEP"] = "5",
                ["STACK_GUIDANCE"] = null,
                ["REPO_FILES"] = BuildFixPlanning.RenderRepoFiles(["src/app.ts", "package.json"]),
            },
            UserFeedback = feedback,
            Repo = RepoScope.WorkingTree,
        };

        var spec = new ArtifactAgentSpec(
            SdlcArtifacts.OptimizationPlan,
            text =>
            {
                var checkedReply = BuildFixPlanning.Process(text, PlanCheck);
                return checkedReply.IsFailure ? Result.Failure<string>(checkedReply.Error!) : Result.Success(text);
            },
            (_, error) => Request(2, BuildFixPlanning.CorrectiveFeedback(error)))
        {
            CorrectiveToContent = text => Result.Success(text),
            LogsRejection = true,
        };
        return new SdlcAgentStageCall(Request(1, null), spec);
    }

    private static SdlcAgentStageHarness Harness(IEnumerable<IFakeToolScript>? scripts = null, params string?[] replies)
    {
        var harness = new SdlcAgentStageHarness(SdlcAgents.BuildFix, scripts ?? [], replies);
        harness.Data.Project.CurrentPhase = Domain.Lifecycle.Phase.Execution;
        return harness;
    }

    [Fact]
    public async Task FR318_FR319_build_fix_success_runs_under_the_checks_run_key_with_0_6_1s_prompt_and_events()
    {
        using var harness = Harness(replies: [null]);
        var job = SdlcAgentStageHarness.Job();
        var call = FixCall(harness, job);

        var outcome = await harness.RunReplyJobAsync(call, "execution");

        var run = Assert.Single(harness.Store.AgentRuns);
        var first = AgentRunRules.RunId(job.Id, RunKey(1));
        var template = new EmbeddedPromptLibrary().Template(SdlcAgents.BuildFix);
        var rendered = template.Render(call.PromptValues()).Value;
        var request = Assert.Single(harness.Provider.Requests);
        Assert.Equal((rendered.System, rendered.User), (request.System, request.Messages[^1].Content));
        Assert.Equal((first, SdlcAgents.BuildFix, "execution", AgentRunStatus.Succeeded, (string?)null), (run.Id, run.Agent, run.Stage, run.Status, run.Error));
        Assert.Equal(first, outcome.RunId);
        Assert.Equal(4, BuildFixPlanning.Process(outcome.Text, PlanCheck).Value.Steps.Single().Number); // numbered after step 3
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal(
            AideJson.Serialize(new { runId = first, jobId = job.Id, agent = SdlcAgents.BuildFix, provider = "fake", model = "fake-1", promptVersion = template.Version, stepId = (Guid?)null, stack = (string?)null, agentKey = (string?)null, legacyAgentKey = (string?)null }),
            harness.Data.Events[0].Payload);
        Assert.Empty(harness.Store.Artifacts);
    }

    [Fact]
    public async Task FR318_FR319_build_fix_an_invalid_plan_is_rerun_once_under_its_second_run_key_with_every_reason()
    {
        using var harness = Harness(replies: [Invalid, null]);
        var job = SdlcAgentStageHarness.Job();

        var outcome = await harness.RunReplyJobAsync(FixCall(harness, job), "execution");

        var runs = harness.Store.AgentRuns;
        Assert.Equal(
            [(AgentRunRules.RunId(job.Id, RunKey(1)), AgentRunStatus.Succeeded, (string?)null), (AgentRunRules.RunId(job.Id, RunKey(2)), AgentRunStatus.Succeeded, null)],
            runs.Select(r => (r.Id, r.Status, r.Error)));
        Assert.Equal(BuildFixPlanning.CorrectiveFeedback(BuildFixPlanning.Process(Invalid, PlanCheck).Error!), runs[1].UserFeedback);
        Assert.Equal(runs[1].Id, outcome.RunId);
        Assert.True(BuildFixPlanning.Process(outcome.Text, PlanCheck).IsSuccess);
    }

    [Fact]
    public async Task FR318_FR319_build_fix_still_invalid_returns_the_second_reply_and_no_run_fails()
    {
        using var harness = Harness(replies: [Invalid]);
        var job = SdlcAgentStageHarness.Job();

        var outcome = await harness.RunReplyJobAsync(FixCall(harness, job), "execution");

        // 0.6.1: the job exhausts the check with the failures; both runs succeeded and nothing is RUN_FAILED.
        Assert.Equal(AgentRunRules.RunId(job.Id, RunKey(2)), outcome.RunId);
        Assert.True(BuildFixPlanning.Process(outcome.Text, PlanCheck).IsFailure);
        Assert.Equal([AgentRunStatus.Succeeded, AgentRunStatus.Succeeded], harness.Store.AgentRuns.Select(r => r.Status));
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED", "RUN_STARTED", "RUN_COMPLETED"], harness.Data.Events.Select(e => e.Type));
    }

    [Fact]
    public async Task FR318_FR319_FR71_build_fix_tool_calls_on_the_working_tree_are_the_runs()
    {
        var root = Directory.CreateTempSubdirectory("tevrin-fix-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "package.json"), "{}", TestContext.Current.CancellationToken);
            var script = new FakeToolScript(SdlcAgents.BuildFix, [new FakeToolCall(RepoToolNames.ReadFile, """{"path":"package.json"}""")]);
            using var harness = Harness([script], [null]);
            var session = new RepoToolSession(root.FullName);
            harness.Repo.Open = () => RepoToolContext.ForSession(session);

            await harness.RunReplyJobAsync(FixCall(harness, SdlcAgentStageHarness.Job()), "execution");

            var run = Assert.Single(harness.Store.AgentRuns);
            Assert.Equal(session.ToJson(), run.ToolCalls);
            Assert.Contains(RepoToolNames.ReadFile, run.ToolCalls, StringComparison.Ordinal);
            Assert.Equal(RepoScope.WorkingTree, SdlcRepoToolScope.ScopeOf(SdlcAgents.BuildFix));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FR318_FR319_FR310_build_fix_redelivery_returns_the_stored_reply_without_a_call()
    {
        using var harness = Harness(replies: [Invalid, null]);
        var job = SdlcAgentStageHarness.Job();
        var first = await harness.RunReplyJobAsync(FixCall(harness, job), "execution");

        var again = await harness.RunReplyJobAsync(FixCall(harness, job with { Attempt = 2 }), "execution");

        Assert.Equal(2, harness.Provider.Requests.Count);
        Assert.Equal(2, harness.Store.AgentRuns.Count);
        Assert.Equal((first.RunId, first.Text), (again.RunId, again.Text));
    }
}
