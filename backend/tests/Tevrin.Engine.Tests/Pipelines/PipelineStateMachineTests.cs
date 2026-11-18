using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-287, FR-288, §4.51–§4.53: the generic state machine on the fixture definition (draft → check → summarise).</summary>
public class PipelineStateMachineTests
{
    private const string Guard = FixturePack.ReportConsistentGuard;

    private static readonly PipelineDefinition Definition = Fixtures.Load("fixture-review.pipeline.yaml");

    /// <summary>The fixture with summarise's <c>no_pending_gates</c> excepting its own entry gate (as the SDLC's release does).</summary>
    private static readonly PipelineDefinition OwnGateExcepted = Definition with
    {
        Stages = [.. Definition.Stages.Select(s => s.Id == "summarise"
            ? s with { Requires = [new ArtifactLockedRequirement("report"), new NoPendingGatesRequirement(["report_approval"])] }
            : s)],
    };

    private static RunState At(string stage, string status, int checkpoint = 3, int? checkIterations = null) =>
        new(stage, status, checkpoint, checkIterations is { } n
            ? new Dictionary<string, int>(StringComparer.Ordinal) { ["check"] = n }
            : new Dictionary<string, int>(StringComparer.Ordinal));

    private static RunFacts Facts(ApprovalMode mode = ApprovalMode.Advisory, bool guardOk = true, string? locked = null) => new()
    {
        Mode = mode,
        LockedArtifacts = locked is null ? [] : [locked],
        Guards = new Dictionary<string, GuardResult>(StringComparer.Ordinal)
        {
            [Guard] = guardOk ? GuardResult.Pass : GuardResult.Fail("The report contradicts the draft.", "The report has no verdict."),
        },
        Settings = new Dictionary<string, int>(StringComparer.Ordinal) { [FixturePack.RedraftCycles] = 2 },
    };

    private static RunTransition Applied(Result<RunTransition> result)
    {
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value;
    }

    private static DomainError Failed<T>(Result<T> result)
    {
        Assert.False(result.IsSuccess);
        return result.Error!;
    }

    [Fact]
    public void FR287_a_new_run_starts_in_the_start_stage_at_checkpoint_1()
    {
        var state = PipelineStateMachine.Start(Definition, "draft");

        Assert.Equal(("draft", "drafting", 1), (state.Stage, state.PackStatus, state.Checkpoint));
        Assert.Equal(1, state.IterationsOf("draft"));
        Assert.Throws<ArgumentException>(() => PipelineStateMachine.Start(Definition, "check"));
    }

    [Fact]
    public void FR287_advance_moves_to_the_next_stage_with_its_status_and_checkpoint_plus_1()
    {
        var moved = Applied(PipelineStateMachine.Apply(At("draft", "drafting"), Transition.Advance(), Facts(locked: "draft"), Definition));

        Assert.Equal(("draft", "check", "checking", GenericRunStatus.Active, 4, RunTransitionKind.Advance), (moved.From, moved.To, moved.PackStatus, moved.Status, moved.Checkpoint, moved.Kind));
        Assert.Equal("Advanced to check", moved.Reason);
        Assert.Equal(1, moved.Iterations["check"]);
        Assert.False(moved.ClearsLoopState);
        Assert.Null(moved.EnqueueOnEnter);
        Assert.Equal(moved.NewState, new RunState("check", "checking", 4, moved.Iterations));
    }

    [Fact]
    public void FR287_a_given_reason_replaces_the_default()
    {
        var moved = Applied(PipelineStateMachine.Apply(At("draft", "drafting"), Transition.Advance("because"), Facts(locked: "draft"), Definition));

        Assert.Equal("because", moved.Reason);
    }

    [Fact]
    public void FR287_advance_to_a_stage_that_is_not_next_is_WRONG_PHASE()
    {
        var error = Failed(PipelineStateMachine.Apply(At("draft", "drafting"), Transition.AdvanceTo("summarise"), Facts(locked: "draft"), Definition));

        Assert.Equal((EngineErrorCodes.WrongPhase, "Forward transitions go to the next phase only: draft → check, not summarise."), (error.Code, error.Message));
    }

    [Fact]
    public void FR287_advance_from_the_last_stage_is_WRONG_PHASE_naming_its_title()
    {
        var error = Failed(PipelineStateMachine.Apply(At("summarise", "summarising"), Transition.Advance(), Facts(), Definition));

        Assert.Equal((EngineErrorCodes.WrongPhase, "Summarise is the last phase; there is nothing to advance to."), (error.Code, error.Message));
    }

