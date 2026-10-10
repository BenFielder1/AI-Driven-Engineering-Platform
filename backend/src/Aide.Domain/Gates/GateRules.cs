using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Lifecycle;

namespace Aide.Domain.Gates;

/// <summary>What the caller does after a gate decision.</summary>
public enum GateFollowUp
{
    /// <summary>Nothing beyond recording the decision (e.g. a rejected pre_release gate).</summary>
    None = 0,

    /// <summary>Apply the gated forward transition (spec_approval, plan_approval, pre_release approved).</summary>
    ApplyTransition = 1,

    /// <summary>Re-run <see cref="GateResolution.RerunAgent"/> with the comment as feedback.</summary>
    RerunAgent = 2,

    /// <summary>Escalation <c>continue</c>: allow one more cycle and re-enter execution.</summary>
    StartReviewCycle = 3,

    /// <summary>Escalation <c>rollback</c>: roll back to <see cref="GateResolution.TargetVersion"/>.</summary>
    Rollback = 4,

    /// <summary>Escalation <c>accept</c>: mark release-eligible (creates the pre_release gate).</summary>
    MarkReleaseEligible = 5,
}

/// <summary>A pending gate as the artifact-approval rules see it.</summary>
public sealed record PendingGateRef(Guid Id, GateType Type, Guid? ArtifactId);

/// <summary>The outcome of deciding a gate.</summary>
public sealed record GateResolution(
    GateStatus NewStatus,
    GateFollowUp FollowUp,
    AgentKind? RerunAgent = null,
    EscalationDecision? Decision = null,
    int? TargetVersion = null);

/// <summary>Gates by approval mode and gate decision rules (§4.2, FR-12–FR-14, D3, D16).</summary>
public static class GateRules
{
    /// <summary>An escalation <c>continue</c> raises the effective max-cycle limit by this much (FR-14).</summary>
    public const int ExtraCyclesPerContinue = 1;

    /// <summary>
    /// The gate required to enter <paramref name="entering"/>, or <c>null</c> when the transition applies directly.
    /// Escalations (§4.5) always create an <see cref="GateType.Escalation"/> gate, in every mode.
    /// </summary>
    public static GateType? RequiredGate(ApprovalMode mode, Phase entering) => (entering, mode) switch
    {
        (Phase.Planning, ApprovalMode.Strict or ApprovalMode.Advisory) => GateType.SpecApproval,
        (Phase.Execution, ApprovalMode.Strict) => GateType.PlanApproval,
        (Phase.Release, _) => GateType.PreRelease,
        _ => null,
    };

    /// <summary>
    /// FR-48: one-click approve/reject links exist for spec_approval, plan_approval and pre_release gates.
    /// Escalation decisions need a choice (and maybe a version), so their notifications link to the project page.
    /// </summary>
    public static bool SupportsApprovalLinks(GateType type) => type is GateType.SpecApproval or GateType.PlanApproval or GateType.PreRelease;

    /// <summary>The phase whose entry a gate guards; <c>null</c> for escalation gates, which guard no single entry.</summary>
    public static Phase? GuardedEntry(GateType type) => type switch
    {
        GateType.SpecApproval => Phase.Planning,
        GateType.PlanApproval => Phase.Execution,
        GateType.PreRelease => Phase.Release,
        _ => null,
    };

    /// <summary>The artifact a gate approves (the latest locked version is attached to the gate); <c>null</c> for pre_release and escalation.</summary>
    public static ArtifactType? GatedArtifact(GateType type) => type switch
    {
        GateType.SpecApproval => ArtifactType.TechnicalSpecification,
        GateType.PlanApproval => ArtifactType.ImplementationPlan,
        _ => null,
    };

    /// <summary>The agent re-run when a gate is rejected (D16: spec gate → spec generator).</summary>
    public static AgentKind? AgentToRerunOnReject(GateType type) => type switch
    {
        GateType.SpecApproval => AgentKind.SpecGenerator,
        GateType.PlanApproval => AgentKind.PlanGenerator,
        _ => null,
    };

