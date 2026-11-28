using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Api;

/// <summary>
/// FR-298, FR-299, FR-302: pipeline definitions, prompt comparison and the gate inbox (SPEC §6). Signed-in users only;
/// prompt stats and the inbox cover only the projects the caller can see.
/// </summary>
public static class PipelineEndpoints
{
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var pipelines = app.MapGroup("/api/pipelines")
            .WithTags("Pipelines")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        pipelines.MapGet("/", ListPipelines)
            .WithName("ListPipelines")
            .WithSummary("FR-299: the registered pipelines (id, title, version, current definition hash, pack).")
            .Produces<IReadOnlyList<PipelineSummaryDto>>();

        pipelines.MapGet("/{pipelineId}/definitions/{hash}", GetDefinition)
            .WithName("GetPipelineDefinition")
            .WithSummary("FR-299: a pipeline definition by hash (registered or stored): stages, artifacts and statuses; FR-362, FR-363: run control, approval modes, triggers, follow-ups, input artifacts with their JSON schemas, and each stage's constructs. 404 NOT_FOUND for an unknown hash.")
            .Produces<PipelineDefinitionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        pipelines.MapGet("/{pipelineId}/prompt-stats", GetPromptStats)
            .WithName("GetPipelinePromptStats")
            .WithSummary("FR-298: the pipeline's agent runs grouped by agent key and prompt version, over projectId (project viewer) or every project the caller can see. 404 NOT_FOUND for an unknown pipeline or project.")
            .Produces<IReadOnlyList<PromptStatsDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/api/pipeline-gates", GetGateInbox)
            .WithTags("Pipelines")
            .RequireAuthorization()
            .WithName("ListPipelineGateInbox")
            .WithSummary("FR-302: gates (default pending) on every project the caller can see, newest first, cursor-paged (limit 1-200, default 50), filterable by pipelineId and gateType. 400 VALIDATION for a bad status, cursor or limit.")
            .Produces<PipelineGateInboxPageDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static Ok<IReadOnlyList<PipelineSummaryDto>> ListPipelines(PipelineDefinitionResolver definitions) =>
        TypedResults.Ok<IReadOnlyList<PipelineSummaryDto>>([.. definitions.Registered()
            .Select(d => new PipelineSummaryDto(d.Definition!.Pipeline, d.Definition.Title, d.Definition.Version, d.Hash!, d.PackId)
            {
                RunControl = d.RunControl,
            })]);

    private static async Task<Results<Ok<PipelineDefinitionDto>, ProblemHttpResult>> GetDefinition(
        string pipelineId,
        string hash,
        PipelineDefinitionResolver definitions,
        PipelineRegistryHost registryHost,
        IEngineProblems problems,
        CancellationToken cancellationToken)
    {
        var resolved = hash.Length > 128 ? null : await definitions.ResolveAsync(hash, cancellationToken);
        if (resolved is null || !string.Equals(resolved.Definition.Pipeline, pipelineId, StringComparison.Ordinal))
        {
            return problems.FromError(new DomainError(EngineErrorCodes.NotFound, "The pipeline definition was not found."));
        }

        // FR-362: input schemas come from the pack's registered schema files; run control from the pack (engine when unknown).
        var registry = registryHost.Registry;
        var schemas = registry?.Packs.FirstOrDefault(p => p.PackId == resolved.PackId)?.Schemas;
        return TypedResults.Ok(PipelineDtoMapper.ToDefinitionDto(
            resolved.Definition,
            resolved.Hash,
            resolved.PackId,
            registry?.RunControlOf(pipelineId) ?? RunControl.Engine,
            name => schemas?.GetValueOrDefault(name)));
    }

    private static async Task<Results<Ok<IReadOnlyList<PromptStatsDto>>, ProblemHttpResult>> GetPromptStats(
        string pipelineId,
        IEngineDbContext db,
        PipelineDefinitionResolver definitions,
        IRunAccessPolicy access,
        RunLogQueries runLog,
        IEngineProblems problems,
        CancellationToken cancellationToken,
        Guid? projectId = null)
    {
        var known = definitions.Registered().Any(d => d.Definition!.Pipeline == pipelineId)
            || await db.PipelineDefinitions.AsNoTracking().AnyAsync(d => d.PipelineId == pipelineId, cancellationToken);
        if (!known)
        {
            return problems.FromError(new DomainError(EngineErrorCodes.NotFound, "The pipeline was not found."));
        }

        IReadOnlyList<PromptStats> stats;
        if (projectId is { } id)
        {
            if (await access.CheckViewAsync(id, cancellationToken) is { } denied)
            {
                return problems.FromError(denied);
            }

            stats = await runLog.PromptStatsAsync(pipelineId, new[] { id }, cancellationToken);
        }
        else
        {
            stats = await runLog.PromptStatsForVisibleProjectsAsync(pipelineId, await access.VisibleProjectIdsAsync(cancellationToken), cancellationToken);
        }

        return TypedResults.Ok<IReadOnlyList<PromptStatsDto>>([.. stats.Select(PipelineDtoMapper.ToDto)]);
    }

    private static async Task<Results<Ok<PipelineGateInboxPageDto>, ProblemHttpResult>> GetGateInbox(
        PipelineGateInbox inbox,
        IEngineProblems problems,
        CancellationToken cancellationToken,
        string? status = null,
        string? pipelineId = null,
        string? gateType = null,
        string? cursor = null,
        int? limit = null)
    {
        var wanted = GateStatus.Pending;
        if (status is not null && !EnumStrings.TryParse(status, out wanted))
        {
            return problems.FromError(PipelineRequests.Invalid("status", $"status must be one of {string.Join(", ", EnumStrings.Values<GateStatus>())}."));
        }

        if (PipelineRequests.Paging(cursor, limit, out var after, out var size) is { } invalid)
        {
            return problems.FromError(invalid);
        }

        return TypedResults.Ok(await inbox.PageAsync(wanted, pipelineId, gateType, after, size, cancellationToken));
    }
}

/// <summary>Request checks shared by the engine endpoints (VALIDATION with the field named, as the host's problems).</summary>
internal static class PipelineRequests
{
    public static DomainError Invalid(string field, string message) =>
        new(EngineErrorCodes.Validation, message) { Field = field };

    /// <summary>A <c>cursor</c> and <c>limit</c> (1–200, default 50), or the VALIDATION error.</summary>
    public static DomainError? Paging(string? cursor, int? limit, out TimelineKey? after, out int size)
    {
        after = null;
        size = limit ?? RunTimeline.DefaultLimit;
        if (size is < 1 or > RunTimeline.MaxLimit)
        {
            return Invalid("limit", $"limit must be between 1 and {RunTimeline.MaxLimit}.");
        }

        if (cursor is not null)
        {
            after = RunTimeline.TryDecode(cursor);
            if (after is null)
            {
                return Invalid("cursor", "cursor must be a next value from a previous page.");
            }
        }

        return null;
    }
}
