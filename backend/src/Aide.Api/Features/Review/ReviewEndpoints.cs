using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Access;
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

        return app;
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
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        return await reader.LatestAsync(id, cancellationToken) is { } report
            ? TypedResults.Ok(report)
            : ApiProblems.NotFound("No review has completed yet.");
    }

    private static async Task<Results<Ok<ComplianceTrendDto>, ProblemHttpResult>> GetComplianceTrend(
        Guid id,
        IProjectAccess access,
        ReviewReader reader,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        return await reader.TrendAsync(id, cancellationToken) is { } trend
            ? TypedResults.Ok(trend)
            : ApiProblems.NotFound("The project was not found.");
    }

    private static async Task<Results<Ok<ComplianceDriftDto>, ProblemHttpResult>> GetComplianceDrift(
        Guid id,
        IProjectAccess access,
        ReviewReader reader,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        return await reader.DriftAsync(id, cancellationToken) is { } drift
            ? TypedResults.Ok(drift)
            : ApiProblems.NotFound("No review has completed yet.");
    }
}
