using Aide.Api.Jobs;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.Infrastructure.Persistence;

namespace Aide.Api.Features.Lifecycle;

/// <summary>
/// Writes an applied lifecycle move (a <see cref="PhaseTransition"/> from the domain state machine) into the current unit of
/// work: the project's phase, status and version, the <c>state_transitions</c> row, the <c>PHASE_CHANGED</c> event and,
/// for a forward move, the follow-up job of the entered phase (<see cref="JobTypes.OnEnter"/>). Nothing is saved here: the
/// caller's single <c>SaveChanges</c> commits it all in one transaction, guarded by the project's <c>xmin</c> (FR-11, §4.9).
/// </summary>
public sealed class LifecycleWriter(AideDbContext db, JobEnqueuer jobs)
{
    /// <summary>The <c>GATE_CREATED</c> payload property holding the advance reason (FR-12).</summary>
    public const string GateCreatedReasonKey = "reason";

    public async Task ApplyAsync(Project project, PhaseTransition transition, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(transition);

        project.CurrentPhase = transition.To;
        project.Status = transition.Status;
        project.ProjectVersion = transition.Version;
        project.UpdatedAt = now;
        if (transition.ClearsReviewState)
        {
            project.ReviewCycle = ReviewCycleRules.CycleAfterRollback;
            project.ExtraReviewCycles = 0;
        }

        db.StateTransitions.Add(new StateTransition
        {
            ProjectId = project.Id,
            FromPhase = transition.From,
            ToPhase = transition.To,
            Actor = actor,
            Reason = transition.Reason,
            ProjectVersion = transition.Version,
            CreatedAt = now,
        });
        db.AddEvent(
            EventTypes.PhaseChanged,
            project.Id,
            new
            {
                from = transition.From,
                to = transition.To,
                status = transition.Status,
                projectVersion = transition.Version,
                kind = transition.Kind,
                reason = transition.Reason,
            },
            actor,
            now);

        if (transition.Kind == TransitionKind.Advance && JobTypes.OnEnter(transition.To) is { } jobType)
        {
            await jobs.EnqueueAsync(
                jobType,
                new LifecycleJobPayload(project.Id, transition.Version, LifecycleJobPayload.PhaseEntered),
                project.Id,
                cancellationToken,
                now);
            if (transition.To == Phase.Release)
            {
                // FR-42: the release attempt is visible (in progress) from the moment the release job is queued.
                await Features.Release.ReleaseRecords.AddAttemptAsync(
                    db, project, Features.Release.ReleaseRecords.PhaseEntered, actor, now, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Adds a pending gate and its <c>GATE_CREATED</c> event; touches the project so its <c>xmin</c> guards the change.
    /// <paramref name="reason"/> is the reason given with the advance that needed the gate (FR-12); it is kept in the event
    /// payload so approving the gate can record it on the transition (<see cref="GateCreatedReasonKey"/>).
    /// </summary>
    /// <remarks>
    /// <paramref name="blockedStep"/> is the step whose fourth rejection raised an escalation gate (FR-33); the payload then
    /// records <c>stepId</c>, <c>stepNumber</c> and <c>escalationReason = step_blocked</c>, which the gate list reads back
    /// (no gate column is needed).
    /// </remarks>
    /// <remarks>
    /// <paramref name="escalationReason"/> is the §4.5 reason of an escalation raised by a review (FR-39); it is recorded as
    /// <c>escalationReason</c> in the payload.
    /// </remarks>
    public ApprovalGate AddGate(
        Project project,
        GateType type,
        Guid? artifactId,
        string actor,
        DateTimeOffset now,
        string? reason = null,
        ExecutionStep? blockedStep = null,
        EscalationReason? escalationReason = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var gate = new ApprovalGate
        {
            ProjectId = project.Id,
            Type = type,
            ArtifactId = artifactId,
            Status = GateStatus.Pending,
            CreatedAt = now,
        };
        db.ApprovalGates.Add(gate);
        project.UpdatedAt = now;
        object payload = blockedStep is null
            ? escalationReason is { } review
                ? new { gateId = gate.Id, type, artifactId, projectVersion = project.ProjectVersion, reason, escalationReason = review }
                : new { gateId = gate.Id, type, artifactId, projectVersion = project.ProjectVersion, reason }
            : new
            {
                gateId = gate.Id,
                type,
                artifactId,
                projectVersion = project.ProjectVersion,
                reason,
                escalationReason = EscalationReason.StepBlocked,
                stepId = blockedStep.Id,
                stepNumber = blockedStep.StepNumber,
            };
        db.AddEvent(EventTypes.GateCreated, project.Id, payload, actor, now);
        return gate;
    }
}