    /// <summary>
    /// Approve a gate. Escalation gates need a <paramref name="decision"/>; <c>rollback</c> needs a
    /// <paramref name="targetVersion"/> (validated later by the rollback rules). An escalation raised by a blocked execution
    /// step (<paramref name="stepBlocked"/>, FR-33) can't be <c>accept</c>ed: there is no review to accept and the step would
    /// stay blocked, so only <c>continue</c> (retry the step) and <c>rollback</c> are valid (<c>INVALID_DECISION</c>).
    /// </summary>
    public static Result<GateResolution> Approve(
        GateType type, GateStatus status, EscalationDecision? decision = null, int? targetVersion = null, bool stepBlocked = false)
    {
        if (status != GateStatus.Pending)
        {
            return NotPending(status);
        }

        if (type != GateType.Escalation)
        {
            return decision is not null || targetVersion is not null
                ? InvalidDecision($"A {type.ToWire()} gate takes no escalation decision or target version.")
                : Result.Success(new GateResolution(GateStatus.Approved, GateFollowUp.ApplyTransition));
        }

        return decision switch
        {
            null => InvalidDecision("An escalation gate needs a decision: continue, rollback or accept."),
            EscalationDecision.Rollback when targetVersion is not { } v || v < 1 =>
                InvalidDecision("A rollback decision needs a target version."),
            EscalationDecision.Rollback =>
                Result.Success(new GateResolution(GateStatus.Approved, GateFollowUp.Rollback, Decision: decision, TargetVersion: targetVersion)),
            _ when targetVersion is not null => InvalidDecision("Only a rollback decision takes a target version."),
            EscalationDecision.Accept when stepBlocked =>
                InvalidDecision("A blocked step's escalation is decided with continue or rollback; there is no review result to accept."),
            EscalationDecision.Continue =>
                Result.Success(new GateResolution(GateStatus.Approved, GateFollowUp.StartReviewCycle, Decision: decision)),
            EscalationDecision.Accept =>
                Result.Success(new GateResolution(GateStatus.Approved, GateFollowUp.MarkReleaseEligible, Decision: decision)),
            _ => InvalidDecision("Unknown escalation decision."),
        };
    }

    /// <summary>Reject a gate. The phase is unchanged; spec/plan gates re-run their generator.</summary>
    public static Result<GateResolution> Reject(GateType type, GateStatus status)
    {
        if (status != GateStatus.Pending)
        {
            return NotPending(status);
        }

        if (type == GateType.Escalation)
        {
            return InvalidDecision("An escalation gate is decided with continue, rollback or accept, not rejected.");
        }

        var agent = AgentToRerunOnReject(type);
        return Result.Success(new GateResolution(
            GateStatus.Rejected,
            agent is null ? GateFollowUp.None : GateFollowUp.RerunAgent,
            agent));
    }

    /// <summary>
    /// FR-17/FR-19 with §4.2: the pending gates that approving a new artifact version makes stale, so they are cancelled
    /// (a gate decision is final, so a stale gate is never re-pointed). A gate holds the version that was locked when the
    /// advance was requested; it is cancelled when
    /// <list type="bullet">
    /// <item>the phase fell back (§4.4) — like a rollback, every pending gate guards a move that no longer applies;</item>
    /// <item>its artifact was invalidated by the cascade;</item>
    /// <item>it approves an older version of the type just approved (the Owner re-requests the advance for the new one).</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<Guid> StaleAfterArtifactApproval(
        Guid approvedArtifactId,
        ArtifactType approvedType,
        IReadOnlyCollection<Guid> invalidatedArtifactIds,
        bool phaseFellBack,
        IEnumerable<PendingGateRef> pendingGates)
    {
        ArgumentNullException.ThrowIfNull(invalidatedArtifactIds);
        ArgumentNullException.ThrowIfNull(pendingGates);
        return
        [
            .. pendingGates
                .Where(g => phaseFellBack
                    || (g.ArtifactId is { } id && invalidatedArtifactIds.Contains(id))
                    || (GatedArtifact(g.Type) == approvedType && g.ArtifactId != approvedArtifactId))
                .Select(g => g.Id),
        ];
    }

    /// <summary>Rollback cancels pending gates; decided gates are history and stay as they are.</summary>
    public static GateStatus Cancel(GateStatus status) => status == GateStatus.Pending ? GateStatus.Cancelled : status;

    private static Result<GateResolution> NotPending(GateStatus status) =>
        Result.Failure<GateResolution>(new DomainError(ErrorCodes.GateNotPending, $"The gate is {status.ToWire()}; only pending gates can be decided."));

    private static Result<GateResolution> InvalidDecision(string message) =>
        Result.Failure<GateResolution>(new DomainError(ErrorCodes.InvalidDecision, message));
}