    [Fact]
    public void FR287_FR288_unmet_requires_and_the_gate_are_PHASE_PRECONDITION_FAILED_in_declared_order()
    {
        var facts = Facts(ApprovalMode.Strict) with { PendingGates = ["escalation"] };

        var error = Failed(PipelineStateMachine.Apply(At("check", "checking"), Transition.Advance(), facts, Definition));

        Assert.Equal(EngineErrorCodes.PhasePreconditionFailed, error.Code);
        Assert.Equal(["The check report is not approved.", "Other gates are pending: escalation.", "The report_approval gate is not approved."], error.Details);
        Assert.Equal("Can't enter summarise: " + string.Join(" ", error.Details), error.Message);
    }

    [Fact]
    public void FR287_a_failing_guard_contributes_its_own_reasons_in_place()
    {
        var definition = Definition with
        {
            Stages = [.. Definition.Stages.Select(s => s.Id == "check" ? s with { Requires = [new GuardRequirement(Guard), new ArtifactLockedRequirement("draft")] } : s)],
        };

        var error = Failed(PipelineStateMachine.Apply(At("draft", "drafting"), Transition.Advance(), Facts(guardOk: false), definition));

        Assert.Equal(["The report contradicts the draft.", "The report has no verdict.", "The draft is not approved."], error.Details);
    }

    [Fact]
    public void FR287_a_guard_the_caller_did_not_evaluate_fails_loudly()
    {
        var definition = Definition with
        {
            Stages = [.. Definition.Stages.Select(s => s.Id == "check" ? s with { Requires = [new GuardRequirement(Guard)] } : s)],
        };

        Assert.Throws<InvalidOperationException>(() => PipelineStateMachine.Apply(At("draft", "drafting"), Transition.Advance(), RunFacts.Empty, definition));
    }

    [Fact]
    public void FR287_the_entry_gate_applies_only_in_its_modes()
    {
        var autonomous = Applied(PipelineStateMachine.Apply(At("check", "checking"), Transition.Advance(), Facts(ApprovalMode.Autonomous, locked: "report"), Definition));
        var approved = Applied(PipelineStateMachine.Apply(At("check", "checking"), Transition.Advance(), Facts(ApprovalMode.Strict, locked: "report") with { EntryGateApproved = true }, Definition));

        Assert.Equal("summarise", autonomous.To);
        Assert.Equal("summarise", approved.To);
    }

    [Fact]
    public void FR288_request_advance_needs_the_gate_then_GATE_PENDING_then_applies_once_approved()
    {
        var state = At("check", "checking");
        var facts = Facts(ApprovalMode.Advisory, locked: "report");

        var needs = PipelineStateMachine.RequestAdvance(state, facts, OwnGateExcepted);
        var pending = PipelineStateMachine.RequestAdvance(state, facts with { PendingGates = ["report_approval"] }, OwnGateExcepted);
        var applied = PipelineStateMachine.RequestAdvance(state, facts with { EntryGateApproved = true }, OwnGateExcepted, "approved");

        Assert.Equal(("report_approval", (RunTransition?)null), (needs.Value.GateRequired, needs.Value.Transition));
        Assert.Equal((EngineErrorCodes.GatePending, "A report_approval gate is already pending."), (pending.Error!.Code, pending.Error.Message));
        Assert.Equal(("summarise", 4, "approved"), (applied.Value.Transition!.To, applied.Value.Transition.Checkpoint, applied.Value.Transition.Reason));
    }

    [Fact]
    public void FR288_request_advance_checks_requires_before_the_gate_and_without_it()
    {
        var error = Failed(PipelineStateMachine.RequestAdvance(At("check", "checking"), Facts(ApprovalMode.Strict) with { PendingGates = ["report_approval"] }, OwnGateExcepted));

        Assert.Equal(EngineErrorCodes.PhasePreconditionFailed, error.Code);
        Assert.Equal(["The check report is not approved."], error.Details);
    }

    [Fact]
    public void FR288_request_advance_from_the_last_stage_is_WRONG_PHASE() =>
        Assert.Equal(EngineErrorCodes.WrongPhase, Failed(PipelineStateMachine.RequestAdvance(At("summarise", "summarising"), Facts(), Definition)).Code);

