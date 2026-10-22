using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Versions;
using Aide.Domain.Access;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Review;

/// <summary>FR-38–FR-41: review cycles and compliance reports (§6 <c>/api/projects/:id/review</c> and <c>/compliance/*</c>).</summary>
public static class ReviewEndpoints
{
    private const string VersionNote =
        "Only reports of the current review timeline count: at or below the current project version and created after the last rollback (§4.8), so a rollback hides every earlier report, even once the project is back at that version.";

    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}")
            .WithTags("Review")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/review", StartReview)
            .WithName("StartReview")
            .WithSummary("Run a review cycle now (Contributor or Owner, FR-38): queues the review job (deterministic checks plus the Code Review agent), which records a compliance report, applies the §4.5 decision and, on review_required, drafts the optimisation plan. Only in the review phase. Returns 202 with the job id and the cycle the review will record; progress arrives as REVIEW_STARTED and REVIEW_COMPLETED on the live channel.")
            .Produces<ReviewStartedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status202Accepted, "The review job was queued."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: Viewers can't start a review."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
                (StatusCodes.Status409Conflict, "WRONG_PHASE (the project isn't in the review phase), REVIEW_IN_PROGRESS (a review job is already queued or running) or CONFLICT (a concurrent change)."));

        group.MapGet("/compliance/latest", GetLatestCompliance)
            .WithName("GetLatestCompliance")
            .WithSummary("The latest compliance report (members, FR-38, FR-41): overall score vs threshold, category breakdown with weights, agent scores, penalties and evidence, violations, missing features, drift level and areas, the §4.5 decision and its reason, and the optimisation plan it produced. " + VersionNote)
            .Produces<ComplianceReportDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project, or no review has completed yet."));

        group.MapGet("/compliance/trend", GetComplianceTrend)
            .WithName("GetComplianceTrend")
            .WithSummary("The compliance score across review cycles, oldest first, with the project's threshold (members, FR-41). The series is empty before the first review. " + VersionNote)
            .Produces<ComplianceTrendDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project."));

        group.MapGet("/compliance/drift", GetComplianceDrift)
            .WithName("GetComplianceDrift")
            .WithSummary("The drift level and areas of the latest compliance report (members, FR-41, §4.7). " + VersionNote)
            .Produces<ComplianceDriftDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project, or no review has completed yet."));

        group.MapGet("/compliance/reports", ListComplianceReports)
            .WithName("ListComplianceReports")
            .WithSummary("Every review cycle of a release version (default: the version in development, else the latest released), newest first (members, FR-56): cycle, score, threshold, drift, decision and date. " + VersionNote)
            .Produces<IReadOnlyList<ComplianceReportSummaryDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project, or the release version in this project."));

        group.MapGet("/compliance/reports/{cycle:int}", GetComplianceReport)
            .WithName("GetComplianceReport")
            .WithSummary("The full compliance report of one review cycle of a release version (default: the version in development, else the latest released), members (FR-56): category breakdown, violations with evidence, deterministic findings, missing features, drift areas, decision and escalation reason, regressions, external checks, its optimisation plan and the execution run that plan produced. Cycle numbers restart after a rollback: reportId selects one exact report (it must be of that cycle), otherwise the newest report of the cycle is returned. " + VersionNote)
            .Produces<ComplianceReportDetailDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION: cycle below 1."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project, the release version, or no report for that cycle (or reportId is not a report of that cycle)."));

        return app;
    }

    /// <summary>FR-56: members; the cycles of the release version's review timeline, newest first.</summary>
    private static async Task<Results<Ok<IReadOnlyList<ComplianceReportSummaryDto>>, ProblemHttpResult>> ListComplianceReports(
        Guid id,
        IProjectAccess access,
        ReviewReader reader,
        AideDbContext db,
        CancellationToken cancellationToken,
        Guid? releaseVersionId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var scope = await ReleaseVersionScope.ResolveAsync(db, id, releaseVersionId, cancellationToken);
        return scope.IsFailure ? ApiProblems.FromError(scope.Error!) : TypedResults.Ok(await reader.ListAsync(id, cancellationToken, scope.Value));
    }

    /// <summary>FR-56: members; one cycle's full report, 404 when the release version has no report for it.</summary>
    private static async Task<Results<Ok<ComplianceReportDetailDto>, ValidationProblem, ProblemHttpResult>> GetComplianceReport(
        Guid id,
        [Range(1, int.MaxValue)] int cycle,
        IProjectAccess access,
        ReviewReader reader,
        AideDbContext db,
        CancellationToken cancellationToken,
        Guid? releaseVersionId = null,
        Guid? reportId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var scope = await ReleaseVersionScope.ResolveAsync(db, id, releaseVersionId, cancellationToken);
        if (scope.IsFailure)
        {
            return ApiProblems.FromError(scope.Error!);
        }

        return await reader.CycleAsync(id, cycle, cancellationToken, scope.Value, reportId) is { } report
            ? TypedResults.Ok(report)
            : ApiProblems.NotFound($"There is no report for review cycle {cycle}.");
    }

    private static async Task<Results<Accepted<ReviewStartedDto>, ProblemHttpResult>> StartReview(
        Guid id,
        IProjectAccess access,
        ReviewService reviews,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.TriggerReview, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var started = await reviews.StartAsync(id, cancellationToken);
        return started.IsSuccess
            ? TypedResults.Accepted($"/api/projects/{id}/compliance/latest", started.Value)
            : ApiProblems.FromError(started.Error!);
    }

    private static async Task<Results<Ok<ComplianceReportDto>, ProblemHttpResult>> GetLatestCompliance(
        Guid id,
        IProjectAccess access,
        ReviewReader reader,
        AideDbContext db,
        CancellationToken cancellationToken,
        Guid? releaseVersionId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var scope = await ReleaseVersionScope.ResolveAsync(db, id, releaseVersionId, cancellationToken);
        if (scope.IsFailure)
        {
            return ApiProblems.FromError(scope.Error!);
        }

        return await reader.LatestAsync(id, cancellationToken, scope.Value) is { } report
            ? TypedResults.Ok(report)
            : ApiProblems.NotFound("No review has completed yet.");
    }

    private static async Task<Results<Ok<ComplianceTrendDto>, ProblemHttpResult>> GetComplianceTrend(
        Guid id,
        IProjectAccess access,
        ReviewReader reader,
        AideDbContext db,
        CancellationToken cancellationToken,
        Guid? releaseVersionId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var scope = await ReleaseVersionScope.ResolveAsync(db, id, releaseVersionId, cancellationToken);
        if (scope.IsFailure)
        {
            return ApiProblems.FromError(scope.Error!);
        }

        return await reader.TrendAsync(id, cancellationToken, scope.Value) is { } trend
            ? TypedResults.Ok(trend)
            : ApiProblems.NotFound("The project was not found.");
    }

    private static async Task<Results<Ok<ComplianceDriftDto>, ProblemHttpResult>> GetComplianceDrift(
        Guid id,
        IProjectAccess access,
        ReviewReader reader,
        AideDbContext db,
        CancellationToken cancellationToken,
        Guid? releaseVersionId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var scope = await ReleaseVersionScope.ResolveAsync(db, id, releaseVersionId, cancellationToken);
        if (scope.IsFailure)
        {
            return ApiProblems.FromError(scope.Error!);
        }

        return await reader.DriftAsync(id, cancellationToken, scope.Value) is { } drift
            ? TypedResults.Ok(drift)
            : ApiProblems.NotFound("No review has completed yet.");
    }
}
