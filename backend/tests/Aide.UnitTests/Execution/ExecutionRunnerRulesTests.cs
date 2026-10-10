using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Agents;

namespace Aide.UnitTests.Execution;

/// <summary>FR-26, FR-31, FR-33, FR-34, FR-35, FR-37: the execution runner's rules.</summary>
public class ExecutionRunnerRulesTests
{
    private static readonly Guid ProjectId = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    private static StepState S(int number, StepStatus status, params int[] deps) => new(number, status, deps);

    private static ExecutionFacts Facts(Phase phase = Phase.Execution, long? budget = null, long used = 0, params StepState[] steps) =>
        new(phase, true, steps.Length > 0 ? steps : [S(1, StepStatus.Pending)], budget, used);

    // ---------- FR-31: what is runnable ----------

    [Fact]
    public void FR31_Steps_1_to_3_completed_and_step_4_depending_on_2_runs_next()
    {
        StepState[] steps = [S(1, StepStatus.Completed), S(2, StepStatus.Completed, 1), S(3, StepStatus.Completed, 2), S(4, StepStatus.Pending, 2), S(5, StepStatus.Pending, 4)];

        Assert.Equal(4, StepRules.NextStep(steps));
        Assert.True(ExecutionRules.HasWork(steps));
    }

    [Fact]
    public void FR31_A_step_in_progress_is_work_to_finish_and_a_blocked_step_stops_execution()
    {
        Assert.True(ExecutionRules.HasWork([S(1, StepStatus.InProgress), S(2, StepStatus.Pending, 1)]));
        Assert.False(ExecutionRules.HasWork([S(1, StepStatus.Blocked), S(2, StepStatus.Pending)]));
        Assert.False(ExecutionRules.HasWork([S(1, StepStatus.Completed)]));
    }

    [Theory]
    [InlineData(new[] { StepStatus.Completed, StepStatus.Completed }, false, false, ExecutionRunStatus.Completed)]
    [InlineData(new[] { StepStatus.Completed, StepStatus.Blocked }, false, true, ExecutionRunStatus.Blocked)]
    [InlineData(new[] { StepStatus.InProgress, StepStatus.Pending }, true, true, ExecutionRunStatus.Paused)]
    [InlineData(new[] { StepStatus.InProgress, StepStatus.Pending }, false, true, ExecutionRunStatus.Running)]
    [InlineData(new[] { StepStatus.Pending }, false, false, ExecutionRunStatus.Idle)]
    [InlineData(new StepStatus[0], false, false, ExecutionRunStatus.Idle)]
    public void FR37_Run_status_follows_steps_pause_flag_and_job(StepStatus[] steps, bool paused, bool jobActive, ExecutionRunStatus expected) =>
        Assert.Equal(expected, ExecutionRules.Status(steps, paused, jobActive));

    // ---------- FR-31 / FR-26: start ----------

    [Fact]
    public void FR31_Start_is_allowed_in_execution_with_a_runnable_step() =>
        Assert.True(ExecutionRules.CheckStart(Facts()).IsSuccess);

    [Fact]
    public void FR26_Budget_100k_with_100k_used_refuses_start_with_BUDGET_EXCEEDED() =>
        Assert.Equal(ErrorCodes.BudgetExceeded, ExecutionRules.CheckStart(Facts(budget: 100_000, used: 100_000)).Error!.Code);

    [Theory]
    [InlineData(Phase.Planning)]
    [InlineData(Phase.Review)]
    public void FR31_Start_outside_execution_is_PHASE_PRECONDITION_FAILED(Phase phase) =>
        Assert.Equal(ErrorCodes.PhasePreconditionFailed, ExecutionRules.CheckStart(Facts(phase)).Error!.Code);

    [Fact]
    public void FR31_Start_without_plan_with_a_blocked_step_or_with_everything_done_is_PHASE_PRECONDITION_FAILED()
    {
        Assert.Equal(ErrorCodes.PhasePreconditionFailed, ExecutionRules.CheckStart(new ExecutionFacts(Phase.Execution, false, [], null, 0)).Error!.Code);
        Assert.Equal(ErrorCodes.PhasePreconditionFailed, ExecutionRules.CheckStart(Facts(steps: [S(1, StepStatus.Blocked), S(2, StepStatus.Pending)])).Error!.Code);
        Assert.Equal(ErrorCodes.PhasePreconditionFailed, ExecutionRules.CheckStart(Facts(steps: [S(1, StepStatus.Completed)])).Error!.Code);
    }

    // ---------- FR-37: pause and resume ----------

    [Fact]
    public void FR37_Pause_and_resume_need_the_execution_phase()
    {
        Assert.True(ExecutionRules.CheckPause(Phase.Execution).IsSuccess);
        Assert.Equal(ErrorCodes.PhasePreconditionFailed, ExecutionRules.CheckPause(Phase.Planning).Error!.Code);
        Assert.Equal(ErrorCodes.PhasePreconditionFailed, ExecutionRules.CheckResume(Facts(Phase.Review)).Error!.Code);
    }

