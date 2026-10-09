using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;

namespace Aide.UnitTests.Gates;

/// <summary>§4.2, FR-12–FR-15, D3, D16, D19.</summary>
public class GateRulesTests
{
    [Theory]
    [InlineData(ApprovalMode.Strict, Phase.Planning, GateType.SpecApproval)]
    [InlineData(ApprovalMode.Advisory, Phase.Planning, GateType.SpecApproval)]
    [InlineData(ApprovalMode.Autonomous, Phase.Planning, null)]
    [InlineData(ApprovalMode.Strict, Phase.Execution, GateType.PlanApproval)]
    [InlineData(ApprovalMode.Advisory, Phase.Execution, null)]
    [InlineData(ApprovalMode.Autonomous, Phase.Execution, null)]
    [InlineData(ApprovalMode.Strict, Phase.Release, GateType.PreRelease)]
    [InlineData(ApprovalMode.Advisory, Phase.Release, GateType.PreRelease)]
    [InlineData(ApprovalMode.Autonomous, Phase.Release, GateType.PreRelease)]
    [InlineData(ApprovalMode.Strict, Phase.Refinement, null)]
    [InlineData(ApprovalMode.Strict, Phase.Specification, null)]
    [InlineData(ApprovalMode.Strict, Phase.Review, null)]
    [InlineData(ApprovalMode.Advisory, Phase.Specification, null)]
    [InlineData(ApprovalMode.Advisory, Phase.Review, null)]
    [InlineData(ApprovalMode.Autonomous, Phase.Specification, null)]
    [InlineData(ApprovalMode.Autonomous, Phase.Review, null)]
    public void S4_2_Required_gate_by_mode_and_entered_phase(ApprovalMode mode, Phase entering, GateType? expected) =>
        Assert.Equal(expected, GateRules.RequiredGate(mode, entering));

    [Theory]
    [InlineData(GateType.SpecApproval)]
    [InlineData(GateType.PlanApproval)]
    [InlineData(GateType.PreRelease)]
    public void FR13_Approving_a_pending_gate_applies_the_gated_transition(GateType type)
    {
        var result = GateRules.Approve(type, GateStatus.Pending);

        Assert.Equal(new GateResolution(GateStatus.Approved, GateFollowUp.ApplyTransition), result.Value);
    }

    public static TheoryData<GateType, GateStatus> DecidedGates()
    {
        var data = new TheoryData<GateType, GateStatus>();
        foreach (var type in Enum.GetValues<GateType>())
        {
            foreach (var status in new[] { GateStatus.Approved, GateStatus.Rejected, GateStatus.Cancelled })
            {
                data.Add(type, status);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DecidedGates))]
    public void S4_2_Only_pending_gates_can_be_decided(GateType type, GateStatus status)
    {
        Assert.Equal(ErrorCodes.GateNotPending, GateRules.Approve(type, status, EscalationDecision.Accept).Error!.Code);
        Assert.Equal(ErrorCodes.GateNotPending, GateRules.Reject(type, status).Error!.Code);
    }

    [Fact]
    public void FR13_Rejecting_plan_approval_reruns_the_plan_generator()
    {
        var result = GateRules.Reject(GateType.PlanApproval, GateStatus.Pending);

        Assert.Equal(GateStatus.Rejected, result.Value.NewStatus);
        Assert.Equal(GateFollowUp.RerunAgent, result.Value.FollowUp);
        Assert.Equal(AgentKind.PlanGenerator, result.Value.RerunAgent);
    }

    [Fact]
    public void D16_Rejecting_spec_approval_reruns_the_spec_generator_not_idea_refinement()
    {
        var result = GateRules.Reject(GateType.SpecApproval, GateStatus.Pending);

        Assert.Equal(AgentKind.SpecGenerator, result.Value.RerunAgent);
    }

    [Fact]
    public void Rejecting_pre_release_records_the_decision_without_rerunning_an_agent()
    {
        var result = GateRules.Reject(GateType.PreRelease, GateStatus.Pending);

        Assert.Equal(new GateResolution(GateStatus.Rejected, GateFollowUp.None), result.Value);
    }

    [Fact]
    public void FR14_Escalation_continue_starts_another_review_cycle()
    {
        var result = GateRules.Approve(GateType.Escalation, GateStatus.Pending, EscalationDecision.Continue);

        Assert.Equal(GateFollowUp.StartReviewCycle, result.Value.FollowUp);
        Assert.Equal(EscalationDecision.Continue, result.Value.Decision);
        Assert.Equal(1, GateRules.ExtraCyclesPerContinue);
    }

    [Fact]
    public void FR14_Escalation_rollback_carries_the_target_version()
    {
        var result = GateRules.Approve(GateType.Escalation, GateStatus.Pending, EscalationDecision.Rollback, 4);

        Assert.Equal(GateFollowUp.Rollback, result.Value.FollowUp);
        Assert.Equal(4, result.Value.TargetVersion);
    }

