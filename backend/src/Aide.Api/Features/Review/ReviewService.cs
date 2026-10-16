using System.Text.Json;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Jobs;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
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
public sealed class ReviewReader(AideDbContext db, ExecutionRunReader runs)
{
    /// <summary>FR-56: every review cycle of the release version, newest first.</summary>
    public async Task<IReadOnlyList<ComplianceReportSummaryDto>> ListAsync(Guid projectId, CancellationToken cancellationToken, Guid? releaseVersionId = null)
    {
        if (await ProjectAsync(projectId, cancellationToken) is not { } project)
        {
            return [];
        }

        return await Reports(projectId, project, releaseVersionId)
            .NewestFirst()
            .Select(r => new ComplianceReportSummaryDto(r.ReviewCycle, r.Score, r.Threshold, r.DriftLevel, r.Decision, r.CreatedAt, r.Checkpoint, r.ReleaseVersionId, r.Id))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// FR-56: the full report of review cycle <paramref name="cycle"/>: report <paramref name="reportId"/> when given (it
    /// must be of that cycle), else the newest report of that cycle in the timeline (cycles restart after a rollback),
    /// with its optimisation plan — the version that ran when it produced a review-fix run, else the draft the review
    /// wrote — and that run's id; <c>null</c> when there is no report for the cycle.
    /// </summary>
    public async Task<ComplianceReportDetailDto?> CycleAsync(
        Guid projectId, int cycle, CancellationToken cancellationToken, Guid? releaseVersionId = null, Guid? reportId = null)
    {
        if (await ProjectAsync(projectId, cancellationToken) is not { } project)
        {
            return null;
        }

        var reports = Reports(projectId, project, releaseVersionId).Where(r => r.ReviewCycle == cycle);
        if (reportId is { } wanted)
        {
            reports = reports.Where(r => r.Id == wanted);
        }

        var report = await reports
            .NewestFirst()
            .FirstOrDefaultAsync(cancellationToken);
        if (report is null)
        {
            return null;
        }

        var run = await runs.RunOfReportAsync(projectId, report.ReleaseVersionId, report.Id, cancellationToken);
        var plan = run is not null
            ? new ReportPlanDto(run.Id, run.Version, run.Status)
            : report.OptimizationPlanArtifactId is { } draftId
                ? await db.Artifacts.AsNoTracking()
                    .Where(a => a.Id == draftId)
                    .Select(a => new ReportPlanDto(a.Id, a.Version, a.Status))
                    .FirstOrDefaultAsync(cancellationToken)
                : null;
        var summary = ToDto(report);
        return new ComplianceReportDetailDto(
            summary.Id,
            summary.ReleaseVersionId,
            summary.Checkpoint,
            summary.Cycle,
            summary.OverallScore,
            summary.Threshold,
            summary.CategoryScores,
            summary.Violations,
            [.. summary.Violations.Where(v => v.Source == FindingSource.Deterministic)],
            summary.MissingFeatures,
            summary.DriftLevel,
            summary.DriftAreas,
            summary.Decision,
            summary.DecisionReason,
            ReadRegressions(report.Regressions),
            ReadExternalChecks(report.ExternalChecks),
            plan,
            run?.Id,
            summary.CreatedAt);
    }

    /// <summary>§4.13 regressions from the report's JSON array; unreadable JSON or entries read as none.</summary>
    public static IReadOnlyList<RegressionDto> ReadRegressions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return [.. (AideJson.Deserialize<List<RegressionDto?>>(json)).OfType<RegressionDto>().Where(r => !string.IsNullOrEmpty(r.RequirementId))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The report's external checks (schemaless JSON), or <c>null</c> when none were collected or they can't be read.</summary>
    public static JsonElement? ReadExternalChecks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Null ? null : doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
