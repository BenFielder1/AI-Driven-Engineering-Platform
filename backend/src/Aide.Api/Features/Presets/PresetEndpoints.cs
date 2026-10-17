using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Presets;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Presets;

/// <summary>
/// FR-74, §6: tech stack presets — <c>GET /api/presets</c>, <c>GET /api/presets/:id</c> (any signed-in user) and
/// <c>PATCH /api/projects/:id/preset</c> (Owner).
/// </summary>
public static class PresetEndpoints
{
    public static IEndpointRouteBuilder MapPresetEndpoints(this IEndpointRouteBuilder app)
    {
        var presets = app.MapGroup("/api/presets")
            .WithTags("Presets")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        presets.MapGet("/", ListPresets)
            .WithName("ListPresets")
            .WithSummary("The tech stack presets (FR-74), ordered by id: name, version, description, stack chips, deployment targets and content hash. For the wizard's Stack step and the admin presets list. No preset (general) is the absence of one.")
            .Produces<IReadOnlyList<PresetSummaryDto>>();

        presets.MapGet("/{presetId}", GetPreset)
            .WithName("GetPreset")
            .WithSummary("A preset in full (FR-74): specialist agents (owned paths, skills), extra spec sections with guidance, required environment variables, review checklist and deterministic rules, protected paths.")
            .Produces<PresetDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: no preset with that id."));

        app.MapPatch("/api/projects/{id:guid}/preset", UpdateProjectPreset)
            .WithTags("Projects")
            .RequireAuthorization()
            .WithName("UpdateProjectPreset")
            .WithSummary("Change the preset of the version in development (Owner, FR-74): pins the preset id, version and content hash on it and records PRESET_CHANGED. presetId null means no preset. Allowed until the version's first implementation plan is locked; a new version can pick another preset.")
            .Produces<ProjectDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "PRESET_NOT_FOUND: no preset with that id."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: only Owners change the preset."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
                (StatusCodes.Status409Conflict, "PRESET_LOCKED (the version's implementation plan is locked, or no version is in development) or CONFLICT (a concurrent edit)."));

        return app;
    }

    private static Ok<IReadOnlyList<PresetSummaryDto>> ListPresets(IPresetCatalog catalog) =>
        TypedResults.Ok<IReadOnlyList<PresetSummaryDto>>([.. catalog.All.Select(PresetMapping.ToSummary)]);

    private static Results<Ok<PresetDetailDto>, ProblemHttpResult> GetPreset(string presetId, IPresetCatalog catalog) =>
        catalog.Find(presetId) is { } preset
            ? TypedResults.Ok(PresetMapping.ToDetail(preset))
            : ApiProblems.NotFound($"The preset '{presetId}' was not found.");

    private static async Task<Results<Ok<ProjectDto>, ValidationProblem, ProblemHttpResult>> UpdateProjectPreset(
        Guid id,
        UpdateProjectPresetRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        PresetService presets,
        AideDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var grant = await access.CheckAsync(id, ProjectAction.ChangeSettings, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var changed = await presets.ChangeAsync(id, request.PresetId, currentUser.Actor, cancellationToken);
        if (changed.IsFailure)
        {
            return ApiProblems.FromError(changed.Error!);
        }

        return TypedResults.Ok(ProjectMapping.ToDto(changed.Value, grant.Value.ProjectRole, await presets.ForProjectAsync(id, cancellationToken), await ProjectEndpoints.DeploymentsEnabledAsync(db, id, cancellationToken)));
    }
}
