using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Gates;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>FR-359, §4.73: the pure rules of approval in the middle of a stage (states, timer, denial text, cap, reason).</summary>
public class ToolApprovalRulesTests
{
    private static readonly DateTimeOffset Raised = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(GateStatus.Pending, 0, ToolApprovalStep.Wait)]
    [InlineData(GateStatus.Pending, 23 * 60 + 59, ToolApprovalStep.Wait)]
    [InlineData(GateStatus.Pending, 24 * 60, ToolApprovalStep.TimeOut)]
    [InlineData(GateStatus.Pending, 30 * 60, ToolApprovalStep.TimeOut)]
    [InlineData(GateStatus.Approved, 30 * 60, ToolApprovalStep.Run)]
    [InlineData(GateStatus.Rejected, 1, ToolApprovalStep.Deny)]
    [InlineData(GateStatus.Cancelled, 1, ToolApprovalStep.Closed)]
    public void FR359_a_pending_call_follows_its_gate_and_its_24_hour_timer(GateStatus status, int minutesLater, ToolApprovalStep step) =>
        Assert.Equal(step, ToolApprovalRules.Next(ToolApprovalPhase.PendingApproval, gateRaised: true, status, Raised, Raised.AddMinutes(minutesLater)));

    [Fact]
    public void FR359_a_gate_that_is_gone_closes_the_call_so_it_never_runs() =>
        Assert.Equal(ToolApprovalStep.Closed, ToolApprovalRules.Next(ToolApprovalPhase.PendingApproval, gateRaised: true, null, null, Raised));

    [Fact]
    public void FR359_a_call_saved_without_a_gate_raises_one() =>
        Assert.Equal(ToolApprovalStep.RaiseGate, ToolApprovalRules.Next(ToolApprovalPhase.PendingApproval, gateRaised: false, null, null, Raised));

    [Theory]
    [InlineData(GateStatus.Approved)]
    [InlineData(GateStatus.Pending)]
    [InlineData(GateStatus.Rejected)]
    public void FR359_a_claimed_call_is_never_run_again_whatever_its_gate_says(GateStatus status) =>
        Assert.Equal(ToolApprovalStep.AlreadyClaimed, ToolApprovalRules.Next(ToolApprovalPhase.Approved, gateRaised: true, status, Raised, Raised.AddDays(3)));

    [Fact]
    public void FR359_the_timer_is_24_hours_from_the_raise() =>
        Assert.Equal(Raised.AddHours(24), ToolApprovalRules.DeadlineOf(Raised));

    [Fact]
    public void FR359_the_denial_text_names_the_user_and_the_comment()
    {
        Assert.Equal("denied by ben: not during the release", ToolApprovalRules.DenialText("ben", "  not during the release "));
        Assert.Equal("denied by ben: no comment", ToolApprovalRules.DenialText("ben", null));
        Assert.Equal("denied by system: no decision within 24 hours", ToolApprovalRules.TimeoutDenial);
    }

    [Fact]
    public void FR359_the_saved_state_cap_is_1_MB_of_UTF_8()
    {
        Assert.True(ToolApprovalRules.FitsCap(new string('a', ToolApprovalRules.MaxStateBytes)));
        Assert.False(ToolApprovalRules.FitsCap(new string('a', ToolApprovalRules.MaxStateBytes + 1)));

        // 'é' is two UTF-8 bytes: half the characters already fill the cap.
        Assert.False(ToolApprovalRules.FitsCap(new string('é', (ToolApprovalRules.MaxStateBytes / 2) + 1)));
        Assert.Equal(AgentRunInput.MaxBytes, ToolApprovalRules.MaxStateBytes);
    }

    [Theory]
    [InlineData("""{"pod":"api-0","reason":"  OOM loop  "}""", "I will restart it.", "OOM loop")]
    [InlineData("""{"pod":"api-0"}""", "  The pod is stuck; restarting.  ", "The pod is stuck; restarting.")]
    [InlineData("""{"pod":"api-0","reason":""}""", "Restarting.", "Restarting.")]
    [InlineData("not json", "", null)]
    [InlineData(null, null, null)]
    public void FR359_the_reason_is_the_reason_argument_else_the_text_with_the_call(string? arguments, string? text, string? reason) =>
        Assert.Equal(reason, ToolApprovalRules.ReasonOf(arguments, text));

    [Fact]
    public void FR359_a_long_reason_is_cut_to_its_limit() =>
        Assert.Equal(ToolApprovalRules.MaxReasonLength, ToolApprovalRules.ReasonOf(null, new string('r', 5000))!.Length);

    [Theory]
    [InlineData(EngineGates.ToolApproval, true)]
    [InlineData(EngineGates.Escalation, true)]
    [InlineData("plan_approval", false)]
    public void FR359_tool_approval_gates_are_raised_by_the_engine_never_as_entry_gates(string type, bool engineRaised) =>
        Assert.Equal(engineRaised, EngineGates.IsEngineRaised(type));
}