    [Fact]
    public void FR14_Escalation_accept_marks_release_eligible()
    {
        var result = GateRules.Approve(GateType.Escalation, GateStatus.Pending, EscalationDecision.Accept);

        Assert.Equal(GateFollowUp.MarkReleaseEligible, result.Value.FollowUp);
    }

    [Fact]
    public void FR33_A_blocked_step_escalation_cannot_be_accepted() =>
        Assert.Equal(
            ErrorCodes.InvalidDecision,
            GateRules.Approve(GateType.Escalation, GateStatus.Pending, EscalationDecision.Accept, stepBlocked: true).Error!.Code);

    [Fact]
    public void FR33_A_blocked_step_escalation_can_be_continued_or_rolled_back()
    {
        Assert.Equal(GateFollowUp.StartReviewCycle, GateRules.Approve(GateType.Escalation, GateStatus.Pending, EscalationDecision.Continue, stepBlocked: true).Value.FollowUp);
        Assert.Equal(GateFollowUp.Rollback, GateRules.Approve(GateType.Escalation, GateStatus.Pending, EscalationDecision.Rollback, 3, stepBlocked: true).Value.FollowUp);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(EscalationDecision.Rollback, null)]
    [InlineData(EscalationDecision.Rollback, 0)]
    [InlineData(EscalationDecision.Continue, 3)]
    [InlineData(EscalationDecision.Accept, 3)]
    public void FR14_Invalid_escalation_decisions_are_rejected(EscalationDecision? decision, int? targetVersion) =>
        Assert.Equal(ErrorCodes.InvalidDecision, GateRules.Approve(GateType.Escalation, GateStatus.Pending, decision, targetVersion).Error!.Code);

    [Theory]
    [InlineData(GateType.SpecApproval)]
    [InlineData(GateType.PlanApproval)]
    [InlineData(GateType.PreRelease)]
    public void Non_escalation_gates_take_no_escalation_decision(GateType type)
    {
        Assert.Equal(ErrorCodes.InvalidDecision, GateRules.Approve(type, GateStatus.Pending, EscalationDecision.Accept).Error!.Code);
        Assert.Equal(ErrorCodes.InvalidDecision, GateRules.Approve(type, GateStatus.Pending, targetVersion: 2).Error!.Code);
    }

    [Fact]
    public void FR14_Escalation_gates_are_decided_not_rejected() =>
        Assert.Equal(ErrorCodes.InvalidDecision, GateRules.Reject(GateType.Escalation, GateStatus.Pending).Error!.Code);

    [Theory]
    [InlineData(GateType.SpecApproval, true)]
    [InlineData(GateType.PlanApproval, true)]
    [InlineData(GateType.PreRelease, true)]
    [InlineData(GateType.Escalation, false)]
    public void FR48_Approval_links_only_for_spec_plan_and_pre_release_gates(GateType type, bool supported) =>
        Assert.Equal(supported, GateRules.SupportsApprovalLinks(type));

    [Theory]
    [InlineData(GateStatus.Pending, GateStatus.Cancelled)]
    [InlineData(GateStatus.Approved, GateStatus.Approved)]
    [InlineData(GateStatus.Rejected, GateStatus.Rejected)]
    [InlineData(GateStatus.Cancelled, GateStatus.Cancelled)]
    public void FR44_Rollback_cancels_only_pending_gates(GateStatus status, GateStatus expected) =>
        Assert.Equal(expected, GateRules.Cancel(status));
}

public class GateTimersTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FR15_Gate_pending_73_hours_is_overdue_and_owner_was_notified_twice()
    {
        var notifications = 0;
        DateTimeOffset? remindedAt = null;
        var overdue = false;

        for (var hour = 1; hour <= 73; hour++)
        {
            var now = Created.AddHours(hour);
            var actions = GateTimers.Evaluate(GateStatus.Pending, Created, now, remindedAt, overdue);
            if (actions.SendReminder)
            {
                remindedAt = now;
                notifications++;
            }

            if (actions.MarkOverdue)
            {
                overdue = true;
                notifications++;
            }
        }

        Assert.True(overdue);
        Assert.Equal(2, notifications);
        Assert.Equal(Created.AddHours(24), remindedAt);
    }

    [Fact]
    public void FR15_Nothing_happens_before_24_hours() =>
        Assert.Equal(GateTimerActions.Nothing, GateTimers.Evaluate(GateStatus.Pending, Created, Created.AddHours(23.9), null, false));

    [Theory]
    [InlineData(GateStatus.Approved)]
    [InlineData(GateStatus.Rejected)]
    [InlineData(GateStatus.Cancelled)]
    public void FR15_Decided_gates_get_no_reminders(GateStatus status) =>
        Assert.Equal(GateTimerActions.Nothing, GateTimers.Evaluate(status, Created, Created.AddHours(100), null, false));
}
