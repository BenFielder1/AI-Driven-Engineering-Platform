using Aide.Api.Agents;
using Aide.Domain.Agents;
using Aide.Domain.Execution;
using Aide.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;
using static Aide.UnitTests.Lifecycle.SdlcPipelineFixture;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-321, FR-314, D-0.7.0-41/-42: <c>sdlc.pipeline.yaml</c> version 2: plan validation as the planning stage's
/// <c>validate:</c> (the <c>sdlc.plan-valid</c> guard) with 0.6.1's one corrective re-run, the <c>agents:</c> bindings of
/// the three stageless agents, the job layer finding them, and the re-pin of runs pinned to version 1.
/// </summary>
public class SdlcDefinitionV2Tests
{
    private const int MaxFiles = 5;

    private const string Invalid = """{"steps":[{"number":1,"title":"A","task":"Do it.","files":[{"path":"a.txt","action":"create"}],"dependencies":[5],"covers":[],"agent":"generalist"}]}""";

    private static readonly string[] RepoTools = [SdlcToolNames.ListFiles, SdlcToolNames.ReadFile, SdlcToolNames.SearchCode];

    [Fact]
    public void FR321_the_planning_stage_validates_its_plan_with_one_corrective_rerun_then_fails()
    {
        var planning = Definition.FindStage("planning")!;

        Assert.Equal([SdlcGuardNames.PlanValid], planning.Validate.Select(v => v.Guard));
        Assert.Equal(new OnInvalidPolicy(RetryWithFeedback: true, MaxAttempts: 2, Then: InvalidOutcome.Fail), planning.OnInvalid);
        Assert.Equal(2, Definition.Version);
        Assert.All(Definition.Stages.Where(s => s.Id != "planning"), s => Assert.Empty(s.Validate));
    }

    [Fact]
    public void FR321_D_0_7_0_42_the_stageless_agents_bind_to_execution_and_refinement_with_0_6_1s_inputs_outputs_and_tools()
    {
        var execution = Definition.FindStage("execution")!.Agents;
        var refinement = Definition.FindStage("refinement")!.Agents;

        Assert.Equal(
            [(SdlcAgents.CodeGenerator, SdlcJobTypes.Execution, (string?)null), (SdlcAgents.BuildFix, SdlcJobTypes.BuildFix, null)],
            execution.Select(a => (a.Key, a.JobType, a.Output)));
        Assert.Equal(
            [SdlcArtifacts.ProjectRequest, SdlcArtifacts.TechnicalSpecification, SdlcArtifacts.ImplementationPlan, SdlcArtifacts.CodebaseSummary],
            execution[0].Inputs);
        Assert.Empty(execution[1].Inputs);
        var analyst = Assert.Single(refinement);
        Assert.Equal((SdlcAgents.RepositoryAnalyst, SdlcJobTypes.RepositoryAnalysis, (string?)SdlcArtifacts.CodebaseSummary), (analyst.Key, analyst.JobType, analyst.Output));
        Assert.All(execution.Concat(refinement), a => Assert.Equal(RepoTools, a.Tools));

        // Bound job types never clash with a primary agent's, and the stages (and so the stepper, FR-308) are 0.6.1's.
        var primaries = Definition.Stages.Select(s => s.Agent?.JobType).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(execution.Concat(refinement), a => primaries.Contains(a.JobType));
        Assert.Equal(["analysis", "refinement", "specification", "planning", "execution", "review", "release"], Definition.Stages.Select(s => s.Id));
    }

    [Theory]
    [InlineData(SdlcAgents.StackAnalyst, "analysis")]
    [InlineData(SdlcAgents.IdeaRefinement, "refinement")]
    [InlineData(SdlcAgents.RepositoryAnalyst, "refinement")]
    [InlineData(SdlcAgents.SpecGenerator, "specification")]
    [InlineData(SdlcAgents.PlanGenerator, "planning")]
    [InlineData(SdlcAgents.CodeGenerator, "execution")]
    [InlineData(SdlcAgents.BuildFix, "execution")]
    [InlineData(SdlcAgents.CodeReview, "review")]
    public void D_0_7_0_42_the_job_layer_finds_each_agents_stage_primary_or_bound(string agent, string stage) =>
        Assert.Equal(stage, SdlcStageLocator.StageOf(Definition, agent));