    [Fact]
    public void FR287_loop_goes_back_with_the_checkpoint_unchanged_and_counts_the_entry()
    {
        var moved = Applied(PipelineStateMachine.Apply(At("check", "checking", checkIterations: 1), Transition.Loop("needs_redraft"), Facts(), Definition));

        Assert.Equal(("check", "draft", "drafting", 3, RunTransitionKind.Loop), (moved.From, moved.To, moved.PackStatus, moved.Checkpoint, moved.Kind));
        Assert.Equal("Loop: re-entering draft", moved.Reason);
        Assert.Equal((1, 1), (moved.Iterations["draft"], moved.Iterations["check"]));
        Assert.False(moved.ClearsLoopState);
    }

    [Fact]
    public void FR287_loop_checks_only_the_loop_requires_never_the_target_stage_requires_or_gate()
    {
        var definition = Definition with
        {
            Stages = [.. Definition.Stages.Select(s => s.Id switch
            {
                "draft" => s with { Requires = [new ArtifactLockedRequirement("summary")], Gate = new StageGate("draft_approval", [ApprovalMode.Strict], null, false) },
                "check" => s with { Loop = s.Loop! with { Requires = [new GuardRequirement(Guard)] } },
                _ => s,
            })],
        };

        var moved = PipelineStateMachine.Apply(At("check", "checking"), Transition.Loop("needs_redraft"), Facts(ApprovalMode.Strict), definition);
        var refused = Failed(PipelineStateMachine.Apply(At("check", "checking"), Transition.Loop("needs_redraft"), Facts(ApprovalMode.Strict, guardOk: false), definition));

        Assert.True(moved.IsSuccess);
        Assert.Equal((EngineErrorCodes.PhasePreconditionFailed, "Can't enter draft: The report contradicts the draft. The report has no verdict."), (refused.Code, refused.Message));
    }

    [Fact]
    public void FR287_loop_needs_the_outcome_it_is_declared_on()
    {
        var error = Failed(PipelineStateMachine.Apply(At("check", "checking"), Transition.Loop("passed"), Facts(), Definition));

        Assert.Equal(EngineErrorCodes.PhasePreconditionFailed, error.Code);
        Assert.Equal(["The check outcome is passed, not needs_redraft."], error.Details);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void FR287_loop_is_refused_once_the_stage_has_run_as_often_as_the_cap(int checkIterations, bool allowed)
    {
        var result = PipelineStateMachine.Apply(At("check", "checking", checkIterations: checkIterations), Transition.Loop("needs_redraft"), Facts(), Definition);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal(["The loop limit of 2 is reached."], result.Error!.Details);
        }
    }

    [Fact]
    public void FR291_the_loop_cap_is_a_backstop_and_adds_no_reason_to_a_loop_already_refused()
    {
        var error = Failed(PipelineStateMachine.Apply(At("check", "checking", checkIterations: 5), Transition.Loop("passed"), Facts(), Definition));

        Assert.Equal(["The check outcome is passed, not needs_redraft."], error.Details);
    }

    [Fact]
    public void FR287_a_loop_cap_setting_the_caller_did_not_resolve_fails_loudly() =>
        Assert.Throws<InvalidOperationException>(() =>
            PipelineStateMachine.Apply(At("check", "checking"), Transition.Loop("needs_redraft"), Facts() with { Settings = new Dictionary<string, int>() }, Definition));

    [Theory]
    [InlineData("draft", null)]
    [InlineData("check", "summarise")]
    public void FR287_loop_from_a_stage_without_one_or_to_another_stage_is_WRONG_PHASE(string stage, string? target)
    {
        var error = Failed(PipelineStateMachine.Apply(At(stage, "checking"), new Transition(RunTransitionKind.Loop, target, LoopOutcome: "needs_redraft"), Facts(), Definition));

        Assert.Equal((EngineErrorCodes.WrongPhase, "A loop moves check → draft only."), (error.Code, error.Message));
    }

    [Fact]
    public void FR287_rollback_sets_the_target_checkpoint_the_rollback_status_and_restores_iterations()
    {
        var restored = new Dictionary<string, int>(StringComparer.Ordinal) { ["draft"] = 1 };

        var moved = Applied(PipelineStateMachine.Apply(At("summarise", "summarising", checkpoint: 5, checkIterations: 2), Transition.Rollback("check", 2, restored), Facts(), Definition));

        Assert.Equal(("check", "rolled_back", GenericRunStatus.Active, 2, "Rolled back to checkpoint 2"), (moved.To, moved.PackStatus, moved.Status, moved.Checkpoint, moved.Reason));
        Assert.Equal(restored, moved.Iterations);
        Assert.True(moved.ClearsLoopState);
    }