    [Fact]
    public void FR37_Resume_checks_the_budget_only_when_work_remains()
    {
        Assert.Equal(ErrorCodes.BudgetExceeded, ExecutionRules.CheckResume(Facts(budget: 10, used: 10)).Error!.Code);
        Assert.True(ExecutionRules.CheckResume(Facts(budget: 10, used: 10, steps: [S(1, StepStatus.Completed)])).IsSuccess);
    }

    [Fact]
    public void FR37_Paused_execution_does_not_continue_to_the_next_step()
    {
        StepState[] steps = [S(5, StepStatus.Completed), S(6, StepStatus.Pending, 5)];

        Assert.False(ExecutionRules.ShouldContinue(steps, paused: true));
        Assert.True(ExecutionRules.ShouldContinue(steps, paused: false));
    }

    // ---------- FR-33: attempts ----------

    [Fact]
    public void FR33_Each_start_of_a_pending_or_rejected_step_is_a_new_attempt_and_a_continued_one_is_not()
    {
        Assert.Equal(new StepAttemptStart(1, true), StepRules.BeginAttempt(StepStatus.Pending, 0).Value);
        Assert.Equal(new StepAttemptStart(3, true), StepRules.BeginAttempt(StepStatus.Rejected, 2).Value);
        Assert.Equal(new StepAttemptStart(2, false), StepRules.BeginAttempt(StepStatus.InProgress, 2).Value);
        Assert.True(StepRules.BeginAttempt(StepStatus.Completed, 1).IsFailure);
    }

    [Fact]
    public void FR33_Escalation_continue_puts_only_a_blocked_step_back_to_pending()
    {
        Assert.Equal(new StepStatusChange(StepStatus.Pending, true), StepRules.Unblock(StepStatus.Blocked));
        Assert.Equal(new StepStatusChange(StepStatus.Completed, false), StepRules.Unblock(StepStatus.Completed));
    }

    [Fact]
    public void FR33_Retry_feedback_lists_every_error_and_warning_of_the_previous_attempt()
    {
        GuardianViolation[] violations =
        [
            new(GuardianCheck.PlanAlignment, ErrorCodes.Guardian.PlanAlignment, ViolationSeverity.Error, "'src/x.js' is not declared for this step.", "src/x.js"),
            new(GuardianCheck.SecretScan, ErrorCodes.Guardian.SecretDetected, ViolationSeverity.Error, "Possible aws_access_key_id in 'a.js' at line 1.", "a.js"),
            new(GuardianCheck.PlanAlignment, ErrorCodes.Guardian.PlanAlignment, ViolationSeverity.Warning, "'b.js' is declared for this step but wasn't produced.", "b.js"),
        ];

        var feedback = CodeGeneration.RetryFeedback(violations)!;

        Assert.Contains("[PLAN_ALIGNMENT] 'src/x.js' is not declared", feedback, StringComparison.Ordinal);
        Assert.Contains("[SECRET_DETECTED]", feedback, StringComparison.Ordinal);
        Assert.Contains("Warnings (not blocking):", feedback, StringComparison.Ordinal);
        Assert.Null(CodeGeneration.RetryFeedback([violations[2]]));
        Assert.Null(CodeGeneration.RetryFeedback(null));
    }

    // ---------- FR-31: the reply and the inputs ----------

    [Fact]
    public void FR31_A_reply_is_read_from_its_JSON_object_even_inside_a_fence()
    {
        var reply = CodeGeneration.Parse("```json\n{\"stepNumber\": 2, \"files\": [{\"path\": \"src/a.js\", \"content\": \"x\"}], \"notes\": \"done\"}\n```");

        Assert.True(reply.IsSuccess);
        Assert.Equal(2, reply.Value.StepNumber);
        Assert.Equal([new ProducedFile("src/a.js", "x")], reply.Value.Files);
        Assert.Equal("done", reply.Value.Notes);
    }

    [Theory]
    [InlineData("no json here")]
    [InlineData("{\"files\": []}")]
    [InlineData("{\"stepNumber\": 1, \"files\": [{\"path\": \"a\"}]}")]
    [InlineData("{\"stepNumber\": 1, \"files\": {}}")]
    public void FR32_An_unreadable_reply_is_INVALID_OUTPUT_and_becomes_a_rejecting_violation(string text)
    {
        var reply = CodeGeneration.Parse(text);

        Assert.Equal(ErrorCodes.InvalidOutput, reply.Error!.Code);
        var violation = CodeGeneration.UnreadableViolation(reply.Error);
        Assert.Equal((GuardianCheck.SingleStep, ErrorCodes.InvalidOutput, ViolationSeverity.Error), (violation.Check, violation.Code, violation.Severity));
    }

