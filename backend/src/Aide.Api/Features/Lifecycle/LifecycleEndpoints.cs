using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Gates;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Lifecycle;

/// <summary>
/// FR-11, FR-12: lifecycle moves requested by users (§6 <c>/api/projects/:id/phase/advance</c>). Phase changes go only
/// through the domain state machine (<see cref="LifecycleService"/>).
/// </summary>
public static class LifecycleEndpoints
{
    public static IEndpointRouteBuilder MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}/phase")
            .WithTags("Lifecycle")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/advance", AdvancePhase)
            .WithName("AdvancePhase")
            .WithSummary("Advance to the next phase (Contributor or Owner). Returns the new state, or the pending gate created when the approval mode requires one (phase unchanged). 409 PHASE_PRECONDITION_FAILED (with reason), GATE_PENDING, WRONG_PHASE or CONFLICT.")
            .Produces<AdvanceResultDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status403Forbidden, "FORBIDDEN: Viewers can't advance."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
                (StatusCodes.Status409Conflict, "PHASE_PRECONDITION_FAILED (details lists every unmet condition), GATE_PENDING, WRONG_PHASE (already in release) or CONFLICT."));

        return app;
    }

    private static async Task<Results<Ok<AdvanceResultDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> AdvancePhase(
        Guid id,
        AdvancePhaseRequest? request,
        IProjectAccess access,
        ICurrentUser currentUser,
        LifecycleService lifecycle,
        GateReader gates,
        AideDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.AdvancePhase, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var advanced = await lifecycle.AdvanceAsync(id, request?.Reason, currentUser.Actor, cancellationToken);
        if (advanced.IsFailure)
        {
            return ApiProblems.FromError(advanced.Error!);
        }

        var state = await ProjectEndpoints.LoadStateAsync(db, id, time, cancellationToken);
        if (state is null)
        {
            return ApiProblems.NotFound("The project was not found.");
        }

        var gate = advanced.Value.Gate is { } created ? await gates.ToDtoAsync(created, cancellationToken) : null;
        return TypedResults.Ok(new AdvanceResultDto(state, gate));
    }
}
