using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Lifecycle;

/// <summary>
/// Loads the facts the lifecycle state machine checks (§4.1 entry conditions, §4.2 gates) for a project. The state
/// machine stays pure; this is the only place those facts are read from the database.
/// </summary>
public sealed class LifecycleSnapshotLoader(AideDbContext db)
{
    public async Task<LifecycleSnapshot> LoadAsync(Project project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var id = project.Id;

        var locked = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == id && a.Status == ArtifactStatus.Locked)
            .Select(a => new { a.Id, a.Type, a.CreatedAt })
            .ToListAsync(cancellationToken);
        bool IsLocked(ArtifactType type) => locked.Any(a => a.Type == type);

        // The current plan: the most recently created locked plan (the implementation plan, or the optimisation plan of
        // a review cycle). Approving a new implementation plan invalidates the optimisation plan (§4.4).
        var currentPlanId = locked
            .Where(a => a.Type is ArtifactType.ImplementationPlan or ArtifactType.OptimizationPlan)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefault();
        var optimizationPlanId = locked
            .Where(a => a.Type == ArtifactType.OptimizationPlan)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefault();
        var planIds = new[] { currentPlanId, optimizationPlanId }.OfType<Guid>().Distinct().ToList();
        var stepCounts = await db.ExecutionSteps.AsNoTracking()
            .Where(s => planIds.Contains(s.PlanArtifactId))
            .GroupBy(s => s.PlanArtifactId)
            .Select(g => new { PlanId = g.Key, Total = g.Count(), Completed = g.Count(s => s.Status == StepStatus.Completed) })
            .ToListAsync(cancellationToken);
        var current = stepCounts.FirstOrDefault(c => c.PlanId == currentPlanId);
        var optimization = stepCounts.FirstOrDefault(c => c.PlanId == optimizationPlanId);

        // A project branch was recorded by an earlier preparation. Entering execution from planning prepares the working
        // copy first and replaces this with that outcome (LifecycleService, GateService); the review cycle relies on it.
        var workingCopyPrepared = await db.ProjectGitSettings.AsNoTracking()
            .AnyAsync(g => g.ProjectId == id && g.BranchName != null, cancellationToken);

        // Reports from versions above the current one were undone by a rollback (§4.8).
        var report = await db.ComplianceReports.AsNoTracking()
            .Where(r => r.ProjectId == id && r.ProjectVersion <= project.ProjectVersion)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => new { r.Decision, r.ProjectVersion, r.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        // The escalation outcome counts only for the escalation raised for that report (created after it).
        EscalationDecision? escalation = null;
        if (report is not null)
        {
            escalation = await db.ApprovalGates.AsNoTracking()
                .Where(g => g.ProjectId == id && g.Type == GateType.Escalation && g.Status == GateStatus.Approved
                    && g.Decision != null && g.CreatedAt >= report.CreatedAt)
                .OrderByDescending(g => g.DecidedAt)
                .Select(g => g.Decision)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var pending = await db.ApprovalGates.AsNoTracking()
            .Where(g => g.ProjectId == id && g.Status == GateStatus.Pending)
            .Select(g => g.Type)
            .ToListAsync(cancellationToken);

        return new LifecycleSnapshot
        {
            Mode = project.ApprovalMode,
            ProjectRequestLocked = IsLocked(ArtifactType.ProjectRequest),
            TechnicalSpecificationLocked = IsLocked(ArtifactType.TechnicalSpecification),
            ImplementationPlanLocked = IsLocked(ArtifactType.ImplementationPlan),
            CurrentPlanStepCount = current?.Total ?? 0,
            CurrentPlanCompletedStepCount = current?.Completed ?? 0,
            WorkingCopyPrepared = workingCopyPrepared,
            LatestReviewDecision = report?.Decision,
            LatestReviewProjectVersion = report?.ProjectVersion,
            EscalationOutcome = escalation,
            OptimizationPlanLocked = optimizationPlanId is not null,
            OptimizationPlanStepCount = optimization?.Total ?? 0,
            EntryGateApproved = false,
            PendingGates = pending,
        };
    }
}