    [Fact]
    public void FR287_rollback_without_a_rollback_status_takes_the_target_stage_status()
    {
        var moved = Applied(PipelineStateMachine.Apply(At("summarise", "summarising", checkpoint: 5), Transition.Rollback("summarise", 4), Facts(), Definition with { RollbackStatus = null }));

        Assert.Equal(("summarise", "summarising", 4), (moved.To, moved.PackStatus, moved.Checkpoint));
        Assert.Empty(moved.Iterations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(6)]
    public void FR287_rollback_to_a_checkpoint_not_below_the_current_or_below_1_is_INVALID_TARGET_VERSION(int? checkpoint)
    {
        var error = Failed(PipelineStateMachine.Apply(At("summarise", "summarising", checkpoint: 5), new Transition(RunTransitionKind.Rollback, "draft", checkpoint), Facts(), Definition));

        Assert.Equal((EngineErrorCodes.InvalidTargetVersion, "The target checkpoint must be an existing checkpoint below the current checkpoint 5."), (error.Code, error.Message));
    }

    [Theory]
    [InlineData("summarise")]
    [InlineData(null)]
    [InlineData("nowhere")]
    public void FR287_rollback_to_a_later_or_unknown_stage_is_WRONG_PHASE(string? target)
    {
        var error = Failed(PipelineStateMachine.Apply(At("check", "checking", checkpoint: 5), new Transition(RunTransitionKind.Rollback, target, 2), Facts(), Definition));

        Assert.Equal((EngineErrorCodes.WrongPhase, "A rollback can't move to a later phase."), (error.Code, error.Message));
    }

    [Fact]
    public void FR287_fallback_moves_to_an_earlier_stage_with_its_status_and_clears_the_loop_state()
    {
        var moved = Applied(PipelineStateMachine.Apply(At("summarise", "summarising", checkIterations: 2), Transition.Fallback("draft"), Facts(), Definition));

        Assert.Equal(("draft", "drafting", 3, "Fell back to draft after invalidation"), (moved.To, moved.PackStatus, moved.Checkpoint, moved.Reason));
        Assert.Empty(moved.Iterations);
        Assert.True(moved.ClearsLoopState);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("summarise")]
    [InlineData(null)]
    public void FR287_fallback_to_the_same_or_a_later_stage_is_WRONG_PHASE(string? target)
    {
        var error = Failed(PipelineStateMachine.Apply(At("check", "checking"), new Transition(RunTransitionKind.Fallback, target), Facts(), Definition));

        Assert.Equal((EngineErrorCodes.WrongPhase, "An invalidation fallback moves to an earlier phase only."), (error.Code, error.Message));
    }

    [Fact]
    public void FR287_status_change_moves_the_status_only_and_can_complete_the_run()
    {
        var moved = Applied(PipelineStateMachine.Apply(At("summarise", "summarising", checkIterations: 1), Transition.Change("summary_published"), Facts(), Definition));

        Assert.Equal(("summarise", "published", GenericRunStatus.Completed, 3, "Summary published"), (moved.To, moved.PackStatus, moved.Status, moved.Checkpoint, moved.Reason));
        Assert.True(moved.CompletesRun);
        Assert.Equal("summary_published", moved.StatusChange);
        Assert.Equal(1, moved.Iterations["check"]);
    }

    [Theory]
    [InlineData("summarise", "published")]
    [InlineData("check", "summarising")]
    public void FR287_status_change_from_another_stage_or_status_fails_with_its_error_code(string stage, string status)
    {
        var error = Failed(PipelineStateMachine.Apply(At(stage, status), Transition.Change("summary_published"), Facts(), Definition));

        Assert.Equal(
            ("NOT_SUMMARISING", $"The project is in {stage} with status {status}, not summarise with status summarising."),
            (error.Code, error.Message));
    }

    [Fact]
    public void FR287_status_change_uses_the_definition_reason_and_error_message()
    {
        var change = Definition.StatusChanges[0] with { Reason = "Published", ErrorMessage = "Not {change_stage} ({from}); it is {stage}/{status}." };
        var definition = Definition with { StatusChanges = [change] };

        var moved = Applied(PipelineStateMachine.Apply(At("summarise", "summarising"), Transition.Change("summary_published"), Facts(), definition));
        var error = Failed(PipelineStateMachine.Apply(At("draft", "drafting"), Transition.Change("summary_published"), Facts(), definition));

        Assert.Equal("Published", moved.Reason);
        Assert.Equal("Not summarise (summarising); it is draft/drafting.", error.Message);
    }

    [Theory]
    [InlineData("summarising")]
    [InlineData("published")]
    public void FR293_a_status_change_naming_another_stage_is_wrong_phase_with_the_definition_message(string status)
    {
        var change = Definition.StatusChanges[0] with { WrongStageMessage = "It stays in {change_stage}, not {target}." };
        var definition = Definition with { StatusChanges = [change] };

        var error = Failed(PipelineStateMachine.Apply(
            At("summarise", status), new Transition(RunTransitionKind.StatusChange, "check", StatusChange: "summary_published"), Facts(), definition));
        var same = PipelineStateMachine.Apply(
            At("summarise", "summarising"), new Transition(RunTransitionKind.StatusChange, "summarise", StatusChange: "summary_published"), Facts(), definition);

        Assert.Equal((EngineErrorCodes.WrongPhase, "It stays in summarise, not check."), (error.Code, error.Message));
        Assert.True(same.IsSuccess);
    }

    [Fact]
    public void FR293_without_a_wrong_stage_message_a_named_target_is_ignored() =>
        Assert.True(PipelineStateMachine.Apply(
            At("summarise", "summarising"), new Transition(RunTransitionKind.StatusChange, "check", StatusChange: "summary_published"), Facts(), Definition).IsSuccess);

    [Fact]
    public void FR287_an_unknown_status_change_is_a_programming_error() =>
        Assert.Throws<ArgumentException>(() => PipelineStateMachine.Apply(At("summarise", "summarising"), Transition.Change("nope"), Facts(), Definition));

    [Fact]
    public void FR287_noop_keeps_stage_status_and_checkpoint_in_the_final_stage()
    {
        var moved = Applied(PipelineStateMachine.Apply(At("summarise", "published"), Transition.Noop(), Facts(), Definition));

        Assert.Equal(("summarise", "published", 3, "No-op", RunTransitionKind.Noop), (moved.To, moved.PackStatus, moved.Checkpoint, moved.Reason, moved.Kind));
    }

    [Theory]
    [InlineData("check", null)]
    [InlineData("summarise", "check")]
    public void FR287_noop_outside_the_final_stage_is_WRONG_PHASE(string stage, string? target)
    {
        var error = Failed(PipelineStateMachine.Apply(At(stage, "checking"), new Transition(RunTransitionKind.Noop, target), Facts(), Definition));

        Assert.Equal((EngineErrorCodes.WrongPhase, "summarise → summarise is only valid in the summarise phase."), (error.Code, error.Message));
    }

    [Fact]
    public void FR287_advance_reports_the_entered_stage_enqueue_on_enter_job_and_only_advance_does()
    {
        var definition = Definition with
        {
            Stages = [.. Definition.Stages.Select(s => s with { EnqueueOnEnter = s.Id is "draft" or "check" ? "agent.run" : null })],
        };

        var advanced = Applied(PipelineStateMachine.Apply(At("draft", "drafting"), Transition.Advance(), Facts(locked: "draft"), definition));
        var looped = Applied(PipelineStateMachine.Apply(At("check", "checking"), Transition.Loop("needs_redraft"), Facts(), definition));
        var fellBack = Applied(PipelineStateMachine.Apply(At("summarise", "summarising"), Transition.Fallback("check"), Facts(), definition));

        Assert.Equal("agent.run", advanced.EnqueueOnEnter);
        Assert.Null(looped.EnqueueOnEnter);
        Assert.Null(fellBack.EnqueueOnEnter);
    }

    [Fact]
    public void FR287_advance_follows_on_success()
    {
        var definition = Definition with
        {
            Stages = [.. Definition.Stages.Select(s => s.Id == "draft" ? s with { OnSuccess = "summarise" } : s)],
        };

        var error = Failed(PipelineStateMachine.Apply(At("draft", "drafting"), Transition.Advance(), Facts(), definition));

        Assert.StartsWith("Can't enter summarise:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR287_a_state_in_an_unknown_stage_is_a_programming_error() =>
        Assert.Throws<ArgumentException>(() => PipelineStateMachine.Apply(At("gone", "drafting"), Transition.Noop(), Facts(), Definition));
}
