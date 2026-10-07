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
            Step(3, [1, 2]),
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
    [InlineData(".git/config")]
    [InlineData("lib/.GIT/hooks/post-merge")]
    public void FR29_Declaring_a_git_internal_path_is_invalid(string path) =>
        Assert.Equal(ErrorCodes.InvalidPlan, PlanValidator.Validate([Step(1, null, new DeclaredFile(path, FileAction.Modify))]).Error!.Code);

    [Fact]
    public void FR29_Missing_title_or_task_is_invalid() =>
        Assert.Equal(2, PlanValidator.Validate([new PlanStep(1, " ", "", [], [])]).Error!.Details.Count);

    [Fact]
    public void FR29_Every_problem_is_reported()
    {
        PlanStep[] plan = [Step(1, [1]), Step(3, [4])];

        Assert.Equal(3, PlanValidator.Validate(plan).Error!.Details.Count);
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
