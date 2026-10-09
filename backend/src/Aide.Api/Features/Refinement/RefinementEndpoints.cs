using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Refinement;

/// <summary>
/// FR-27: idea refinement rounds (§6 <c>/api/projects/:id/refinement</c>). Starting a round is for writers and only queues
/// the agent job; reading the state is for members.
/// </summary>
public static class RefinementEndpoints
{
    public static IEndpointRouteBuilder MapRefinementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}/refinement")
            .WithTags("Refinement")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/", GetRefinement)
            .WithName("GetRefinement")
            .WithSummary("The refinement state (members): the latest project request version, the latest round with its status, and the clarifying questions of the latest completed round with any answers already given.")
            .Produces<RefinementDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project."));

        group.MapPost("/run", StartRefinement)
            .WithName("StartRefinement")
            .WithSummary("Run a refinement round (Contributor or Owner): queues the Idea Refinement agent on the idea, the current draft and the answers given (questionId of a question from the latest round, or the question text). Returns 202 with the queued round; the new draft and questions arrive with the run. 409 WRONG_PHASE, BUDGET_EXCEEDED, REFINEMENT_IN_PROGRESS.")
            .Produces<RefinementRoundDto>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION: an answer is blank or too long, names an unknown questionId, has neither questionId nor question, or answers a question twice; more than 20 answers."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: Viewers can't run agents."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
                (StatusCodes.Status409Conflict, "WRONG_PHASE (not in refinement), BUDGET_EXCEEDED (token budget used up), REFINEMENT_IN_PROGRESS (a round is queued or running) or CONFLICT (a concurrent start)."));

        return app;
    }

    private static async Task<Results<Ok<RefinementDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> GetRefinement(
        Guid id,
        IProjectAccess access,
        RefinementService refinement,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        return TypedResults.Ok(await refinement.GetAsync(id, cancellationToken));
    }

    private static async Task<Results<Accepted<RefinementRoundDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> StartRefinement(
        Guid id,
        StartRefinementRequest? request,
        IProjectAccess access,
        ICurrentUser currentUser,
        RefinementService refinement,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.RunAgents, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var answers = request?.Answers?.Select(a => new RefinementAnswerInput(a.QuestionId, a.Question, a.Answer)).ToList();
        var started = await refinement.StartAsync(id, answers, currentUser.Actor, cancellationToken);
        if (started.IsFailure)
        {
            return started.Error!.Code == ErrorCodes.Validation ? ApiProblems.Validation(started.Error) : ApiProblems.FromError(started.Error);
        }

        return TypedResults.Accepted($"/api/projects/{id}/refinement", started.Value);
    }
}
