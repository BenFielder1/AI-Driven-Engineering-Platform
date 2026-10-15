using Aide.Api.Features.Lifecycle;
using Aide.Api.Jobs;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Review;

/// <summary>FR-38: starting a review cycle by hand (<c>POST /review</c>).</summary>
public sealed partial class ReviewService(AideDbContext db, JobEnqueuer jobs, TimeProvider time, ILogger<ReviewService> logger)
{
    /// <summary>
    /// Queues a review job: only in the review phase (<c>WRONG_PHASE</c>) and when no review job is queued or running
    /// (<c>REVIEW_IN_PROGRESS</c>). The project row is touched so two concurrent requests can't both queue one (<c>CONFLICT</c>).
    /// </summary>
    public async Task<Result<ReviewStartedDto>> StartAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<ReviewStartedDto>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        if (project.CurrentPhase != Phase.Review)
        {
            return Result.Failure<ReviewStartedDto>(new DomainError(
                ErrorCodes.WrongPhase, $"Reviews run in the review phase; the project is in {project.CurrentPhase.ToWire()}."));
        }

        if (await ActiveAsync(db, projectId, cancellationToken))
        {
            return Result.Failure<ReviewStartedDto>(new DomainError(ErrorCodes.ReviewInProgress, "A review is already queued or running."));
        }

        var now = time.GetUtcNow();
        project.UpdatedAt = now;
        var job = await jobs.EnqueueAsync(
            JobTypes.Review,
            new LifecycleJobPayload(project.Id, project.Checkpoint, LifecycleJobPayload.UserRequested),
            project.Id,
            cancellationToken,
            now);
        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<ReviewStartedDto>(saved.Error!);
        }

        var cycle = ReviewCycleRules.NextCycle(project.ReviewCycle);
        LogQueued(logger, projectId, cycle);
        return Result.Success(new ReviewStartedDto(job.Id, cycle));
    }

    /// <summary>A review job of the project is queued or running (also counting one added in this unit of work).</summary>
    public static async Task<bool> ActiveAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.ChangeTracker.Entries<Job>().Any(e => e.State == EntityState.Added && e.Entity.Type == JobTypes.Review && e.Entity.ProjectId == projectId)
            || await db.Jobs.AnyAsync(
                j => j.ProjectId == projectId && j.Type == JobTypes.Review && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
                cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: review cycle {Cycle} queued by hand")]
    private static partial void LogQueued(ILogger logger, Guid projectId, int cycle);
}

/// <summary>
/// FR-41, FR-62: compliance reports. For the current release version, those of the current review timeline
/// (<see cref="ReviewTimeline"/>) — reports above the current checkpoint or from before the last rollback were undone by it
/// (§4.8) and are left out. For another release version (<c>releaseVersionId</c>), all of its reports (read-only history).
/// </summary>
public sealed class ReviewReader(AideDbContext db)
{
    public async Task<ComplianceReportDto?> LatestAsync(Guid projectId, CancellationToken cancellationToken, Guid? releaseVersionId = null)
    {
        var report = await LatestReportAsync(projectId, releaseVersionId, cancellationToken);
        return report is null ? null : ToDto(report);
    }

    public async Task<ComplianceTrendDto?> TrendAsync(Guid projectId, CancellationToken cancellationToken, Guid? releaseVersionId = null)
    {
        if (await ProjectAsync(projectId, cancellationToken) is not { } project)
        {
            return null;
        }

        var series = await Reports(projectId, project, releaseVersionId)
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Select(r => new ComplianceTrendPointDto(r.ReviewCycle, r.Checkpoint, r.Score, r.DriftLevel, r.Decision, r.CreatedAt, r.Threshold))
            .ToListAsync(cancellationToken);
        return new ComplianceTrendDto(project.ComplianceThreshold, series);
    }

    public async Task<ComplianceDriftDto?> DriftAsync(Guid projectId, CancellationToken cancellationToken, Guid? releaseVersionId = null)
    {
        var report = await LatestReportAsync(projectId, releaseVersionId, cancellationToken);
        return report is null
            ? null
            : new ComplianceDriftDto(report.DriftLevel, [.. report.DriftAreas.Select(d => new DriftAreaDto(d.Area, d.Severity, d.Description))], report.Checkpoint, report.ReviewCycle);
    }

    private async Task<ComplianceReport?> LatestReportAsync(Guid projectId, Guid? releaseVersionId, CancellationToken cancellationToken) =>
        await ProjectAsync(projectId, cancellationToken) is { } project
            ? await Reports(projectId, project, releaseVersionId).NewestFirst().FirstOrDefaultAsync(cancellationToken)
            : null;

    private IQueryable<ComplianceReport> Reports(Guid projectId, TimelineFacts project, Guid? releaseVersionId) =>
        releaseVersionId is { } requested && requested != project.CurrentReleaseVersionId
            ? ReviewTimeline.OfVersion(db, projectId, requested)
            : ReviewTimeline.Current(db, projectId, project.CurrentReleaseVersionId, project.Checkpoint, project.LastRollbackAt);

    private Task<TimelineFacts?> ProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new TimelineFacts(p.Checkpoint, p.CurrentReleaseVersionId, p.LastRollbackAt, p.ComplianceThreshold))
            .FirstOrDefaultAsync(cancellationToken);

    private sealed record TimelineFacts(int Checkpoint, Guid? CurrentReleaseVersionId, DateTimeOffset? LastRollbackAt, decimal ComplianceThreshold);

    public static ComplianceReportDto ToDto(ComplianceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new ComplianceReportDto(
            report.Id,
            report.ReleaseVersionId,
            report.Checkpoint,
            report.ReviewCycle,
            report.Score,
            report.Threshold,
            [.. report.CategoryScores.OrderBy(c => c.Category).Select(c => new CategoryScoreDto(
                c.Category,
                ComplianceScoring.Weights[c.Category],
                c.AgentScore,
                c.Penalty,
                c.Score,
                [.. c.Evidence.Select(e => new EvidenceDto(e.File, e.Reason))]))],
            [.. report.Violations.Select(v => new ViolationDto(v.Category, v.Severity, v.Source, v.Message, v.Path, v.Evidence))],
            report.MissingFeatures,
            report.DriftLevel,
            [.. report.DriftAreas.Select(d => new DriftAreaDto(d.Area, d.Severity, d.Description))],
            report.Decision,
            report.Decision == ReviewDecision.EscalationRequired ? report.EscalationReason : null,
            report.OptimizationPlanArtifactId,
            report.CreatedAt);
    }
}

public static class ReviewServiceCollectionExtensions
{
    /// <summary>FR-38–FR-41: the review job, its failure hook, the start service and the report reader.</summary>
    public static IServiceCollection AddReview(this IServiceCollection services)
    {
        services.AddScoped<ReviewService>();
        services.AddScoped<ReviewReader>();
        services.AddScoped<IJobEndedHook, ReviewJobEndedHook>();
        services.AddJobHandler<ReviewJobHandler>(JobTypes.Review);
        return services;
    }
}
