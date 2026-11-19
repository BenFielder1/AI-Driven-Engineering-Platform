using Aide.Domain.Common;
using Aide.Domain.Lifecycle;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Domain.Gates;

/// <summary>Gate decision rules (§4.2, FR-12–FR-14, D3, D16). Which gate guards which entry, in which approval modes, and
/// what it approves are the SDLC definition's (<see cref="SdlcStages"/>, FR-293).</summary>
public static class GateRules
{
    /// <summary>An escalation <c>continue</c> raises the effective max-cycle limit by this much (FR-14).</summary>
    public const int ExtraCyclesPerContinue = 1;

    /// <summary>The agent re-run when a gate is rejected (D16: spec gate → spec generator).</summary>
    public static string? AgentToRerunOnReject(string type) => type switch
    {
        SdlcGates.SpecApproval => SdlcAgents.SpecGenerator,
        SdlcGates.PlanApproval => SdlcAgents.PlanGenerator,
        _ => null,
    };

    /// <summary>
    /// Approve a gate. Escalation gates need a <paramref name="decision"/>; <c>rollback</c> needs a
    /// <paramref name="targetVersion"/> (validated later by the rollback rules). An escalation raised by a blocked execution
    /// step (<paramref name="stepBlocked"/>, FR-33) can't be <c>accept</c>ed: there is no review to accept and the step would
    /// stay blocked, so only <c>continue</c> (retry the step) and <c>rollback</c> are valid (<c>INVALID_DECISION</c>).
    /// </summary>
    public static Result<GateResolution> Approve(
        string type, GateStatus status, EscalationDecision? decision = null, int? targetVersion = null, bool stepBlocked = false)
    {
        if (status != GateStatus.Pending)
        {
            return NotPending(status);
        }

        if (type != EngineGates.Escalation)
        {
            return decision is not null || targetVersion is not null
                ? InvalidDecision($"A {type} gate takes no escalation decision or target version.")
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
    public static Result<GateResolution> Reject(string type, GateStatus status)
    {
        if (status != GateStatus.Pending)
        {
            return NotPending(status);
        }

        if (type == EngineGates.Escalation)
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
        PipelineDefinition definition,
        Guid approvedArtifactId,
        string approvedType,
        IReadOnlyCollection<Guid> invalidatedArtifactIds,
        bool phaseFellBack,
        IEnumerable<PendingGateRef> pendingGates)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(invalidatedArtifactIds);
        ArgumentNullException.ThrowIfNull(pendingGates);
        return
        [
            .. pendingGates
                .Where(g => phaseFellBack
                    || (g.ArtifactId is { } id && invalidatedArtifactIds.Contains(id))
                    || (SdlcStages.GatedArtifact(definition, g.Type) == approvedType && g.ArtifactId != approvedArtifactId))
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
