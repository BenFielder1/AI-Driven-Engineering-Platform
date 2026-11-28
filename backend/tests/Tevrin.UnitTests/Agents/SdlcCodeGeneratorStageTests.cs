using System.Text.Json;
using Tevrin.Api.Agents;
using Tevrin.Domain.Execution;
using Tevrin.Domain.RepoTools;
using Tevrin.Domain.Stack;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Infrastructure.Agents;
using Tevrin.Infrastructure.Persistence.Conversion;
using Tevrin.Infrastructure.RepoTools;
using Tevrin.Pack.Sdlc;
using Tevrin.UnitTests.Stack;

namespace Tevrin.UnitTests.Agents;

/// <summary>
/// FR-318, FR-319 (FR-31–FR-35, FR-121), D-0.7.0-42: <c>code_generator</c>, bound on <c>execution</c>, on the engine runner
/// records what 0.6.1's <c>ExecutionJobHandler</c> got from <c>AgentRunner</c>: the step's prompt values, the run
/// keyed by the step attempt (<c>step-&lt;id&gt;-attempt-&lt;n&gt;</c>) with the step id, the specialist prompt
/// (<see cref="StackPrompt.Compose"/>) and stack stamp, the reply accepted as sent (the Guardian judges it; no corrective
/// re-run), the repo tools on the working tree with the <c>TOOL_LIMIT</c> forced answer, failures, and redelivery.
/// </summary>
public class SdlcCodeGeneratorStageTests
{
    private const string Unreadable = "Not JSON at all.";

    private static readonly Guid StepId = Guid.CreateVersion7();

    /// <summary>0.6.1's <c>packs</c> JSON options (<c>SdlcAgentRuns.StampStack</c>).</summary>
    private static readonly JsonSerializerOptions PacksJson = new(JsonSerializerDefaults.Web);

    /// <summary>0.6.1's literal run key of the step's first attempt.</summary>
    private static readonly string RunKey = $"step-{StepId:N}-attempt-1";

    private static SdlcAgentStageHarness Harness(IEnumerable<IFakeToolScript>? scripts = null, params string?[] replies)
    {
        var harness = new SdlcAgentStageHarness(SdlcAgents.CodeGenerator, scripts ?? [], replies);
        harness.Data.Project.CurrentPhase = Domain.Lifecycle.Phase.Execution;
        return harness;
    }

