using Aide.Api.Features.Review;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
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

        // The latest report of the current review timeline (§4.8: nothing from before a rollback).
        var report = await LatestReportAsync(project, cancellationToken);

        // The review cycle runs the optimisation plan of that report (FR-40, FR-14): its draft, or a later version of it
        // (a revision or a human edit), once locked. An optimisation plan of an earlier cycle — already executed — never
        // counts, and only steps still to run do.
        var cyclePlanId = report is null
            ? null
            : locked
                .Where(a => a.Type == ArtifactType.OptimizationPlan && IsOfReport(report, a.Id, a.CreatedAt))
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefault();
        var planIds = new[] { currentPlanId, cyclePlanId }.OfType<Guid>().Distinct().ToList();
        var stepCounts = await db.ExecutionSteps.AsNoTracking()
            .Where(s => planIds.Contains(s.PlanArtifactId))
            .GroupBy(s => s.PlanArtifactId)
            .Select(g => new
            {
                PlanId = g.Key,
                Total = g.Count(),
                Completed = g.Count(s => s.Status == StepStatus.Completed),
                Remaining = g.Count(s => s.Status != StepStatus.Completed && s.Status != StepStatus.Invalidated),
            })
            .ToListAsync(cancellationToken);
        var current = stepCounts.FirstOrDefault(c => c.PlanId == currentPlanId);
        var optimization = stepCounts.FirstOrDefault(c => c.PlanId == cyclePlanId);

        // A project branch was recorded by an earlier preparation. Entering execution from planning prepares the working
        // copy first and replaces this with that outcome (LifecycleService, GateService); the review cycle relies on it.
        var workingCopyPrepared = await db.ProjectGitSettings.AsNoTracking()
            .AnyAsync(g => g.ProjectId == id && g.BranchName != null, cancellationToken);

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
            OptimizationPlanLocked = cyclePlanId is not null,
            OptimizationPlanStepCount = optimization?.Remaining ?? 0,
            EntryGateApproved = false,
            PendingGates = pending,
        };
    }

    /// <summary>
    /// Whether the optimisation plan <paramref name="artifact"/> belongs to the review cycle of the latest current-timeline
    /// report: it is that report's plan or a version created since (a revision or a human edit). <c>false</c> without a report.
    /// </summary>
    public async Task<bool> IsCyclePlanAsync(Project project, Artifact artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(artifact);
        var report = await LatestReportAsync(project, cancellationToken);
        return artifact.Type == ArtifactType.OptimizationPlan && report is not null && IsOfReport(report, artifact.Id, artifact.CreatedAt);
    }

    /// <summary>A draft optimisation plan of the latest current-timeline report's cycle is waiting for approval (FR-14 continue).</summary>
    public async Task<bool> CyclePlanDraftPendingAsync(Project project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (await LatestReportAsync(project, cancellationToken) is not { } report)
        {
            return false;
        }

        return await db.Artifacts.AsNoTracking().AnyAsync(
            a => a.ProjectId == project.Id && a.Type == ArtifactType.OptimizationPlan && a.Status == ArtifactStatus.Draft
                && (a.Id == report.OptimizationPlanArtifactId || a.CreatedAt >= report.CreatedAt),
            cancellationToken);
    }

    private Task<LatestReport?> LatestReportAsync(Project project, CancellationToken cancellationToken) =>
        ReviewTimeline.Current(db, project)
            .NewestFirst()
            .Select(r => new LatestReport(r.Decision, r.ProjectVersion, r.CreatedAt, r.OptimizationPlanArtifactId))
            .FirstOrDefaultAsync(cancellationToken);

    private static bool IsOfReport(LatestReport report, Guid artifactId, DateTimeOffset createdAt) =>
        artifactId == report.OptimizationPlanArtifactId || createdAt >= report.CreatedAt;

    private sealed record LatestReport(ReviewDecision Decision, int ProjectVersion, DateTimeOffset CreatedAt, Guid? OptimizationPlanArtifactId);
}
