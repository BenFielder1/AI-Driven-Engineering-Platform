using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Access;
using Aide.Domain.Common;
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

        stack.MapPost("/detect", Detect)
            .WithName("DetectStack")
            .WithSummary("Import detection (FR-118): reads the repository's file list and manifests at ref (default: the default branch) through the Git provider (github_app with the caller's GitHub connection, github_pat with an optional token, gitlab with the caller's GitLab connection on instanceId (FR-141), local under Git:Local:ImportRoot) and returns the suggested choices with their layer paths and evidence, and the conflicting ones (left empty, every candidate shown). The VCS layer is fixed to the repository's host. Nothing is stored.")
            .Produces<StackDetectionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION: a malformed repo, ref or token, an instanceId or connectionId for a provider other than gitlab, or (local) a directory outside the import root or local import not enabled. GIT_HOST_NOT_ALLOWED (gitlab)."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: GitHub refused the request."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: no such repository or ref, or the caller can't see it."),
                (StatusCodes.Status409Conflict, "GIT_NOT_CONNECTED: github_app without a valid GitHub connection, or gitlab without a valid GitLab connection on the instance."),
                (StatusCodes.Status429TooManyRequests, "GITHUB_RATE_LIMITED (Retry-After: seconds until GitHub's rate-limit reset)"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR: GitHub failed or couldn't be reached, or the local repository couldn't be read."));

        var projectStack = app.MapGroup("/api/projects/{id:guid}/stack")
            .WithTags("Projects")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        projectStack.MapGet("/", GetProjectStack)
            .WithName("GetProjectStack")
            .WithSummary("Settings → Stack (FR-117, any member): the stack composition pinned on the current release version with its derived fields, the catalog version and hash it was pinned against (and the current ones), whether it is locked and why (plan_locked, not_in_development), the stack change against the version's base code (layersChanged, isStackChange), the retired v2 preset of a migrated version (legacyPreset, history: Preset <id>@<version> (retired, migrated)), and the VCS option fixed by the repository host (repoVcs).")
            .Produces<ProjectStackDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project."));

        projectStack.MapPut("/", UpdateProjectStack)
            .WithName("UpdateProjectStack")
            .WithSummary("Change the stack of the version in development (Owner, FR-117): validates the composition like POST /api/stack/validate plus R5 (the VCS layer is the repository's host; omitted, it is set from it), pins it with the current catalog version and hash (a migrated version keeps its retired preset as history), updates the project's stack packs and records STACK_CHANGED {from, to, layersChanged}. The pinned composition again changes nothing. Allowed until the version's first implementation plan is locked.")
            .Produces<ProjectStackDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "STACK_INCOMPATIBLE (violations extension), VCS_MISMATCH (vcs isn't the repository's host) or VALIDATION (an unknown id or a malformed or overlapping path)."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: only Owners change the stack."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
                (StatusCodes.Status409Conflict, "STACK_LOCKED (the version's implementation plan is locked, or no version is in development) or CONFLICT (a concurrent edit)."));

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
            return ApiProblems.Validation(new DomainError(ErrorCodes.Validation, "The stack choices are invalid.")
            {
                Details = [.. validation.InputErrors.Select(e => e.ToString())],
            });
        }

        return validation.Composition is { } composition
            ? TypedResults.Ok(StackMapping.ToDto(composition))
            : ApiProblems.StackIncompatible(validation.Violations);
    }

    private static async Task<Results<Ok<StackDetectionDto>, ValidationProblem, ProblemHttpResult>> Detect(
        DetectStackRequest request,
        ICurrentUser currentUser,
        StackDetectionService detection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var detected = await detection.DetectAsync(currentUser.RequiredUserId, request.Git, cancellationToken);
        if (detected.IsFailure)
        {
            return detected.Error!.Code == ErrorCodes.Validation
                ? ApiProblems.Validation(detected.Error!)
                : ApiProblems.FromError(detected.Error!);
        }

        return TypedResults.Ok(detected.Value);
    }

    private static async Task<Results<Ok<ProjectStackDto>, ProblemHttpResult>> GetProjectStack(
        Guid id,
        IProjectAccess access,
        ProjectStackService stacks,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var stack = await stacks.GetAsync(id, cancellationToken);
        return stack.IsFailure ? ApiProblems.FromError(stack.Error!) : TypedResults.Ok(stack.Value);
    }

    private static async Task<Results<Ok<ProjectStackDto>, ValidationProblem, ProblemHttpResult>> UpdateProjectStack(
        Guid id,
        StackCompositionRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        ProjectStackService stacks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var grant = await access.CheckAsync(id, ProjectAction.ChangeSettings, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var changed = await stacks.ChangeAsync(id, request, currentUser.Actor, cancellationToken);
        if (changed.IsFailure)
        {
            return changed.Error!.Code == ErrorCodes.Validation
                ? ApiProblems.Validation(changed.Error!)
                : ApiProblems.FromError(changed.Error!);
        }

        return TypedResults.Ok(changed.Value);
    }

    private static Ok<AdminStackCatalogDto> GetAdminCatalog(IStackCatalog catalog) => TypedResults.Ok(StackMapping.ToAdminDto(catalog));
}
