using Aide.Domain.Artifacts;
using Aide.Domain.Execution;

namespace Aide.UnitTests.Execution;

/// <summary>FR-57 execution runs and D-V2-14 attempt history.</summary>
public class ExecutionRunRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static RunPlan Plan(ArtifactType type, int version, int minutes, ArtifactStatus status = ArtifactStatus.Locked) =>
        new(Guid.CreateVersion7(), type, version, status, T0.AddMinutes(minutes));

    [Fact]
    public void FR57_The_development_run_comes_first_then_one_review_fix_run_per_cycle_in_cycle_order()
    {
        var development = Plan(ArtifactType.ImplementationPlan, 1, 0);
        var fix1 = Plan(ArtifactType.OptimizationPlan, 1, 10);
        var fix2 = Plan(ArtifactType.OptimizationPlan, 2, 20);
        var reports = new List<RunReport> { new(Guid.CreateVersion7(), 1, T0.AddMinutes(9), fix1.Id), new(Guid.CreateVersion7(), 2, T0.AddMinutes(19), fix2.Id) };

        var ordered = ExecutionRunRules.Order([fix2, development, fix1], p => ExecutionRunRules.ReportOf(p, reports));

        Assert.Equal([development.Id, fix1.Id, fix2.Id], ordered.Select(p => p.Id));
        Assert.Equal([null, 1, 2], ordered.Select(p => ExecutionRunRules.CycleOf(p, reports)));
    }

    [Fact]
    public void FR57_An_edited_optimisation_plan_belongs_to_the_latest_cycle_reported_before_it()
    {
        var drafted = Plan(ArtifactType.OptimizationPlan, 1, 10, ArtifactStatus.Superseded);
        var edited = Plan(ArtifactType.OptimizationPlan, 2, 12);
        var reports = new List<RunReport> { new(Guid.CreateVersion7(), 1, T0.AddMinutes(1), null), new(Guid.CreateVersion7(), 2, T0.AddMinutes(10), drafted.Id), new(Guid.CreateVersion7(), 3, T0.AddMinutes(30), null) };

        Assert.Equal(2, ExecutionRunRules.CycleOf(edited, reports));
        Assert.Null(ExecutionRunRules.CycleOf(Plan(ArtifactType.OptimizationPlan, 1, 0), [new(Guid.CreateVersion7(), 1, T0.AddMinutes(5), null)]));
        Assert.Null(ExecutionRunRules.CycleOf(Plan(ArtifactType.ImplementationPlan, 1, 50), reports));
    }

    [Fact]
    public void FR56_Two_cycle_1_reports_after_a_rollback_each_link_to_their_own_run_and_order_by_report()
    {
        var first = Plan(ArtifactType.OptimizationPlan, 1, 10, ArtifactStatus.Invalidated);
        var second = Plan(ArtifactType.OptimizationPlan, 2, 40);
        var before = new RunReport(Guid.CreateVersion7(), 1, T0.AddMinutes(9), first.Id);
        var after = new RunReport(Guid.CreateVersion7(), 1, T0.AddMinutes(39), second.Id);
        RunReport[] reports = [before, after];

        Assert.Equal(first.Id, ExecutionRunRules.RunOfReport(before.Id, [first, second], reports)!.Id);
        Assert.Equal(second.Id, ExecutionRunRules.RunOfReport(after.Id, [first, second], reports)!.Id);
        Assert.Null(ExecutionRunRules.RunOfReport(Guid.CreateVersion7(), [first, second], reports));
        Assert.Equal([first.Id, second.Id], ExecutionRunRules.Order([second, first], p => ExecutionRunRules.ReportOf(p, reports)).Select(p => p.Id));
    }

    [Theory]
    [InlineData(ArtifactStatus.Invalidated, new[] { StepStatus.Invalidated, StepStatus.Invalidated }, true, false, false, PlanRunStatus.Superseded)]
    [InlineData(ArtifactStatus.Invalidated, new[] { StepStatus.Completed }, false, false, false, PlanRunStatus.Superseded)]
    [InlineData(ArtifactStatus.Locked, new[] { StepStatus.Invalidated }, false, false, false, PlanRunStatus.Superseded)]
    [InlineData(ArtifactStatus.Locked, new[] { StepStatus.Completed, StepStatus.Completed }, false, false, false, PlanRunStatus.Completed)]
    [InlineData(ArtifactStatus.Locked, new[] { StepStatus.Completed, StepStatus.Blocked }, true, false, true, PlanRunStatus.Blocked)]
    [InlineData(ArtifactStatus.Locked, new[] { StepStatus.Pending, StepStatus.Pending }, true, false, false, PlanRunStatus.Pending)]
    [InlineData(ArtifactStatus.Locked, new[] { StepStatus.Pending, StepStatus.Pending }, true, false, true, PlanRunStatus.Running)]
    [InlineData(ArtifactStatus.Locked, new[] { StepStatus.Pending, StepStatus.Pending }, true, true, true, PlanRunStatus.Pending)]
    [InlineData(ArtifactStatus.Locked, new[] { StepStatus.Completed, StepStatus.InProgress }, true, false, false, PlanRunStatus.Running)]
    [InlineData(ArtifactStatus.Locked, new[] { StepStatus.Completed, StepStatus.Pending }, true, false, true, PlanRunStatus.Running)]
    [InlineData(ArtifactStatus.Locked, new[] { StepStatus.Completed, StepStatus.InProgress }, true, true, false, PlanRunStatus.Paused)]
    [InlineData(ArtifactStatus.Locked, new[] { StepStatus.Completed, StepStatus.Rejected }, true, false, false, PlanRunStatus.Paused)]
    [InlineData(ArtifactStatus.Locked, new[] { StepStatus.Completed, StepStatus.Pending }, false, false, true, PlanRunStatus.Paused)]
    public void FR57_Run_status_is_derived_from_plan_and_steps(
        ArtifactStatus plan, StepStatus[] steps, bool current, bool paused, bool jobActive, PlanRunStatus expected) =>
        Assert.Equal(expected, ExecutionRunRules.Status(plan, steps, current, paused, jobActive));

    [Fact]
    public void FR57_Superseded_runs_explain_why_their_plan_was_invalidated()
    {
        Assert.Equal(
            "implementation_plan v2 was invalidated when technical_specification v4 was approved.",
            ExecutionRunRules.SupersededReason(ArtifactType.ImplementationPlan, 2, new PlanInvalidation("cascade", ArtifactType.TechnicalSpecification, 4, null)));
        Assert.Equal(
            "implementation_plan v2 was invalidated by a rollback to checkpoint 3.",
            ExecutionRunRules.SupersededReason(ArtifactType.ImplementationPlan, 2, new PlanInvalidation("rollback", null, null, 3)));
        Assert.Equal("optimization_plan v5 was invalidated.", ExecutionRunRules.SupersededReason(ArtifactType.OptimizationPlan, 5, null));
    }

    [Fact]
    public void D_V2_14_A_new_attempt_is_numbered_after_the_highest_recorded_one()
    {
        Assert.Equal(new AttemptSlot(1, false), StepAttemptRules.Slot([], Guid.CreateVersion7()));
        Assert.Equal(new AttemptSlot(1, false), StepAttemptRules.Slot([], null));
        Assert.Equal(new AttemptSlot(3, false), StepAttemptRules.Slot([new(1, Guid.CreateVersion7()), new(2, null)], Guid.CreateVersion7()));
    }

    [Fact]
    public void D_V2_14_A_redelivered_attempt_with_the_same_run_reuses_its_row()
    {
        var run = Guid.CreateVersion7();

        Assert.Equal(new AttemptSlot(2, true), StepAttemptRules.Slot([new(1, Guid.CreateVersion7()), new(2, run)], run));
    }

    [Fact]
    public void D_V2_14_After_an_escalation_continue_history_keeps_counting_up()
    {
        // Four rejected attempts, then the step's retry counter is reset: the next attempt is the fifth in its history.
        var recorded = Enumerable.Range(1, 4).Select(n => new RecordedAttempt(n, Guid.CreateVersion7())).ToList();

        Assert.Equal(new AttemptSlot(5, false), StepAttemptRules.Slot(recorded, Guid.CreateVersion7()));
    }
}