    [Fact]
    public void D_0_7_0_42_an_SDLC_call_names_its_agent_so_a_corrective_attempt_runs_the_same_agent()
    {
        var job = SdlcAgentStageHarness.Job();
        var call = SdlcAgentStageHarness.SpecCall(job, Guid.CreateVersion7()) with
        {
            Request = SdlcAgentStageHarness.SpecCall(job, Guid.CreateVersion7()).Request with { Agent = SdlcAgents.BuildFix },
        };

        Assert.Equal(SdlcAgents.BuildFix, call.ToStageRequest(Guid.CreateVersion7(), "execution", "stage_entered").AgentKey);
    }

    [Fact]
    public async Task FR321_FR314_the_plan_valid_guard_returns_the_plan_rules_message_as_its_one_reason()
    {
        var services = new ServiceCollection().AddScoped<SdlcPlanChecks>().BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var projectId = Guid.CreateVersion7();
        scope.ServiceProvider.GetRequiredService<SdlcPlanChecks>().Set(projectId, new SdlcPlanCheck(MaxFiles, null, null));
        var guard = new SdlcPlanValidGuard(scope.ServiceProvider);
        GuardContext Context(FactBag facts) => new(Guid.CreateVersion7(), projectId, "planning", ApprovalMode.Advisory, facts);
        FactBag Output(string content) => FactBag.Empty.With(new StageOutputFact(SdlcArtifacts.ImplementationPlan, ArtifactFormat.Markdown, content));
        var valid = new Aide.Infrastructure.Agents.FakePlanGeneratorResponder().Respond(new Tevrin.Engine.Runtime.Llm.LlmRequest
        {
            Model = "fake-1",
            System = string.Empty,
            Messages = [new Tevrin.Engine.Runtime.Llm.LlmMessage(Tevrin.Engine.Runtime.Llm.LlmRole.User, $"<project_request>{SdlcAgentStageHarness.ProjectRequest}</project_request>")],
        });

        var ct = TestContext.Current.CancellationToken;
        Assert.True((await guard.EvaluateAsync(Context(FactBag.Empty), ct)).Ok); // a lifecycle evaluation: never an entry rule
        Assert.True((await guard.EvaluateAsync(Context(Output(valid)), ct)).Ok);
        var rejected = await guard.EvaluateAsync(Context(Output(Invalid)), ct);
        Assert.Equal([PlanDocument.ParseAgentOutput(Invalid, MaxFiles).Error!.Message], rejected.Reasons);
    }

