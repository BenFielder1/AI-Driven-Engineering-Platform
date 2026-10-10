using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Activity;
using Aide.Api.Features.Projects;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Admin;

/// <summary>
/// FR-51 (and FR-23's admin side): the cross-project event log, agent run log and dead letters (§6 <c>/api/admin/events ·
/// /runs · /dead-letters</c>), newest first, paged like the other admin lists. Admins only; an Admin's API token can read
/// the lists, but retrying a dead letter needs a browser session (as for role changes).
/// </summary>
public static class AdminOperationsEndpoints
{
    public const int MaxLimit = 100;
    public const int DefaultLimit = 20;
    public const int TypeMaxLength = 100;
    public const int ModelMaxLength = 200;

    public static IEndpointRouteBuilder MapAdminOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin")
            .WithTags("Admin")
            .RequireAuthorization(AuthConstants.AdminPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/events", ListEvents)
            .WithName("ListAdminEvents")
            .WithSummary("Audit events of every project, newest first, paged. Filter by projectId, type (exact event type, e.g. RUN_FAILED), category, outcome (ok or failure) and createdAt range from/to (inclusive).")
            .ProducesValidationProblem();

        group.MapGet("/runs", ListRuns)
            .WithName("ListAdminRuns")
            .WithSummary("Agent runs of every project, newest first, paged. Filter by projectId, agent, status, provider and model (exact).")
            .ProducesValidationProblem();

        group.MapGet("/dead-letters", ListDeadLetters)
            .WithName("ListDeadLetters")
            .WithSummary("Dead-lettered jobs, most recently dead-lettered first, paged. Filter by projectId and job type; includeRetried also lists dead letters already retried.")
            .ProducesValidationProblem();

        group.MapPost("/dead-letters/{jobId:guid}/retry", RetryDeadLetter)
            .WithName("RetryDeadLetter")
            .WithSummary("Re-enqueue a dead letter's job (browser session only): the dead letter is marked retried and a new queued job with the same type and payload is returned. 404 NOT_FOUND if the job isn't a dead letter; 409 CONFLICT if the project already has a queued or running execution job.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<Results<Ok<PagedResult<AdminEventDto>>, ValidationProblem>> ListEvents(
        AideDbContext db,
        CancellationToken cancellationToken,
        Guid? projectId = null,
        [MaxLength(TypeMaxLength), RegularExpression("^[A-Z][A-Z0-9_]*$", ErrorMessage = "type must be an event type such as RUN_FAILED.")] string? type = null,
        [WireEnum(typeof(EventCategory))] string? category = null,
        [WireEnum(typeof(EventOutcome))] string? outcome = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, MaxLimit)] int limit = DefaultLimit)
    {
        if (from > to)
        {
            return RangeProblem();
        }

        var query = db.Events.AsNoTracking();
        if (projectId is { } pid)
        {
            query = query.Where(e => e.ProjectId == pid);
        }

        if (type is not null)
        {
            query = query.Where(e => e.Type == type);
        }

        if (category is not null)
        {
            var types = EventTypes.InCategory(EnumStrings.Parse<EventCategory>(category)).ToList();
            query = query.Where(e => types.Contains(e.Type));
        }

        if (outcome is not null)
        {
            var failures = EventTypes.Failures.ToList();
            query = EnumStrings.Parse<EventOutcome>(outcome) == EventOutcome.Failure
                ? query.Where(e => failures.Contains(e.Type))
                : query.Where(e => !failures.Contains(e.Type));
        }

        if (from is { } start)
        {
            query = query.Where(e => e.CreatedAt >= start);
        }

        if (to is { } end)
        {
            query = query.Where(e => e.CreatedAt <= end);
        }

        var total = await query.CountAsync(cancellationToken);
        if (SkipFor(page, limit) >= total)
        {
            return TypedResults.Ok(new PagedResult<AdminEventDto>([], page, limit, total));
        }

        var events = await query
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var actorOf = await ActorResolver.ForActorsAsync(db, events.Select(e => e.Actor), cancellationToken);
        var names = await ProjectNamesAsync(db, events.Select(e => e.ProjectId), cancellationToken);
        var items = events.Select(e =>
        {
            var payload = ActivityEndpoints.ParsePayload(e.Payload);
            return new AdminEventDto(
                e.Id,
                e.ProjectId,
                NameOf(names, e.ProjectId),
                e.Type,
                EventTypes.CategoryOf(e.Type),
                EventTypes.OutcomeOf(e.Type),
                actorOf(e.Actor),
                ActivitySummary.For(e.Type, payload),
                payload,
                e.CreatedAt);
        }).ToList();
        return TypedResults.Ok(new PagedResult<AdminEventDto>(items, page, limit, total));
    }

    private static async Task<Ok<PagedResult<AdminAgentRunDto>>> ListRuns(
        AideDbContext db,
        CancellationToken cancellationToken,
        Guid? projectId = null,
        [WireEnum(typeof(AgentKind))] string? agent = null,
        [WireEnum(typeof(AgentRunStatus))] string? status = null,
        [WireEnum(typeof(LlmProvider))] string? provider = null,
        [MaxLength(ModelMaxLength)] string? model = null,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, MaxLimit)] int limit = DefaultLimit)
    {
        var query = db.AgentRuns.AsNoTracking();
        if (projectId is { } pid)
        {
            query = query.Where(r => r.ProjectId == pid);
        }

        if (agent is not null)
        {
            var wanted = EnumStrings.Parse<AgentKind>(agent);
            query = query.Where(r => r.Agent == wanted);
        }

        if (status is not null)
        {
            var wanted = EnumStrings.Parse<AgentRunStatus>(status);
            query = query.Where(r => r.Status == wanted);
        }

        if (provider is not null)
        {
            var wanted = EnumStrings.Parse<LlmProvider>(provider);
            query = query.Where(r => r.Provider == wanted);
        }

        if (!string.IsNullOrEmpty(model))
        {
            query = query.Where(r => r.Model == model);
        }

        var total = await query.CountAsync(cancellationToken);
        if (SkipFor(page, limit) >= total)
        {
            return TypedResults.Ok(new PagedResult<AdminAgentRunDto>([], page, limit, total));
        }

        // Output (possibly large) isn't loaded for the list.
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Join(db.Projects, r => r.ProjectId, p => p.Id, (r, p) => new AdminAgentRunDto(
                r.Id,
                r.ProjectId,
                p.Name,
                r.Agent,
                r.Phase,
                r.StepId,
                r.Provider,
                r.Model,
                r.PromptVersion,
                r.Status,
                r.Attempts,
                r.InputTokens,
                r.OutputTokens,
                r.CostEstimate,
                r.DurationMs,
                r.Error,
                r.CreatedAt,
                r.CompletedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new PagedResult<AdminAgentRunDto>(items, page, limit, total));
    }

    private static async Task<Ok<PagedResult<DeadLetterDto>>> ListDeadLetters(
        AideDbContext db,
        CancellationToken cancellationToken,
        Guid? projectId = null,
        [MaxLength(TypeMaxLength)] string? type = null,
        bool includeRetried = false,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, MaxLimit)] int limit = DefaultLimit)
    {
        var query = includeRetried
            ? db.Jobs.AsNoTracking().Where(j => j.Status == JobStatus.Dead || j.Status == JobStatus.Retried)
            : db.Jobs.AsNoTracking().Where(j => j.Status == JobStatus.Dead);
        if (projectId is { } pid)
        {
            query = query.Where(j => j.ProjectId == pid);
        }

        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(j => j.Type == type);
        }

        var total = await query.CountAsync(cancellationToken);
        if (SkipFor(page, limit) >= total)
        {
            return TypedResults.Ok(new PagedResult<DeadLetterDto>([], page, limit, total));
        }

        var jobs = await query
            .OrderByDescending(j => j.CompletedAt ?? j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var names = await ProjectNamesAsync(db, jobs.Select(j => j.ProjectId), cancellationToken);
        var items = jobs.Select(j => new DeadLetterDto(
            j.Id,
            j.Type,
            j.ProjectId,
            NameOf(names, j.ProjectId),
            j.Status,
            j.Attempts,
            j.LastError,
            ActivityEndpoints.ParsePayload(j.Payload),
            j.CreatedAt,
            j.CompletedAt)).ToList();
        return TypedResults.Ok(new PagedResult<DeadLetterDto>(items, page, limit, total));
    }

    private static async Task<Results<Ok<JobDto>, ProblemHttpResult>> RetryDeadLetter(
        Guid jobId,
        DeadLetterService deadLetters,
        CancellationToken cancellationToken)
    {
        var result = await deadLetters.RetryAsync(jobId, cancellationToken);
        return result.IsFailure
            ? ApiProblems.FromError(result.Error!)
            : TypedResults.Ok(JobDto.From(result.Value));
    }

    private static long SkipFor(int page, int limit) => (long)(page - 1) * limit;

    private static ValidationProblem RangeProblem() =>
        ApiProblems.Validation(new Dictionary<string, string[]> { ["from"] = ["from must not be after to."] });

    private static async Task<Dictionary<Guid, string>> ProjectNamesAsync(AideDbContext db, IEnumerable<Guid?> projectIds, CancellationToken cancellationToken)
    {
        var ids = projectIds.OfType<Guid>().Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.Projects.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
    }

    private static string? NameOf(Dictionary<Guid, string> names, Guid? projectId) =>
        projectId is { } id && names.TryGetValue(id, out var name) ? name : null;
}
