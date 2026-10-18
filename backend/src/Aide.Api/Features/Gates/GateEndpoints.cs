using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Gates;

/// <summary>
/// FR-12–FR-14: approval gates (§6 <c>/api/projects/:id/gates</c>). Listing is for members; deciding is for Owners
/// (<see cref="GateService"/>).
/// </summary>
public static class GateEndpoints
{
    public static IEndpointRouteBuilder MapGateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}/gates")
            .WithTags("Gates")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/", ListGates)
            .WithName("ListGates")
            .WithSummary("The project's gates, newest first, optionally filtered by status and by releaseVersionId (FR-62: the gates of one release version; an id of another project's version gives an empty list) (members).")
            .Produces<IReadOnlyList<GateDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{gateId:guid}/approve", ApproveGate)
            .WithName("ApproveGate")
            .WithSummary("Approve a pending gate (Owner). Applies the gated transition; escalation gates need a decision (continue, rollback with targetCheckpoint, or accept). 409 GATE_NOT_PENDING, 400 INVALID_DECISION.")
            .Produces<GateDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION, INVALID_DECISION, or INVALID_TARGET_VERSION (escalation rollback)."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: only Owners decide gates."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project or gate."),
                (StatusCodes.Status409Conflict, "GATE_NOT_PENDING, PHASE_PRECONDITION_FAILED, WRONG_PHASE, ROLLBACK_BLOCKED (escalation rollback) or CONFLICT."),
                (StatusCodes.Status429TooManyRequests, "RATE_LIMITED: escalation rollback within a minute of the last rollback; see Retry-After."))
            .WithRetryAfterOn429();

        group.MapPost("/{gateId:guid}/reject", RejectGate)
            .WithName("RejectGate")
            .WithSummary("Reject a pending gate (Owner). The phase is unchanged and the agent that produced the gated artifact re-runs with the comment as feedback. Escalation gates can't be rejected (400 INVALID_DECISION). 409 GATE_NOT_PENDING.")
            .Produces<GateDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION or INVALID_DECISION."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: only Owners decide gates."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project or gate."),
                (StatusCodes.Status409Conflict, "GATE_NOT_PENDING or CONFLICT."));

        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<GateDto>>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> ListGates(
        Guid id,
        IProjectAccess access,
        GateReader gates,
        CancellationToken cancellationToken,
        [WireEnum(typeof(GateStatus))] string? status = null,
        Guid? releaseVersionId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var wanted = status is null ? (GateStatus?)null : EnumStrings.Parse<GateStatus>(status);
        return TypedResults.Ok(await gates.ListAsync(id, wanted, cancellationToken, releaseVersionId));
    }

    private static Task<Results<Ok<GateDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> ApproveGate(
        Guid id,
        Guid gateId,
        DecideGateRequest? request,
        HttpContext httpContext,
        IProjectAccess access,
        ICurrentUser currentUser,
        GateService service,
        GateReader gates,
        CancellationToken cancellationToken) =>
        DecideAsync(
            id,
            httpContext,
            access,
            gates,
            () => service.ApproveAsync(id, gateId, request ?? new DecideGateRequest(), currentUser.RequiredUserId, currentUser.Actor, cancellationToken),
            cancellationToken);

    private static Task<Results<Ok<GateDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> RejectGate(
        Guid id,
        Guid gateId,
        DecideGateRequest? request,
        HttpContext httpContext,
        IProjectAccess access,
        ICurrentUser currentUser,
        GateService service,
        GateReader gates,
        CancellationToken cancellationToken) =>
        DecideAsync(
            id,
            httpContext,
            access,
            gates,
            () => service.RejectAsync(id, gateId, request ?? new DecideGateRequest(), currentUser.RequiredUserId, currentUser.Actor, cancellationToken),
            cancellationToken);

    private static async Task<Results<Ok<GateDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> DecideAsync(
        Guid projectId,
        HttpContext httpContext,
        IProjectAccess access,
        GateReader gates,
        Func<Task<Result<ApprovalGate>>> decide,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(projectId, ProjectAction.DecideGate, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var decided = await decide();
        if (decided.IsFailure)
        {
            ResponseDocs.SetRetryAfter(httpContext, decided.Error!.RetryAfter);
            return ApiProblems.FromError(decided.Error!);
        }

        return TypedResults.Ok(await gates.ToDtoAsync(decided.Value, cancellationToken));
    }
}
