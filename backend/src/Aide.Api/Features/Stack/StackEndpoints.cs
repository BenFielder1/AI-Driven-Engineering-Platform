using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Stack;
using Aide.Infrastructure.Stack;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Stack;

/// <summary>
/// FR-113–FR-116, §6: the stack catalog and the compatibility engine — <c>GET /api/stack/catalog</c>,
/// <c>POST /api/stack/options</c>, <c>POST /api/stack/validate</c> (any signed-in user) and <c>GET /api/admin/stack</c>
/// (Admins). The rules are evaluated by <see cref="StackCompatibility"/>; these endpoints only map.
/// </summary>
public static class StackEndpoints
{
    public static IEndpointRouteBuilder MapStackEndpoints(this IEndpointRouteBuilder app)
    {
        var stack = app.MapGroup("/api/stack")
            .WithTags("Stack")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        stack.MapGet("/catalog", GetCatalog)
            .WithName("GetStackCatalog")
            .WithSummary("The stack catalog (FR-113): layers, choices in evaluation order with their options (chips, description, pack, pack version, stack-packs source, modes), the compatibility rule statements and the recommended combinations, plus the catalog version and content hash.")
            .Produces<StackCatalogDto>();

        stack.MapPost("/options", GetOptions)
            .WithName("GetStackOptions")
            .WithSummary("Option states for a partial composition (FR-114, FR-115, §4.17): every choice with each option's state (allowed, caveat, forbidden, or invalidated for a selection that earlier choices now forbid), the reasons (rule id and text), whether a recommended combination includes it, and the default to preselect.")
            .Produces<StackOptionsDto>()
            .ProducesValidationProblem()
            .WithResponseDescriptions((StatusCodes.Status400BadRequest, "VALIDATION: an unknown pack or option id, or a malformed layer path."));

        stack.MapPost("/validate", Validate)
            .WithName("ValidateStack")
            .WithSummary("Validate a composition (FR-116): the normalised composition with its derived fields (Next.js mode, specialists, origin model options, spec sections, env names, gates) and caveats. Left-out layers are None; the local dev loop and origin model take their defaults; vcs is required.")
            .Produces<StackCompositionDto>()
            .ProducesValidationProblem()
            .WithResponseDescriptions((StatusCodes.Status400BadRequest, "STACK_INCOMPATIBLE: the composition breaks a compatibility rule (the violations extension lists {layer, option, rule, reason}); VALIDATION: an unknown id, a missing vcs or a malformed path."));

        app.MapGet("/api/admin/stack", GetAdminCatalog)
            .WithTags("Admin")
            .RequireAuthorization(AuthConstants.AdminPolicy)
            .WithName("GetAdminStackCatalog")
            .WithSummary("Admin → Stack catalog (§6): the catalog version, content hash and load status, every load error, and each pack with its stack-packs source, version, content hash and load status.")
            .Produces<AdminStackCatalogDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static Ok<StackCatalogDto> GetCatalog(IStackCatalog catalog) => TypedResults.Ok(StackMapping.ToDto(catalog.Current));

    private static Results<Ok<StackOptionsDto>, ValidationProblem> GetOptions(StackCompositionRequest request, IStackCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = catalog.Current;
        var evaluation = StackCompatibility.Evaluate(StackMapping.ToSelection(request), current);
        return evaluation.IsFailure
            ? ApiProblems.Validation(evaluation.Error!)
            : TypedResults.Ok(StackMapping.ToDto(current, evaluation.Value));
    }

    private static Results<Ok<StackCompositionDto>, ValidationProblem, ProblemHttpResult> Validate(StackCompositionRequest request, IStackCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = StackCompatibility.Validate(StackMapping.ToSelection(request), catalog.Current);
        if (validation.InputErrors.Count > 0)
        {
            return ApiProblems.Validation(new Domain.Common.DomainError(Domain.Common.ErrorCodes.Validation, "The stack choices are invalid.")
            {
                Details = [.. validation.InputErrors.Select(e => e.ToString())],
            });
        }

        return validation.Composition is { } composition
            ? TypedResults.Ok(StackMapping.ToDto(composition))
            : ApiProblems.StackIncompatible(validation.Violations);
    }

    private static Ok<AdminStackCatalogDto> GetAdminCatalog(IStackCatalog catalog) => TypedResults.Ok(StackMapping.ToAdminDto(catalog));
}
