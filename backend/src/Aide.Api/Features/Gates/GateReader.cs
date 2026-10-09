using System.Text.Json;
using Aide.Api.Features.Activity;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Review;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Gates;

/// <summary>Reads gates as <see cref="GateDto"/>s: gated artifact, escalation reason, reviewer name and the overdue flag (FR-15).</summary>
public sealed class GateReader(AideDbContext db, TimeProvider time)
{
    public async Task<IReadOnlyList<GateDto>> ListAsync(Guid projectId, GateStatus? status, CancellationToken cancellationToken)
    {
        var query = db.ApprovalGates.AsNoTracking().Where(g => g.ProjectId == projectId);
        if (status is { } wanted)
        {
            query = query.Where(g => g.Status == wanted);
        }

        var gates = await query.OrderByDescending(g => g.CreatedAt).ThenByDescending(g => g.Id).ToListAsync(cancellationToken);
        return await ToDtosAsync(projectId, gates, cancellationToken);
    }

    public async Task<GateDto> ToDtoAsync(ApprovalGate gate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return (await ToDtosAsync(gate.ProjectId, [gate], cancellationToken))[0];
    }

    private async Task<IReadOnlyList<GateDto>> ToDtosAsync(Guid projectId, List<ApprovalGate> gates, CancellationToken cancellationToken)
    {
        if (gates.Count == 0)
        {
            return [];
        }

        var artifactIds = gates.Select(g => g.ArtifactId).OfType<Guid>().Distinct().ToList();
        var artifacts = artifactIds.Count == 0
            ? []
            : await db.Artifacts.AsNoTracking()
                .Where(a => artifactIds.Contains(a.Id))
                .Select(a => new { a.Id, a.Type, a.Version })
                .ToDictionaryAsync(a => a.Id, cancellationToken);

        // An escalation gate is raised by the latest escalating review before it (§4.5).
        var escalations = gates.Any(g => g.Type == GateType.Escalation)
            ? await db.ComplianceReports.AsNoTracking()
                .Where(r => r.ProjectId == projectId && r.Decision == ReviewDecision.EscalationRequired)
                .Select(r => new { r.CreatedAt, r.EscalationReason })
                .ToListAsync(cancellationToken)
            : [];

        // An escalation raised by a blocked step (FR-33) names the step in its GATE_CREATED payload.
        var blockedSteps = await BlockedStepsAsync(db, projectId, [.. gates.Where(g => g.Type == GateType.Escalation).Select(g => g.Id)], cancellationToken);

        var reviewers = await ActorResolver.NamesAsync(db, gates.Select(g => g.ReviewerId).OfType<Guid>(), cancellationToken);
        var now = time.GetUtcNow();

        return [.. gates.Select(g =>
        {
            var artifact = g.ArtifactId is { } aid && artifacts.TryGetValue(aid, out var a) ? a : null;
            var blocked = blockedSteps.TryGetValue(g.Id, out var stepRef) ? stepRef : null;
            var escalationReason = g.Type != GateType.Escalation
                ? null
                : blocked is not null
                    ? EscalationReason.StepBlocked
                    : escalations.Where(r => r.CreatedAt <= g.CreatedAt).OrderByDescending(r => r.CreatedAt).Select(r => r.EscalationReason).FirstOrDefault();
            return new GateDto(
                g.Id,
                g.Type,
                g.Status,
                g.ArtifactId,
                artifact?.Type,
                artifact?.Version,
                escalationReason,
                ReasonFor(g.Type, artifact?.Version, escalationReason),
                g.CreatedAt,
                g.DecidedAt,
                g.ReviewerId is { } reviewer
                    ? new ActorDto(reviewer, reviewers.GetValueOrDefault(reviewer, ActorResolver.DeletedUserName), false)
                    : null,
                g.Comment,
                g.Decision,
                g.TargetVersion,
                g.Status == GateStatus.Pending && (g.Overdue || now - g.CreatedAt >= GateTimers.OverdueAfter),
                blocked?.StepId,
                blocked?.StepNumber);
        })];
    }

    /// <summary>The step an escalation gate was raised for (FR-33), read from the gate's <c>GATE_CREATED</c> payload.</summary>
    internal sealed record BlockedStepRef(Guid StepId, int StepNumber);

    /// <summary>
    /// The blocked steps behind <paramref name="escalationIds"/>: the <c>GATE_CREATED</c> events of those gates that name a
    /// step, filtered in SQL by gate id, so only those payloads are read.
    /// </summary>
    internal static async Task<Dictionary<Guid, BlockedStepRef>> BlockedStepsAsync(
        AideDbContext db, Guid projectId, IReadOnlyCollection<Guid> escalationIds, CancellationToken cancellationToken)
    {
        if (escalationIds.Count == 0)
        {
            return [];
        }

        var ids = escalationIds.Select(id => id.ToString("D")).ToArray();
        var payloads = await db.Events
            .FromSql($"SELECT * FROM events WHERE project_id = {projectId} AND type = {EventTypes.GateCreated} AND payload ->> 'gateId' = ANY({ids}) AND payload ->> 'stepId' IS NOT NULL")
            .AsNoTracking()
            .Select(e => e.Payload)
            .ToListAsync(cancellationToken);
        var result = new Dictionary<Guid, BlockedStepRef>();
        foreach (var payload in payloads)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("gateId", out var gateId) && gateId.TryGetGuid(out var id) && escalationIds.Contains(id)
                    && root.TryGetProperty("stepId", out var stepId) && stepId.TryGetGuid(out var step)
                    && root.TryGetProperty("stepNumber", out var number) && number.TryGetInt32(out var n))
                {
                    result[id] = new BlockedStepRef(step, n);
                }
            }
            catch (JsonException)
            {
                // Not a payload we wrote; ignore.
            }
        }

        return result;
    }

    internal static string ReasonFor(GateType type, int? artifactVersion, EscalationReason? escalation)
    {
        var version = artifactVersion is { } v ? $" v{v}" : string.Empty;
        return type switch
        {
            GateType.SpecApproval => $"Approve the technical specification{version} to enter planning.",
            GateType.PlanApproval => $"Approve the implementation plan{version} to enter execution.",
            GateType.PreRelease => "Approve to enter release and merge and tag the project branch.",
            _ => escalation switch
            {
                EscalationReason.LowScore => $"The review score is below {ReviewDecisionRules.EscalationFloor.ToString(System.Globalization.CultureInfo.InvariantCulture)}. Continue, roll back or accept.",
                EscalationReason.SevereDrift => "The code drifts severely from the specification. Continue, roll back or accept.",
                EscalationReason.MaxCycles => "The maximum number of review cycles was reached. Continue, roll back or accept.",
                EscalationReason.NoImprovement => "The review score did not improve on the previous cycle. Continue, roll back or accept.",
                EscalationReason.NoOptimizationPlan => "The review requires another cycle but proposed no optimisation plan. Continue (run another review), roll back or accept.",
                EscalationReason.StepBlocked => "An execution step was rejected four times and is blocked. Continue (retry the step with fresh attempts) or roll back.",
                _ => $"A human decision is needed ({GateType.Escalation.ToWire()}). Continue, roll back or accept.",
            },
        };
    }
}