    [Fact]
    public void FR31_The_plan_input_shows_every_step_with_its_status()
    {
        var text = CodeGeneration.PlanWithStatuses(
        [
            new PlanStepView(2, "API", "Build the API.", StepStatus.Pending, [new DeclaredFile("src/api.js", FileAction.Create)], [1]),
            new PlanStepView(1, "Model", "Build the model.", StepStatus.Completed, [new DeclaredFile("src/model.js", FileAction.Create)], []),
        ]);

        Assert.StartsWith("## Step 1 [completed]: Model", text, StringComparison.Ordinal);
        Assert.Contains("## Step 2 [pending]: API", text, StringComparison.Ordinal);
        Assert.Contains("Files: src/api.js (create)", text, StringComparison.Ordinal);
        Assert.Contains("Depends on: step 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FR31_The_existing_code_input_holds_each_declared_files_current_content_or_why_not()
    {
        var text = CodeGeneration.ExistingCode(
        [
            new WorkingFileContent("src/a.js", WorkingFileState.Present, "line 1\r\nline 2\n"),
            new WorkingFileContent("src/new.js", WorkingFileState.Missing),
            new WorkingFileContent("big.bin", WorkingFileState.TooLarge, Length: 200_000),
        ])!;

        Assert.Contains("----- BEGIN src/a.js -----\nline 1\nline 2\n----- END src/a.js -----", text, StringComparison.Ordinal);
        Assert.Contains("src/new.js: does not exist yet", text, StringComparison.Ordinal);
        Assert.Contains("big.bin: exists, but is too large to include (200000 bytes)", text, StringComparison.Ordinal);
        Assert.Null(CodeGeneration.ExistingCode([]));
    }

    [Fact]
    public void FR31_Project_rules_carry_the_limits_and_the_repository_rules_file()
    {
        var rules = CodeGeneration.ProjectRules(7, "Use tabs.");

        Assert.Contains("at most 7 files", rules, StringComparison.Ordinal);
        Assert.EndsWith("Repository rules (.aide/rules.md):\nUse tabs.", rules, StringComparison.Ordinal);
    }

    // ---------- FR-34 / FR-35: branch, commit, manifest ----------

    [Fact]
    public void FR34_The_project_branch_is_aide_slug_short_id()
    {
        Assert.Equal("aide/todo-api-4a5b0c-0199a1b2", ExecutionRules.BranchName("todo-api-4a5b0c", ProjectId));
        Assert.Equal("aide/project-0199a1b2", ExecutionRules.BranchName("bad..slug", ProjectId));
        Assert.Equal("aide/project-0199a1b2", ExecutionRules.BranchName("  ", ProjectId));
    }

    [Fact]
    public void FR34_A_step_commit_has_the_AIDE_subject_and_a_body_listing_the_files_with_the_step_trailer()
    {
        var stepId = Guid.Parse("0199a1b2-0000-7000-8000-000000000004");

        var message = CodeGeneration.CommitMessage(4, "Add the\nREST API", ["./src/routes.js", "src\\server.js"], stepId);

        Assert.Equal("[AIDE][Phase:execution][Step:4] Add the REST API", message.Subject);
        Assert.Equal($"Files:\n- src/routes.js\n- src/server.js\n\nAIDE-Step-Id: {stepId:D}", message.Body);
    }

    [Fact]
    public void FR34_The_manifest_records_path_declared_action_and_content_hash()
    {
        var manifest = CodeGeneration.Manifest(
            [new ProducedFile("./src/b.js", "b\r\n"), new ProducedFile("src/a.js", "a")],
            [new DeclaredFile("src/a.js", FileAction.Create), new DeclaredFile("src/b.js", FileAction.Modify)]);

        Assert.Equal(
            [new FileManifestEntry("src/a.js", FileAction.Create, ContentHash.Compute("a")), new FileManifestEntry("src/b.js", FileAction.Modify, ContentHash.Compute("b\n"))],
            manifest);
    }

    // ---------- D13: the Fake code generator ----------

    [Fact]
    public void FR31_The_Fake_code_generator_writes_exactly_the_declared_files_and_passes_the_Guardian()
    {
        var prompt = """
            <project_request>
            # Todo API
            </project_request>

            <step number="3">
            <step_title>
            REST API
            </step_title>
            <declared_files>
            - src/routes/todos.js (create)
            - src/server.js (modify)
            - package.json (modify)
            </declared_files>
            </step>
            """;
        var request = new LlmRequest { Model = "fake-1", System = "s", Messages = [new LlmMessage(LlmRole.User, prompt)], MaxTokens = 100, Agent = AgentKind.CodeGenerator };
        var responder = new FakeCodeGeneratorResponder();

        var text = responder.Respond(request);
        var reply = CodeGeneration.Parse(text).Value;

        Assert.Equal(text, responder.Respond(request));
        Assert.Equal(3, reply.StepNumber);
        Assert.Equal(["src/routes/todos.js", "src/server.js", "package.json"], reply.Files.Select(f => f.Path));
        StepState[] plan =
        [
            new(1, StepStatus.Completed, [], ["package.json", "src/server.js"]),
            new(2, StepStatus.Completed, [1], ["src/models/todo.js"]),
            new(3, StepStatus.InProgress, [1, 2], ["src/routes/todos.js", "src/server.js", "package.json"]),
        ];
        Assert.Empty(ExecutionGuardian.Validate(new GuardianInput(3, reply.StepNumber, plan, reply.Files, 20)).Violations);
    }
}