    /// <summary>The step's request, as <c>ExecutionJobHandler.RequestAsync</c> builds it (then the stack fields it adds).</summary>
    private static SdlcAgentStageCall StepCall(
        SdlcAgentStageHarness harness, JobExecution job, StackResolution? stack = null, string? specialist = null, string? legacy = null, IReadOnlyList<GuardianViolation>? violations = null)
    {
        IReadOnlyList<DeclaredFile> declared = [new("src/app.ts", FileAction.Create), new("README.md", FileAction.Modify)];
        var request = new AgentRunRequest
        {
            ProjectId = harness.Data.Project.Id,
            Agent = SdlcAgents.CodeGenerator,
            Job = job,
            StepId = StepId,
            RunKey = Api.Features.Execution.StepAttemptRecorder.RunKey(StepId, 1),
            SpecialistAgentKey = specialist,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_REQUEST"] = SdlcAgentStageHarness.ProjectRequest,
                ["TECHNICAL_SPECIFICATION"] = "# Spec\n\n## API\nGET /api/todos",
                ["IMPLEMENTATION_PLAN"] = CodeGeneration.PlanWithStatuses([new PlanStepView(1, "App", "Build the app.", StepStatus.InProgress, declared, [])]),
                ["CODEBASE_SUMMARY"] = null,
                ["PROJECT_RULES"] = CodeGeneration.ProjectRules(5, null),
                ["MAX_FILES_PER_STEP"] = "5",
                ["STEP_NUMBER"] = "1",
                ["STEP_TITLE"] = "App",
                ["STEP_TASK"] = "Build the app.",
                ["DECLARED_FILES"] = CodeGeneration.DeclaredFiles(declared),
                ["STEP_USER_INSTRUCTIONS"] = null,
                ["EXISTING_CODE"] = CodeGeneration.ExistingCode([new WorkingFileContent("src/app.ts", WorkingFileState.Missing), new WorkingFileContent("README.md", WorkingFileState.Present, "# Demo\n", 7)]),
            },
            UserFeedback = CodeGeneration.RetryFeedback(violations),
            Repo = RepoScope.WorkingTree,
        };
        return new SdlcAgentStageCall(
            request with
            {
                Stack = stack,
                LegacyAgentKey = legacy,
                Warnings = legacy is null ? request.Warnings : [.. request.Warnings, AgentRunWarnings.LegacyAgentMapped],
            },
            ExecutionJobHandler.ReplySpec);
    }

    private static object RunStarted(AgentRun run, JobExecution job, string? stack = null, string? agentKey = null, string? legacy = null) =>
        new { runId = run.Id, jobId = job.Id, agent = SdlcAgents.CodeGenerator, provider = "fake", model = "fake-1", promptVersion = run.PromptVersion, stepId = (Guid?)StepId, stack, agentKey, legacyAgentKey = legacy };

    [Fact]
    public async Task FR318_FR319_code_generator_step_succeeds_under_the_step_attempts_run_key_with_0_6_1s_prompt_and_events()
    {
        using var harness = Harness(replies: [null]);
        var job = SdlcAgentStageHarness.Job();
        var call = StepCall(harness, job);

        var outcome = await harness.RunReplyJobAsync(call, "execution");

        var run = Assert.Single(harness.Store.AgentRuns);
        var expectedId = AgentRunRules.RunId(job.Id, RunKey);
        var template = new EmbeddedPromptLibrary().Template(SdlcAgents.CodeGenerator);
        var rendered = template.Render(call.PromptValues()).Value;
        var request = Assert.Single(harness.Provider.Requests);
        Assert.Equal((rendered.System, rendered.User), (request.System, request.Messages[^1].Content));
        Assert.Equal(
            (expectedId, SdlcAgents.CodeGenerator, "execution", AgentRunStatus.Succeeded, (string?)null, (Guid?)StepId, (string?)null, (string?)null),
            (run.Id, run.Agent, run.Stage, run.Status, run.Error, run.StepId, run.AgentKey, run.CatalogVersion));
        Assert.Equal((expectedId, run.InputTokens, run.OutputTokens), (outcome.RunId, outcome.InputTokens, outcome.OutputTokens));
        var reply = CodeGeneration.Parse(outcome.Text).Value;
        Assert.Equal(1, reply.StepNumber);
        Assert.Equal(["src/app.ts", "README.md"], reply.Files.Select(f => f.Path));
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal(TevrinJson.Serialize(RunStarted(run, job)), harness.Data.Events[0].Payload);
        Assert.Empty(harness.Store.Artifacts);
    }

    [Fact]
    public async Task FR318_FR319_code_generator_retry_feedback_is_the_runs_user_feedback_in_the_prompt()
    {
        using var harness = Harness(replies: [null]);
        var job = SdlcAgentStageHarness.Job();
        GuardianViolation[] violations = [new(GuardianCheck.PlanAlignment, "UNDECLARED_FILE", ViolationSeverity.Error, "src/x.ts is not declared.", "src/x.ts")];
        var call = StepCall(harness, job, violations: violations);

        await harness.RunReplyJobAsync(call, "execution");

        var run = Assert.Single(harness.Store.AgentRuns);
        var expected = CodeGeneration.RetryFeedback(violations);
        Assert.Equal(expected, run.UserFeedback);
        Assert.Contains(expected!, harness.Provider.Requests.Single().Messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR318_FR319_FR121_code_generator_specialist_step_sends_the_composed_prompt_and_records_the_stack()
    {
        using var harness = Harness(replies: [null]);
        var job = SdlcAgentStageHarness.Job();
        var stack = StackResolverTests.Resolve(StackResolverTests.AspnetSpa());
        var specialist = stack.Specialists.First(s => s.Skills.Count > 0);
        var call = StepCall(harness, job, stack, specialist.Key, legacy: "backend");

        await harness.RunReplyJobAsync(call, "execution");

        var template = new EmbeddedPromptLibrary().Template(SdlcAgents.CodeGenerator);
        var rendered = template.Render(call.PromptValues()).Value;
        var composed = StackPrompt.Compose(rendered, stack, specialist.Key).Value;
        var request = Assert.Single(harness.Provider.Requests);
        Assert.NotEqual(rendered.System, composed.System);
        Assert.Equal((composed.System, composed.User), (request.System, request.Messages[^1].Content));

        var run = Assert.Single(harness.Store.AgentRuns);
        var packs = JsonSerializer.Serialize(
            specialist.Packs.Select(p => new { id = p.Id, version = p.Version, sections = p.Sections }),
            PacksJson);
        Assert.Equal((specialist.Key, stack.CatalogVersion, packs), (run.AgentKey, run.CatalogVersion, run.Packs));
        Assert.Equal(TevrinJson.Serialize(RunStarted(run, job, stack.CatalogLabel, specialist.Key, "backend")), harness.Data.Events[0].Payload);
        Assert.Equal([AgentRunWarnings.LegacyAgentMapped], SdlcAgentRuns.ReadOutput(run.Output)!.Warnings);
        Assert.Contains("\"warnings\":[\"legacy_agent_mapped\"]", harness.Data.Events.Single(e => e.Type == "RUN_COMPLETED").Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR318_FR319_FR121_code_generator_generalist_step_sends_the_base_prompt_and_records_generalist()
    {
        using var harness = Harness(replies: [null]);
        var job = SdlcAgentStageHarness.Job();
        var stack = StackResolverTests.Resolve(StackResolverTests.AspnetSpa());
        var call = StepCall(harness, job, stack, PlanAgents.Generalist);

        await harness.RunReplyJobAsync(call, "execution");

        var rendered = new EmbeddedPromptLibrary().Template(SdlcAgents.CodeGenerator).Render(call.PromptValues()).Value;
        Assert.Equal(rendered.System, harness.Provider.Requests.Single().System);
        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal((PlanAgents.Generalist, stack.CatalogVersion, "[]"), (run.AgentKey, run.CatalogVersion, run.Packs));
    }

    [Fact]
    public async Task FR318_FR319_code_generator_unreadable_reply_is_accepted_as_sent_with_no_corrective_rerun()
    {
        using var harness = Harness(replies: [Unreadable]);
        var job = SdlcAgentStageHarness.Job();

        var outcome = await harness.RunReplyJobAsync(StepCall(harness, job), "execution");

        // 0.6.1: the runner had no check; the Guardian rejects the step from the unreadable reply, the run succeeded.
        Assert.Equal((AgentRunRules.RunId(job.Id, RunKey), Unreadable), (outcome.RunId, outcome.Text));
        Assert.Single(harness.Provider.Requests);
        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal((AgentRunStatus.Succeeded, (string?)null), (run.Status, run.Error));
        Assert.True(CodeGeneration.Parse(outcome.Text).IsFailure);
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED"], harness.Data.Events.Select(e => e.Type));
    }

    [Fact]
    public async Task FR318_FR319_FR71_code_generator_tool_calls_on_the_working_tree_are_the_runs()
    {
        var root = Directory.CreateTempSubdirectory("tevrin-codegen-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "README.md"), "# Demo\n", TestContext.Current.CancellationToken);
            var script = new FakeToolScript(SdlcAgents.CodeGenerator, [new FakeToolCall(RepoToolNames.ReadFile, """{"path":"README.md"}""")]);
            using var harness = Harness([script], [null]);
            var session = new RepoToolSession(root.FullName);
            harness.Repo.Open = () => RepoToolContext.ForSession(session);

            await harness.RunReplyJobAsync(StepCall(harness, SdlcAgentStageHarness.Job()), "execution");

            var run = Assert.Single(harness.Store.AgentRuns);
            Assert.Equal(session.ToJson(), run.ToolCalls);
            Assert.Contains(RepoToolNames.ReadFile, run.ToolCalls, StringComparison.Ordinal);
            Assert.Equal((1, RepoScope.WorkingTree), (harness.Repo.Opened, SdlcRepoToolScope.ScopeOf(SdlcAgents.CodeGenerator)));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FR318_FR319_FR71_code_generator_at_TOOL_LIMIT_answers_with_what_it_has()
    {
        var root = Directory.CreateTempSubdirectory("tevrin-codegen-");
        try
        {
            var script = new FakeToolScript(SdlcAgents.CodeGenerator, FakeToolScripts.Many(30));
            using var harness = Harness([script], [null]);
            var session = new RepoToolSession(root.FullName);
            harness.Repo.Open = () => RepoToolContext.ForSession(session);

            var outcome = await harness.RunReplyJobAsync(StepCall(harness, SdlcAgentStageHarness.Job()), "execution");

            Assert.Equal(RepoToolLimits.MaxCallsPerRun + 1, session.Calls.Count);
            Assert.Equal(RepoToolErrors.ToolLimit, session.Calls[^1].Error);
            Assert.Equal(RepoToolLimits.MaxCallsPerRun + 2, harness.Provider.Turns);
            var run = Assert.Single(harness.Store.AgentRuns);
            Assert.Equal((AgentRunStatus.Succeeded, session.ToJson()), (run.Status, run.ToolCalls));
            Assert.True(CodeGeneration.Parse(outcome.Text).IsSuccess);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FR318_FR319_FR71_code_generator_without_a_snapshot_is_a_plain_call_with_the_warning()
    {
        using var harness = Harness(replies: [null]);
        harness.Repo.Open = () => new RepoToolContext(null, AgentRunWarnings.SnapshotUnavailable);

        await harness.RunReplyJobAsync(StepCall(harness, SdlcAgentStageHarness.Job()), "execution");

        Assert.Equal(0, harness.Provider.Turns);
        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal([AgentRunWarnings.SnapshotUnavailable], SdlcAgentRuns.ReadOutput(run.Output)!.Warnings);
    }

    [Fact]
    public async Task FR318_FR319_FR26_code_generator_budget_used_up_between_tool_turns_fails_with_BUDGET_EXCEEDED()
    {
        var root = Directory.CreateTempSubdirectory("tevrin-codegen-");
        try
        {
            var script = new FakeToolScript(SdlcAgents.CodeGenerator, [new FakeToolCall(RepoToolNames.ListFiles, "{}")]);
            using var harness = Harness([script], [null]);
            harness.Repo.Open = () => RepoToolContext.ForSession(new RepoToolSession(root.FullName));
            harness.Data.Project.TokenBudget = 1;
            var job = SdlcAgentStageHarness.Job();

            var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunReplyJobAsync(StepCall(harness, job), "execution"));

            Assert.Equal(("BUDGET_EXCEEDED", false), (ex.ErrorCode, ex.Transient));
            var run = Assert.Single(harness.Store.AgentRuns);
            var used = (long)run.InputTokens + run.OutputTokens;
            Assert.Equal((AgentRunStatus.Failed, $"BUDGET_EXCEEDED: {ex.Message}"), (run.Status, run.Error));
            Assert.Equal(["RUN_STARTED", "BUDGET_EXCEEDED", "RUN_FAILED"], harness.Data.Events.Select(e => e.Type));
            Assert.Equal(
                TevrinJson.Serialize(new { agent = SdlcAgents.CodeGenerator, tokenBudget = (long?)1, tokensUsed = used, jobId = job.Id, runId = run.Id }),
                harness.Data.Events[1].Payload);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FR318_FR319_code_generator_provider_failure_on_the_last_attempt_fails_the_run_dead_lettered()
    {
        using var harness = Harness(replies: [null]);
        harness.Provider.Failures.Enqueue(new LlmProviderException("overloaded", transient: true, statusCode: 529));
        var job = SdlcAgentStageHarness.Job(attempt: RetryPolicy.Default.MaxAttempts);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunReplyJobAsync(StepCall(harness, job), "execution"));

        Assert.Equal((true, "PROVIDER_ERROR"), (ex.Transient, ex.ErrorCode));
        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal((AgentRunStatus.Failed, "PROVIDER_ERROR: overloaded", (Guid?)StepId), (run.Status, run.Error, run.StepId));
        Assert.Equal(["RUN_STARTED", "RUN_FAILED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal(
            TevrinJson.Serialize(new { runId = run.Id, agent = SdlcAgents.CodeGenerator, errorCode = "PROVIDER_ERROR", error = run.Error, attempts = 1, deadLettered = true }),
            harness.Data.Events[1].Payload);
    }

    [Fact]
    public async Task FR318_FR319_FR310_code_generator_redelivery_returns_the_stored_reply_without_a_call()
    {
        using var harness = Harness(replies: [null]);
        var job = SdlcAgentStageHarness.Job();
        var first = await harness.RunReplyJobAsync(StepCall(harness, job), "execution");

        var again = await harness.RunReplyJobAsync(StepCall(harness, job with { Attempt = 2 }), "execution");

        Assert.Single(harness.Provider.Requests);
        Assert.Single(harness.Store.AgentRuns);
        Assert.Equal((first.RunId, first.Text), (again.RunId, again.Text));
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED"], harness.Data.Events.Select(e => e.Type));
    }
}