    [Fact]
    public async Task FR321_FR314_the_definitions_validation_gives_each_reason_once_and_0_6_1s_retry()
    {
        using var harness = PlanHarness([Invalid, null]);
        var job = SdlcAgentStageHarness.Job();

        var result = await harness.RunCallAsync(PlanCall(harness, job), "planning", Checks(harness));

        Assert.Equal(PlanDocument.ParseAgentOutput(Invalid, MaxFiles).Error!.Message, result.Attempts[0].Findings);
        Assert.Equal([(job.Id, (string?)null), (AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey), null)], harness.Store.AgentRuns.Select(r => (r.Id, r.Error)));
        Assert.Single(harness.Store.Artifacts);
    }

    [Fact]
    public async Task FR321_a_run_pinned_to_version_1_keeps_the_host_plan_check_and_its_INVALID_PLAN()
    {
        using var harness = PlanHarness([Invalid]);
        Assert.True(harness.Services.GetRequiredService<PipelineRegistry>().TryGet(SdlcPack.PipelineId, out var loaded));
        var version1 = loaded.Definition! with
        {
            Version = 1,
            Stages = [.. loaded.Definition!.Stages.Select(s => s.Id == "planning" ? s with { Validate = [], OnInvalid = null } : s with { Agents = [] })],
        };
        harness.Services.GetRequiredService<PipelineDefinitionCache>().Add(SdlcRunRepin.Version1Hash, new StoredPipelineDefinition(loaded.PackId, version1));
        var run = harness.Run;
        harness.Store.Runs.Remove(run);
        harness.Store.Runs.Add(new PipelineRun
        {
            Id = run.Id,
            ProjectId = run.ProjectId,
            PipelineId = run.PipelineId,
            DefinitionHash = SdlcRunRepin.Version1Hash,
            SubjectId = run.SubjectId,
            CurrentStage = run.CurrentStage,
            PackStatus = run.PackStatus,
        });
        var job = SdlcAgentStageHarness.Job();

        // No plan check is set: version 1's stage has no guard, so the host check (the job's own spec) decides.
        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunCallAsync(PlanCall(harness, job), "planning"));

        Assert.Equal("INVALID_PLAN", ex.ErrorCode);
        Assert.Equal([AgentRunStatus.Succeeded, AgentRunStatus.Failed], harness.Store.AgentRuns.Select(r => r.Status));
    }

    [Theory]
    [InlineData("analysis")]
    [InlineData("refinement")]
    [InlineData("specification")]
    [InlineData("planning")]
    [InlineData("review")]
    public void FR321_FR317_the_primary_agents_0_6_1_gave_repo_tools_list_them(string stage) =>
        Assert.Equal(RepoTools, Definition.FindStage(stage)!.Agent!.Tools);

    [Fact]
    public async Task FR321_FR317_a_primary_without_a_snapshot_makes_0_6_1s_plain_call_despite_its_stage_tools()
    {
        using var harness = new SdlcAgentStageHarness([null]);

        await harness.RunAsync(SdlcAgentStageHarness.Job());

        // 0.6.1: no snapshot, no tools: one plain completion, its tokens as the provider counted them, no tool calls.
        var run = Assert.Single(harness.Store.AgentRuns);
        var request = Assert.Single(harness.Provider.Requests);
        var plain = await new Tevrin.Engine.Runtime.Llm.FakeLlmProvider([new AgentReplies(SdlcAgents.SpecGenerator, [null])]).CompleteAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal((0, plain.InputTokens, plain.OutputTokens, "[]"), (harness.Provider.Turns, run.InputTokens, run.OutputTokens, run.ToolCalls));
        Assert.Null(AgentRunner.ReadOutput(run.Output)!.Warnings);
    }

    [Fact]
    public async Task FR321_FR317_a_primary_with_a_snapshot_runs_its_tool_loop_over_the_snapshot()
    {
        var root = Directory.CreateTempSubdirectory("tevrin-v2-");
        try
        {
            using var harness = new SdlcAgentStageHarness([null]);
            var session = new Aide.Infrastructure.RepoTools.RepoToolSession(root.FullName);
            harness.Repo.Open = () => RepoToolContext.ForSession(session);

            await harness.RunAsync(SdlcAgentStageHarness.Job());

            Assert.Equal(1, harness.Provider.Turns);
            Assert.Equal(session.ToJson(), Assert.Single(harness.Store.AgentRuns).ToolCalls);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void FR321_only_the_version_1_hash_is_re_pinned_and_never_onto_itself()
    {
        Assert.True(Registry.TryGet(SdlcPack.PipelineId, out var loaded));

        Assert.Equal("90f1f065397fe245955c62ef5057122a6fca2ae373941a90dc37b431ecf0c9e7", SdlcRunRepin.Version1Hash);
        Assert.NotEqual(SdlcRunRepin.Version1Hash, loaded.Hash);
        Assert.Equal([SdlcRunRepin.Version1Hash], SdlcRunRepin.From(loaded.Hash!));
        Assert.Empty(SdlcRunRepin.From(SdlcRunRepin.Version1Hash));
    }

    private static SdlcAgentStageHarness PlanHarness(params string?[] replies)
    {
        var harness = new SdlcAgentStageHarness(SdlcAgents.PlanGenerator, [], replies);
        harness.Data.Project.CurrentPhase = Domain.Lifecycle.Phase.Planning;
        return harness;
    }

    private static Action<IServiceProvider> Checks(SdlcAgentStageHarness harness) =>
        services => services.GetRequiredService<SdlcPlanChecks>().Set(harness.Data.Project.Id, new SdlcPlanCheck(MaxFiles, null, null));

    /// <summary>The plan job's request and spec, as <see cref="PlanGenerationJobHandler"/> builds them (no stack).</summary>
    private static SdlcAgentStageCall PlanCall(SdlcAgentStageHarness harness, JobExecution job)
    {
        var check = new SdlcPlanCheck(MaxFiles, null, null);
        AgentRunRequest Request(string? previous, string? feedback) => new()
        {
            ProjectId = harness.Data.Project.Id,
            Agent = SdlcAgents.PlanGenerator,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_REQUEST"] = SdlcAgentStageHarness.ProjectRequest,
                ["TECHNICAL_SPECIFICATION"] = "## 1. Overview\nA todo API.",
                ["PREVIOUS_PLAN"] = previous,
                ["MAX_FILES_PER_STEP"] = "5",
            },
            UserFeedback = feedback,
            Repo = RepoScope.BaseCommit,
        };

        return new SdlcAgentStageCall(
            Request(null, null),
            new ArtifactAgentSpec(
                SdlcArtifacts.ImplementationPlan,
                check.ToContent,
                (rejected, error) => Request(AgentOutput.ExtractJsonObject(rejected) ?? rejected, PlanDocument.CorrectiveFeedback(error))));
    }
}
