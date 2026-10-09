using Aide.Domain.Common;
using Aide.Domain.Execution;

namespace Aide.UnitTests.Execution;

/// <summary>FR-29 (plan validation), FR-30, FR-31, FR-33, D17.</summary>
public class PlanValidatorTests
{
    private static PlanStep Step(int number, int[]? deps = null, params DeclaredFile[] files) =>
        new(number, $"Step {number}", $"Do {number}", files, deps ?? []);

    [Fact]
    public void FR29_Valid_plan_passes_with_paths_normalised()
    {
        PlanStep[] plan =
        [
            Step(1, null, new DeclaredFile("./src/app.ts", FileAction.Create)),
            Step(2, [1], new DeclaredFile("src\\app.ts", FileAction.Modify)),
            Step(3, [1, 2], new DeclaredFile("README.md", FileAction.Create)),
        ];

        var result = PlanValidator.Validate(plan);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("src/app.ts", result.Value[0].Files[0].Path);
        Assert.Equal("src/app.ts", result.Value[1].Files[0].Path);
    }

    [Fact]
    public void FR29_Step3_depending_on_step5_is_invalid_plan()
    {
        PlanStep[] plan = [Step(1), Step(2), Step(3, [5]), Step(4), Step(5)];

        var result = PlanValidator.Validate(plan);

        Assert.Equal(ErrorCodes.InvalidPlan, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.Contains("dependencies must point to earlier steps", StringComparison.Ordinal));
    }

    [Fact]
    public void FR29_Empty_plan_is_invalid() =>
        Assert.Equal(ErrorCodes.InvalidPlan, PlanValidator.Validate([]).Error!.Code);

