using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Artifacts;

/// <summary>
/// FR-16–FR-21: versioned artifacts (§6 <c>/api/projects/:id/artifacts</c>). Reads are for members and re-check the
/// content hash (FR-20); creating a draft is for writers (FR-18); approving and rejecting are for Owners (FR-17).
/// The <c>type</c> route value is the wire name (<c>project_request</c>, <c>technical_specification</c>,
/// <c>implementation_plan</c>, <c>optimization_plan</c>).
/// </summary>
public static class ArtifactEndpoints
{
    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}/artifacts")
            .WithTags("Artifacts")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/", ListLatestArtifacts)
            .WithName("ListLatestArtifacts")
            .WithSummary("The latest version of each artifact type that exists, without content (members).")
            .Produces<IReadOnlyList<ArtifactSummaryDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project."));

        group.MapGet("/{type}/latest", GetLatestArtifact)
            .WithName("GetLatestArtifact")
            .WithSummary("The highest version of an artifact type, with content and integrity (members).")
            .Produces<ArtifactDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION: unknown artifact type."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project, or no version of this type exists."));

        group.MapGet("/{type}/history", GetArtifactHistory)
            .WithName("GetArtifactHistory")
            .WithSummary("Every version of an artifact type, newest first, without content (members).")
            .Produces<IReadOnlyList<ArtifactSummaryDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION: unknown artifact type."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."));

        group.MapGet("/{type}/versions/{version:int}", GetArtifactVersion)
            .WithName("GetArtifactVersion")
            .WithSummary("One version of an artifact type, with content and integrity (members).")
            .Produces<ArtifactDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION: unknown artifact type or version below 1."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project or version."));

        group.MapGet("/{type}/diff", GetArtifactDiff)
            .WithName("GetArtifactDiff")
            .WithSummary("Line diff between two versions of an artifact type: unified text plus a structured line list for side-by-side rendering (members).")
            .Produces<ArtifactDiffDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION: unknown artifact type, from/to missing or below 1, or the versions have more than 100,000 lines together."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project, or either version."));

        group.MapPost("/{type}", CreateArtifactDraft)
            .WithName("CreateArtifactDraft")
            .WithSummary("Save edited Markdown as a new draft version (Contributor or Owner): version = latest + 1, parent = latest; an open draft of the type becomes superseded. With baseVersion, 409 CONFLICT unless it is still the latest version. 409 WRONG_PHASE.")
            .Produces<ArtifactDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION: unknown artifact type, or empty or too long content."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: Viewers can't create drafts."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
                (StatusCodes.Status409Conflict, "WRONG_PHASE: the type can't be edited in the current phase; CONFLICT: baseVersion is no longer the latest version (details: latestVersion), or a concurrent save."));

        group.MapPost("/{artifactId:guid}/approve", ApproveArtifact)
            .WithName("ApproveArtifact")
            .WithSummary("Approve a draft (Owner): records approver and time, sets locked and applies the invalidation cascade (§4.4). Approving an already locked artifact is a no-op. 409 ARTIFACT_NOT_DRAFT.")
            .Produces<ArtifactDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: only Owners approve artifacts."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project or artifact."),
                (StatusCodes.Status409Conflict, "ARTIFACT_NOT_DRAFT (rejected, superseded, invalidated or corrupted), ARTIFACT_LOCKED or CONFLICT."));

        group.MapPost("/{artifactId:guid}/reject", RejectArtifact)
            .WithName("RejectArtifact")
            .WithSummary("Reject a draft (Owner): records the comment and re-runs the producing agent with it (not for human-edited drafts). 409 ARTIFACT_NOT_DRAFT, ARTIFACT_LOCKED.")
            .Produces<ArtifactDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION: comment too long."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: only Owners reject artifacts."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project or artifact."),
                (StatusCodes.Status409Conflict, "ARTIFACT_NOT_DRAFT, ARTIFACT_LOCKED or CONFLICT."));

        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<ArtifactSummaryDto>>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> ListLatestArtifacts(
        Guid id,
        IProjectAccess access,
        ArtifactReader reader,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        return TypedResults.Ok(await reader.ListLatestAsync(id, cancellationToken));
    }

    private static async Task<Results<Ok<ArtifactDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> GetLatestArtifact(
        Guid id,
        [WireEnum(typeof(ArtifactType))] string type,
        IProjectAccess access,
        ArtifactReader reader,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var artifactType = EnumStrings.Parse<ArtifactType>(type);
        return await reader.LatestAsync(id, artifactType, cancellationToken) is { } artifact
            ? TypedResults.Ok(artifact)
            : ApiProblems.NotFound($"No version of the {type} exists yet.");
    }

    private static async Task<Results<Ok<IReadOnlyList<ArtifactSummaryDto>>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> GetArtifactHistory(
        Guid id,
        [WireEnum(typeof(ArtifactType))] string type,
        IProjectAccess access,
        ArtifactReader reader,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        return TypedResults.Ok(await reader.HistoryAsync(id, EnumStrings.Parse<ArtifactType>(type), cancellationToken));
    }

    private static async Task<Results<Ok<ArtifactDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> GetArtifactVersion(
        Guid id,
        [WireEnum(typeof(ArtifactType))] string type,
        [Range(1, int.MaxValue)] int version,
        IProjectAccess access,
        ArtifactReader reader,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        return await reader.VersionAsync(id, EnumStrings.Parse<ArtifactType>(type), version, cancellationToken) is { } artifact
            ? TypedResults.Ok(artifact)
            : ApiProblems.NotFound($"Version {version} of the {type} was not found.");
    }

    private static async Task<Results<Ok<ArtifactDiffDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> GetArtifactDiff(
        Guid id,
        [WireEnum(typeof(ArtifactType))] string type,
        [Range(1, int.MaxValue)] int from,
        [Range(1, int.MaxValue)] int to,
        IProjectAccess access,
        ArtifactReader reader,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var diff = await reader.DiffAsync(id, EnumStrings.Parse<ArtifactType>(type), from, to, cancellationToken);
        if (diff.IsSuccess)
        {
            return TypedResults.Ok(diff.Value);
        }

        return diff.Error!.Code == ErrorCodes.Validation ? ApiProblems.Validation(diff.Error) : ApiProblems.FromError(diff.Error);
    }

    private static async Task<Results<Created<ArtifactDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> CreateArtifactDraft(
        Guid id,
        [WireEnum(typeof(ArtifactType))] string type,
        CreateArtifactDraftRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        ArtifactService service,
        ArtifactReader reader,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.CreateDraft, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["content"] = ["The content can't be empty."] });
        }

        var created = await service.CreateDraftAsync(
            id, EnumStrings.Parse<ArtifactType>(type), request.Content, request.BaseVersion, currentUser.RequiredUserId, currentUser.Actor, cancellationToken);
        if (created.IsFailure)
        {
            return ApiProblems.FromError(created.Error!);
        }

        var dto = await reader.ToDtoAsync(created.Value, cancellationToken);
        return TypedResults.Created($"/api/projects/{id}/artifacts/{type}/versions/{dto.Version}", dto);
    }

    private static async Task<Results<Ok<ArtifactDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> ApproveArtifact(
        Guid id,
        Guid artifactId,
        DecideArtifactRequest? request,
        IProjectAccess access,
        ICurrentUser currentUser,
        ArtifactService service,
        ArtifactReader reader,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.DecideArtifact, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var approved = await service.ApproveAsync(id, artifactId, request?.Comment, currentUser.RequiredUserId, currentUser.Actor, cancellationToken);
        return approved.IsSuccess
            ? TypedResults.Ok(await reader.ToDtoAsync(approved.Value, cancellationToken))
            : ApiProblems.FromError(approved.Error!);
    }

    private static async Task<Results<Ok<ArtifactDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> RejectArtifact(
        Guid id,
        Guid artifactId,
        DecideArtifactRequest? request,
        IProjectAccess access,
        ICurrentUser currentUser,
        ArtifactService service,
        ArtifactReader reader,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.DecideArtifact, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var rejected = await service.RejectAsync(id, artifactId, request?.Comment, currentUser.Actor, cancellationToken);
        return rejected.IsSuccess
            ? TypedResults.Ok(await reader.ToDtoAsync(rejected.Value, cancellationToken))
            : ApiProblems.FromError(rejected.Error!);
    }
}
