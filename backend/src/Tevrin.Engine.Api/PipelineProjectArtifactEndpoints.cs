using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Runs;

namespace Tevrin.Engine.Api;

/// <summary>FR-352: a new version of a project-scope artifact.</summary>
public sealed record CreateProjectArtifactVersionRequest
{
    /// <summary>The content: a string for a Markdown artifact, the JSON value (checked against its schema) for a JSON one.</summary>
    public JsonElement Content { get; init; }
}

/// <summary>
/// FR-352, §4.76: source-of-truth (<c>scope: project</c>) artifacts, one version history per project and artifact key.
/// People add versions and lock them here; a run pins the newest locked version when it starts.
/// </summary>
public static partial class PipelineRunEndpoints
{
    private static void MapProjectArtifacts(IEndpointRouteBuilder app)
    {
        var artifacts = app.MapGroup("/api/projects/{id:guid}/pipelines/{pipelineId}/artifacts/{key}")
            .WithTags("Pipeline runs")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        artifacts.MapGet("/", ListProjectArtifactVersions).WithName("ListProjectArtifactVersions")
            .WithSummary("FR-352: the versions of a project-scope artifact, newest first (project viewer). 404 NOT_FOUND for an unknown pipeline or an artifact that isn't project-scope.")
            .Produces<IReadOnlyList<PipelineArtifactVersionDto>>();
        artifacts.MapPost("/versions", CreateProjectArtifactVersion).WithName("CreateProjectArtifactVersion")
            .WithSummary("FR-352: add the next version of a project-scope artifact, a draft unless the artifact auto-locks (Contributor). 400 VALIDATION (no content) or INPUT_INVALID (details: the failing paths), 403 FORBIDDEN, 404 NOT_FOUND.")
            .Produces<PipelineArtifactVersionDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        artifacts.MapPost("/versions/{version:int}/lock", LockProjectArtifactVersion).WithName("LockProjectArtifactVersion")
            .WithSummary("FR-352: lock a draft version of a project-scope artifact; runs started from now on pin it, running runs keep theirs (Owner). 403 FORBIDDEN, 404 NOT_FOUND, 409 ARTIFACT_NOT_DRAFT or CONFLICT.")
            .Produces<PipelineArtifactVersionDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Results<Ok<IReadOnlyList<PipelineArtifactVersionDto>>, ProblemHttpResult>> ListProjectArtifactVersions(
        Guid id, string pipelineId, string key, IRunControlAccess access, ProjectArtifacts artifacts, IEngineProblems problems,
        CancellationToken cancellationToken)
    {
        if (await access.CheckAsync(id, RunControlAction.ViewArtifacts, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        var listed = await artifacts.ListAsync(id, pipelineId, key, cancellationToken);
        if (listed.IsFailure)
        {
            return problems.FromError(listed.Error!);
        }

        var (definition, versions) = listed.Value;
        return TypedResults.Ok<IReadOnlyList<PipelineArtifactVersionDto>>([.. versions.Select(v => ToVersionDto(definition, v))]);
    }

    private static async Task<Results<Created<PipelineArtifactVersionDto>, ProblemHttpResult>> CreateProjectArtifactVersion(
        Guid id, string pipelineId, string key, CreateProjectArtifactVersionRequest? request, IRunControlAccess access,
        ProjectArtifacts artifacts, IEngineProblems problems, CancellationToken cancellationToken)
    {
        if (request is null || request.Content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return problems.FromError(PipelineRequests.Invalid("content", "content is required."));
        }

        if (await access.CheckAsync(id, RunControlAction.EditArtifacts, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        var created = await artifacts.CreateAsync(id, pipelineId, key, request.Content, access.UserId, cancellationToken);
        if (created.IsFailure)
        {
            return problems.FromError(created.Error!);
        }

        var dto = ToVersionDto(artifacts.Find(pipelineId, key).Value.Artifact, created.Value);
        return TypedResults.Created($"/api/projects/{id}/pipelines/{pipelineId}/artifacts/{key}", dto);
    }

    private static async Task<Results<Ok<PipelineArtifactVersionDto>, ProblemHttpResult>> LockProjectArtifactVersion(
        Guid id, string pipelineId, string key, int version, IRunControlAccess access, ProjectArtifacts artifacts, IEngineProblems problems,
        CancellationToken cancellationToken)
    {
        if (await access.CheckAsync(id, RunControlAction.LockArtifacts, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        var locked = await artifacts.LockAsync(id, pipelineId, key, version, access.UserId, cancellationToken);
        if (locked.IsFailure)
        {
            return problems.FromError(locked.Error!);
        }

        return TypedResults.Ok(ToVersionDto(artifacts.Find(pipelineId, key).Value.Artifact, locked.Value));
    }

    private static PipelineArtifactVersionDto ToVersionDto(ArtifactDefinition definition, Artifact artifact) =>
        new(artifact.Id, artifact.Type, definition.Title, artifact.Version, artifact.ParentVersion, artifact.Status,
            artifact.ContentHash, ContentHash.Matches(artifact.Content, artifact.ContentHash), artifact.Content, artifact.AgentRunId,
            artifact.CreatedAt, artifact.ApprovedAt);
}