    [Theory]
    [InlineData(new[] { 1, 3 })]
    [InlineData(new[] { 2, 1 })]
    [InlineData(new[] { 0, 1 })]
    [InlineData(new[] { 1, 1 })]
    public void FR29_Steps_must_be_numbered_1_to_n_in_order(int[] numbers)
    {
        var plan = numbers.Select(n => Step(n)).ToList();

        Assert.Equal(ErrorCodes.InvalidPlan, PlanValidator.Validate(plan).Error!.Code);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void FR29_Self_or_zero_dependency_is_invalid(int dep) =>
        Assert.Equal(ErrorCodes.InvalidPlan, PlanValidator.Validate([Step(1), Step(2, [dep])]).Error!.Code);

    [Fact]
    public void FR29_A_file_created_by_two_steps_is_invalid()
    {
        PlanStep[] plan =
        [
            Step(1, null, new DeclaredFile("src/a.ts", FileAction.Create)),
            Step(2, [1], new DeclaredFile("./src/a.ts", FileAction.Create)),
        ];

        var result = PlanValidator.Validate(plan);

        Assert.Contains(result.Error!.Details, d => d.Contains("created by step 1 and again by step 2", StringComparison.Ordinal));
    }

    [Fact]
    public void FR29_Unsafe_declared_path_is_invalid() =>
        Assert.Equal(ErrorCodes.InvalidPlan, PlanValidator.Validate([Step(1, null, new DeclaredFile("../x.ts", FileAction.Create))]).Error!.Code);

    [Theory]
    [InlineData("a\nb.ts")]
    [InlineData("a\rb.ts")]
    [InlineData("a`b.ts")]
    [InlineData("a\u007fb.ts")]
    public void FR29_Paths_with_line_breaks_backticks_or_control_characters_are_unsafe(string path)
    {
        Assert.True(PathRules.IsUnsafe(PathRules.Normalize(path)));
        Assert.Equal(ErrorCodes.InvalidPlan, PlanValidator.Validate([Step(1, null, new DeclaredFile(path, FileAction.Create))]).Error!.Code);
    }

    [Fact]
    public void FR29_No_plan_may_declare_a_path_under_aide_even_when_protected_paths_are_allowed()
    {
        var result = PlanValidator.Validate([Step(1, null, new DeclaredFile(".aide/rules.md", FileAction.Modify))], allowProtectedPaths: true);

        Assert.Equal(ErrorCodes.InvalidPlan, result.Error!.Code);
        Assert.Contains(result.Error.Details!, d => d.Contains(".aide/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(StepStatus.Blocked, true)]
    [InlineData(StepStatus.Rejected, true)]
    [InlineData(StepStatus.Pending, false)]
    [InlineData(StepStatus.Completed, false)]
    [InlineData(StepStatus.Invalidated, false)]
    [InlineData(StepStatus.InProgress, false)]
    public void S4_8_Rollback_requeues_only_blocked_or_rejected_steps_of_a_surviving_plan(StepStatus status, bool requeued) =>
        Assert.Equal(requeued, StepRules.RequeueOnRollback(status));

    [Theory]
    [InlineData(StepStatus.InProgress, StepStatus.Rejected, true)]
    [InlineData(StepStatus.Pending, StepStatus.Pending, false)]
    [InlineData(StepStatus.Completed, StepStatus.Completed, false)]
    [InlineData(StepStatus.Blocked, StepStatus.Blocked, false)]
    public void FR33_An_ended_execution_job_turns_its_in_progress_step_into_a_rejected_attempt(StepStatus status, StepStatus expected, bool changed) =>
        Assert.Equal(new StepStatusChange(expected, changed), StepRules.AbandonAttempt(status));

    [Fact]
    public void FR33_An_abandoned_attempt_is_retried_as_the_next_attempt()
    {
        var abandoned = StepRules.AbandonAttempt(StepStatus.InProgress);
        var retry = StepRules.BeginAttempt(abandoned.NewStatus, 2);

        Assert.Equal(new StepAttemptStart(3, true), retry.Value);
    }

    [Fact]
    public void FR29_Protected_paths_are_refused_only_when_protected_paths_are_not_allowed()
    {
        PlanStep[] plan = [Step(1, null, new DeclaredFile(".github/workflows/ci.yml", FileAction.Create), new DeclaredFile("yarn.lock", FileAction.Modify))];

        Assert.True(PlanValidator.Validate(plan).IsSuccess);
        var refused = PlanValidator.Validate(plan, allowProtectedPaths: false);
        Assert.Equal(2, refused.Error!.Details.Count);
        Assert.All(refused.Error.Details, d => Assert.Contains("protected path", d, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData("lib/.GIT/hooks/post-merge")]
    public void FR29_Declaring_a_git_internal_path_is_invalid(string path) =>
        Assert.Equal(ErrorCodes.InvalidPlan, PlanValidator.Validate([Step(1, null, new DeclaredFile(path, FileAction.Modify))]).Error!.Code);

    [Fact]
    public void FR29_Missing_title_or_task_is_invalid() =>
        Assert.Equal(2, PlanValidator.Validate([new PlanStep(1, " ", "", [new DeclaredFile("a.txt", FileAction.Create)], [])]).Error!.Details.Count);

    [Fact]
    public void FR29_Every_problem_is_reported()
    {
        PlanStep[] plan = [Step(1, [1]), Step(3, [4])];

        // Self dependency, numbering, forward dependency, and both steps without files.
        Assert.Equal(5, PlanValidator.Validate(plan).Error!.Details.Count);
    }

    [Fact]
    public void FR29_A_step_without_declared_files_is_invalid()
    {
        var result = PlanValidator.Validate([Step(1)]);

        Assert.Contains(result.Error!.Details, d => d.Contains("declares no files", StringComparison.Ordinal));
    }

    [Fact]
    public void FR29_Declaring_more_files_than_the_project_allows_per_step_is_invalid()
    {
        var files = Enumerable.Range(1, 3).Select(i => new DeclaredFile($"src/f{i}.ts", FileAction.Create)).ToArray();

        Assert.True(PlanValidator.Validate([Step(1, null, files)], maxFilesPerStep: 3).IsSuccess);
        var result = PlanValidator.Validate([Step(1, null, files)], maxFilesPerStep: 2);
        Assert.Contains(result.Error!.Details, d => d.Contains("declares 3 files; at most 2", StringComparison.Ordinal));
    }

    [Fact]
    public void FR29_Modifying_a_file_before_the_step_that_creates_it_is_invalid()
    {
        PlanStep[] plan =
        [
            Step(1, null, new DeclaredFile("src/a.ts", FileAction.Modify)),
            Step(2, [1], new DeclaredFile("src/a.ts", FileAction.Create)),
        ];

        Assert.Contains(PlanValidator.Validate(plan).Error!.Details, d => d.Contains("modifies 'src/a.ts' before step 2 creates it", StringComparison.Ordinal));
    }

    [Fact]
    public void FR29_Modifying_a_file_requires_depending_on_its_creator_directly_or_indirectly()
    {
        PlanStep[] missing =
        [
            Step(1, null, new DeclaredFile("src/a.ts", FileAction.Create)),
            Step(2, null, new DeclaredFile("src/b.ts", FileAction.Create)),
            Step(3, [2], new DeclaredFile("src/a.ts", FileAction.Modify)),
        ];
        PlanStep[] indirect =
        [
            Step(1, null, new DeclaredFile("src/a.ts", FileAction.Create)),
            Step(2, [1], new DeclaredFile("src/b.ts", FileAction.Create)),
            Step(3, [2], new DeclaredFile("src/a.ts", FileAction.Modify)),
        ];

        Assert.Contains(PlanValidator.Validate(missing).Error!.Details, d => d.Contains("but doesn't depend on step 1", StringComparison.Ordinal));
        Assert.True(PlanValidator.Validate(indirect).IsSuccess);
    }

    [Fact]
    public void FR29_Modifying_a_file_the_plan_does_not_create_is_allowed()
    {
        Assert.True(PlanValidator.Validate([Step(1, null, new DeclaredFile("README.md", FileAction.Modify))]).IsSuccess);
    }

    [Fact]
    public void FR29_More_than_the_maximum_number_of_steps_is_invalid()
    {
        var plan = Enumerable.Range(1, PlanValidator.MaxSteps + 1)
            .Select(n => Step(n, null, new DeclaredFile($"f{n}.txt", FileAction.Create)))
            .ToList();

        Assert.Contains(PlanValidator.Validate(plan).Error!.Details, d => d.Contains($"at most {PlanValidator.MaxSteps}", StringComparison.Ordinal));
    }
}

public class StepRulesTests
{
    [Fact]
    public void FR31_Steps_1_to_3_completed_and_step4_depending_on_2_runs_step4_next()
    {
        StepState[] steps =
        [
            new(1, StepStatus.Completed),
            new(2, StepStatus.Completed, 1),
            new(3, StepStatus.Completed),
            new(4, StepStatus.Pending, 2),
            new(5, StepStatus.Pending),
        ];

        Assert.Equal(4, StepRules.NextStep(steps));
    }

    [Fact]
    public void FR31_Picks_the_lowest_pending_step_whose_dependencies_are_completed()
    {
        StepState[] steps = [new(1, StepStatus.Completed), new(2, StepStatus.Pending, 3), new(3, StepStatus.Pending, 1)];

        Assert.Equal(3, StepRules.NextStep(steps));
    }

    [Fact]
    public void FR31_Nothing_runs_while_a_step_is_in_progress() =>
        Assert.Null(StepRules.NextStep([new(1, StepStatus.InProgress), new(2, StepStatus.Pending)]));

    [Fact]
    public void FR31_Nothing_runs_when_dependencies_are_blocked() =>
        Assert.Null(StepRules.NextStep([new(1, StepStatus.Blocked), new(2, StepStatus.Pending, 1)]));

    [Fact]
    public void FR33_A_rejected_step_awaiting_retry_runs_before_new_steps() =>
        Assert.Equal(2, StepRules.NextStep([new(1, StepStatus.Completed), new(2, StepStatus.Rejected), new(3, StepStatus.Pending)]));

    [Fact]
    public void FR31_All_completed_means_no_next_step() =>
        Assert.Null(StepRules.NextStep([new(1, StepStatus.Completed), new(2, StepStatus.Completed)]));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FR33_Rejections_1_to_3_are_retried(int attempt) =>
        Assert.Equal(new StepRejectionOutcome(StepStatus.Rejected, true, false), StepRules.OnRejected(attempt));

    [Fact]
    public void FR33_Step_rejected_three_times_is_blocked_with_escalation_on_the_fourth_rejection() =>
        Assert.Equal(new StepRejectionOutcome(StepStatus.Blocked, false, true), StepRules.OnRejected(4));

    [Theory]
    [InlineData(StepStatus.Pending, true)]
    [InlineData(StepStatus.Rejected, true)]
    [InlineData(StepStatus.InProgress, false)]
    public void Start_moves_pending_or_rejected_to_in_progress(StepStatus status, bool changed)
    {
        var result = StepRules.Start(status);

        Assert.Equal(new StepStatusChange(StepStatus.InProgress, changed), result.Value);
    }

    [Theory]
    [InlineData(StepStatus.Completed)]
    [InlineData(StepStatus.Blocked)]
    [InlineData(StepStatus.Invalidated)]
    public void Start_refuses_finished_steps(StepStatus status) =>
        Assert.Equal(ErrorCodes.Conflict, StepRules.Start(status).Error!.Code);

    [Fact]
    public void D17_Redelivered_completion_is_a_no_op()
    {
        Assert.Equal(new StepStatusChange(StepStatus.Completed, true), StepRules.Complete(StepStatus.InProgress).Value);
        Assert.Equal(new StepStatusChange(StepStatus.Completed, false), StepRules.Complete(StepStatus.Completed).Value);
    }

    [Theory]
    [InlineData(StepStatus.Pending)]
    [InlineData(StepStatus.Rejected)]
    [InlineData(StepStatus.Blocked)]
    [InlineData(StepStatus.Invalidated)]
    public void Completing_a_step_that_is_not_running_is_a_conflict(StepStatus status) =>
        Assert.Equal(ErrorCodes.Conflict, StepRules.Complete(status).Error!.Code);

    [Theory]
    [InlineData(StepStatus.Pending, true)]
    [InlineData(StepStatus.Completed, true)]
    [InlineData(StepStatus.Invalidated, false)]
    public void FR19_Invalidate_resets_any_step_once(StepStatus status, bool changed) =>
        Assert.Equal(new StepStatusChange(StepStatus.Invalidated, changed), StepRules.Invalidate(status));
}
