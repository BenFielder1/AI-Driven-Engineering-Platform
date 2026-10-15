using System.ComponentModel.DataAnnotations;
using Aide.Api.Agents;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Versions;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Runs;

/// <summary>FR-23: a project's agent runs (§6 <c>/api/projects/:id/runs[/:runId]</c>), for members. Read-only: runs are started by background jobs.</summary>
public static class RunEndpoints
{
    /// <summary>Longest output text returned by the detail endpoint.</summary>
    public const int MaxOutputLength = 200_000;

    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}/runs")
            .WithTags("Runs")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/", ListRuns)
            .WithName("ListRuns")
            .WithSummary("The project's agent runs, newest first, paged, optionally filtered by agent (members).")
            .Produces<PagedResult<AgentRunDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{runId:guid}", GetRun)
            .WithName("GetRun")
            .WithSummary("One agent run with its output (members). 404 NOT_FOUND for a run of another project.")
            .Produces<AgentRunDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Results<Ok<PagedResult<AgentRunDto>>, ValidationProblem, ProblemHttpResult>> ListRuns(
        Guid id,
        IProjectAccess access,
        AideDbContext db,
        CancellationToken cancellationToken,
        [WireEnum(typeof(AgentKind))] string? agent = null,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, ProjectLimits.MaxPageLimit)] int limit = 20,
        Guid? releaseVersionId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        // §6 Notes: runs of one release version (default: the version in development, else the latest released).
        var scope = await ReleaseVersionScope.ResolveAsync(db, id, releaseVersionId, cancellationToken);
        if (scope.IsFailure)
        {
            return ApiProblems.FromError(scope.Error!);
        }

        var versionId = scope.Value;
        var query = db.AgentRuns.AsNoTracking().Where(r => r.ProjectId == id && r.ReleaseVersionId == versionId);
        if (agent is not null)
        {
            var wanted = EnumStrings.Parse<AgentKind>(agent);
            query = query.Where(r => r.Agent == wanted);
        }

        var total = await query.CountAsync(cancellationToken);
        var skip = (long)(page - 1) * limit;
        if (skip >= total)
        {
            return TypedResults.Ok(new PagedResult<AgentRunDto>([], page, limit, total));
        }

        // Output (possibly large) isn't loaded for the list.
        var runs = await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Skip((int)skip)
            .Take(limit)
            .Select(r => new AgentRun
            {
                Id = r.Id,
                ProjectId = r.ProjectId,
                ReleaseVersionId = r.ReleaseVersionId,
                Agent = r.Agent,
                Phase = r.Phase,
                StepId = r.StepId,
                Provider = r.Provider,
                Model = r.Model,
                PromptVersion = r.PromptVersion,
                InputArtifacts = r.InputArtifacts,
                UserFeedback = r.UserFeedback,
                Status = r.Status,
                Attempts = r.Attempts,
                InputTokens = r.InputTokens,
                OutputTokens = r.OutputTokens,
                CostEstimate = r.CostEstimate,
                DurationMs = r.DurationMs,
                Error = r.Error,
                CreatedAt = r.CreatedAt,
                CompletedAt = r.CompletedAt,
            })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new PagedResult<AgentRunDto>([.. runs.Select(ToDto)], page, limit, total));
    }

    private static async Task<Results<Ok<AgentRunDetailDto>, ProblemHttpResult>> GetRun(
        Guid id,
        Guid runId,
        IProjectAccess access,
        AideDbContext db,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var run = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.ProjectId == id, cancellationToken);
        if (run is null)
        {
            return ApiProblems.NotFound("The agent run was not found.");
        }

        var text = AgentRunner.ReadOutput(run.Output)?.Text ?? run.Output;
        var truncated = text is { Length: > MaxOutputLength };
        var r = ToDto(run);
        return TypedResults.Ok(new AgentRunDetailDto(
            r.Id, r.ReleaseVersionId, r.Agent, r.Phase, r.StepId, r.Provider, r.Model, r.PromptVersion, r.InputArtifacts, r.UserFeedback, r.Status,
            r.Attempts, r.InputTokens, r.OutputTokens, r.CostEstimate, r.DurationMs, r.Error, r.CreatedAt, r.CompletedAt,
            truncated ? text![..MaxOutputLength] : text,
            truncated));
    }

    private static AgentRunDto ToDto(AgentRun r) => new(
        r.Id,
        r.ReleaseVersionId,
        r.Agent,
        r.Phase,
        r.StepId,
        r.Provider,
        r.Model,
        r.PromptVersion,
        [.. r.InputArtifacts.Select(a => new RunInputArtifactDto(a.Type, a.Version))],
        r.UserFeedback,
        r.Status,
        r.Attempts,
        r.InputTokens,
        r.OutputTokens,
        r.CostEstimate,
        r.DurationMs,
        r.Error,
        r.CreatedAt,
        r.CompletedAt);
}
