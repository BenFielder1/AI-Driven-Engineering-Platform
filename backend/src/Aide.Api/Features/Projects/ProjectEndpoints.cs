using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Admin;
using Aide.Domain.Access;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Projects;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Projects;

/// <summary>
/// FR-5–FR-7: projects (§6 <c>/api/projects</c>, <c>/:id</c>, <c>/:id/state</c>). Access follows the §2 matrix via
/// <c>IProjectAccess</c>: non-members get 404 (Admins see every project), members below the required role get 403.
/// </summary>
public static partial class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects")
            .WithTags("Projects")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/", ListProjects)
            .WithName("ListProjects")
            .WithSummary("The caller's projects (every project for Admins), paged, with search by name, phase and status filters and sorting.")
            .Produces<PagedResult<ProjectSummaryDto>>()
            .ProducesValidationProblem();

        group.MapPost("/", CreateProject)
            .WithName("CreateProject")
            .WithSummary("Create a project from the wizard; the caller becomes its Owner. Omitted settings take the FR-5 defaults.")
            .Produces<ProjectDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}", GetProject)
            .WithName("GetProject")
            .WithSummary("A project (members and Admins; 404 for anyone else).")
            .Produces<ProjectDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/{id:guid}", UpdateProject)
            .WithName("UpdateProject")
            .WithSummary("Edit name, approval mode, threshold and limits (Owner). 409 CONFLICT on a concurrent edit.")
            .Produces<ProjectDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", DeleteProject)
            .WithName("DeleteProject")
            .WithSummary("Delete a project and everything it owns (Owner).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/state", GetProjectState)
            .WithName("GetProjectState")
            .WithSummary("Phase, status, version, artifact summary, execution counts, review state and pending gates (members).")
            .Produces<ProjectStateDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Ok<PagedResult<ProjectSummaryDto>>> ListProjects(
        ICurrentUser currentUser,
        SystemRoleLookup roles,
        AideDbContext db,
        CancellationToken cancellationToken,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, ProjectLimits.MaxPageLimit)] int limit = 20,
        [MaxLength(ProjectLimits.SearchMaxLength)] string? search = null,
        [WireEnum(typeof(Phase))] string? phase = null,
        [WireEnum(typeof(ProjectStatus))] string? status = null,
        [WireEnum(typeof(ProjectSortBy))] string sortBy = "updated",
        [WireEnum(typeof(SortOrder))] string sortOrder = "desc")
    {
        var userId = currentUser.RequiredUserId;
        var isAdmin = await roles.GetAsync(userId, cancellationToken) == SystemRole.Admin;

        var query = db.Projects.AsNoTracking();
        if (!isAdmin)
        {
            query = query.Where(p => db.ProjectMembers.Any(m => m.ProjectId == p.Id && m.UserId == userId));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern, "\\"));
        }

        if (phase is not null)
        {
            var wanted = EnumStrings.Parse<Phase>(phase);
            query = query.Where(p => p.CurrentPhase == wanted);
        }

        if (status is not null)
        {
            var wanted = EnumStrings.Parse<ProjectStatus>(status);
            query = query.Where(p => p.Status == wanted);
        }

        var total = await query.CountAsync(cancellationToken);
        var skip = (long)(page - 1) * limit;
        if (skip >= total)
        {
            return TypedResults.Ok(new PagedResult<ProjectSummaryDto>([], page, limit, total));
        }

        var descending = EnumStrings.Parse<SortOrder>(sortOrder) == SortOrder.Desc;
        var ordered = EnumStrings.Parse<ProjectSortBy>(sortBy) switch
        {
            ProjectSortBy.Created => descending ? query.OrderByDescending(p => p.CreatedAt) : query.OrderBy(p => p.CreatedAt),
            ProjectSortBy.Name => descending ? query.OrderByDescending(p => p.Name) : query.OrderBy(p => p.Name),
            _ => descending ? query.OrderByDescending(p => p.UpdatedAt) : query.OrderBy(p => p.UpdatedAt),
        };
        ordered = descending ? ordered.ThenByDescending(p => p.Id) : ordered.ThenBy(p => p.Id);

        var items = await ordered
            .Skip((int)skip)
            .Take(limit)
            .Select(p => new ProjectSummaryDto(
                p.Id,
                p.Name,
                p.Slug,
                p.CurrentPhase,
                p.Status,
                p.ProjectVersion,
                p.ApprovalMode,
                db.ProjectMembers.Where(m => m.ProjectId == p.Id && m.UserId == userId).Select(m => (ProjectRole?)m.Role).FirstOrDefault(),
                p.CreatedAt,
                p.UpdatedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new PagedResult<ProjectSummaryDto>(items, page, limit, total));
    }

    private static async Task<Results<Created<ProjectDto>, ValidationProblem>> CreateProject(
        CreateProjectRequest request,
        ICurrentUser currentUser,
        ProjectCreator creator,
        CancellationToken cancellationToken)
    {
        var created = await creator.CreateAsync(request, currentUser.RequiredUserId, currentUser.Actor, cancellationToken);
        if (created.IsFailure)
        {
            return ApiProblems.Validation(created.Error!);
        }

        var project = created.Value;
        return TypedResults.Created($"/api/projects/{project.Id}", ProjectMapping.ToDto(project, ProjectRole.Owner));
    }

    private static async Task<Results<Ok<ProjectDto>, ProblemHttpResult>> GetProject(
        Guid id,
        IProjectAccess access,
        AideDbContext db,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        return project is null
            ? ApiProblems.NotFound("The project was not found.")
            : TypedResults.Ok(ProjectMapping.ToDto(project, grant.Value.ProjectRole));
    }

    private static async Task<Results<Ok<ProjectDto>, ValidationProblem, ProblemHttpResult>> UpdateProject(
        Guid id,
        UpdateProjectRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ChangeSettings, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var validation = ProjectRules.ValidateUpdate(request.Name, request.ComplianceThreshold, request.TokenBudget, request.ClearTokenBudget);
        if (validation.IsFailure)
        {
            return ApiProblems.Validation(validation.Error!);
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (project is null)
        {
            return ApiProblems.NotFound("The project was not found.");
        }

        var changes = new List<string>();
        void Set<T>(string field, T current, T? requested, Action<T> apply)
            where T : struct
        {
            if (requested is { } value && !EqualityComparer<T>.Default.Equals(current, value))
            {
                apply(value);
                changes.Add(field);
            }
        }

        if (request.Name?.Trim() is { } name && name != project.Name)
        {
            project.Name = name;
            changes.Add("name");
        }

        Set("approvalMode", project.ApprovalMode, request.ApprovalMode, v => project.ApprovalMode = v);
        Set("complianceThreshold", project.ComplianceThreshold, request.ComplianceThreshold, v => project.ComplianceThreshold = v);
        Set("maxFilesPerStep", project.MaxFilesPerStep, request.MaxFilesPerStep, v => project.MaxFilesPerStep = v);
        Set("maxReviewCycles", project.MaxReviewCycles, request.MaxReviewCycles, v => project.MaxReviewCycles = v);
        if (request.ClearTokenBudget && project.TokenBudget is not null)
        {
            project.TokenBudget = null;
            changes.Add("tokenBudget");
        }
        else
        {
            Set("tokenBudget", project.TokenBudget ?? 0, request.TokenBudget, v => project.TokenBudget = v);
        }

        if (changes.Count > 0)
        {
            var now = time.GetUtcNow();
            project.UpdatedAt = now;
            db.AddEvent(EventTypes.ProjectUpdated, project.Id, new { projectId = project.Id, changes }, currentUser.Actor, now);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ConcurrentEdit();
            }

            var logger = loggers.CreateLogger(typeof(ProjectEndpoints));
            LogUpdated(logger, project.Id, changes);
        }

        return TypedResults.Ok(ProjectMapping.ToDto(project, grant.Value.ProjectRole));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteProject(
        Guid id,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        IWorkspaceStore workspaces,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.DeleteProject, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (project is null)
        {
            return ApiProblems.NotFound("The project was not found.");
        }

        // Everything the project owns goes with it (FK cascades); the audit record outlives it with no project_id.
        db.Projects.Remove(project);
        db.AddEvent(
            EventTypes.ProjectDeleted,
            null,
            new { projectId = project.Id, name = project.Name, slug = project.Slug },
            currentUser.Actor,
            time.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentEdit();
        }

        workspaces.TryDelete(project.Id);
        var logger = loggers.CreateLogger(typeof(ProjectEndpoints));
        LogDeleted(logger, project.Id);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<ProjectStateDto>, ProblemHttpResult>> GetProjectState(
        Guid id,
        IProjectAccess access,
        AideDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var state = await LoadStateAsync(db, id, time, cancellationToken);
        return state is null ? ApiProblems.NotFound("The project was not found.") : TypedResults.Ok(state);
    }

    /// <summary>The §6 project state, read from the database (no tracking); <c>null</c> if the project doesn't exist.</summary>
    internal static async Task<ProjectStateDto?> LoadStateAsync(AideDbContext db, Guid id, TimeProvider time, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (project is null)
        {
            return null;
        }

        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == id)
            .Select(a => new { a.Id, a.Type, a.Version, a.Status, a.CreatedAt })
            .ToListAsync(cancellationToken);
        var artifactStates = ArtifactRules.All
            .Select(type =>
            {
                var versions = artifacts.Where(a => a.Type == type).ToList();
                var latest = versions.MaxBy(a => a.Version);
                return new ArtifactStateDto(
                    type,
                    latest?.Version,
                    latest?.Status,
                    ArtifactRules.Current(versions.Select(a => new ArtifactVersion(a.Version, a.Status))));
            })
            .ToList();

        // The current plan: the most recently created locked plan (implementation plan, or the optimisation plan of
        // a review cycle). An approved new implementation plan invalidates the optimisation plan (§4.4).
        var currentPlanId = artifacts
            .Where(a => a.Status == ArtifactStatus.Locked && a.Type is ArtifactType.ImplementationPlan or ArtifactType.OptimizationPlan)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefault();
        var stepCounts = currentPlanId is { } planId
            ? await db.ExecutionSteps.AsNoTracking()
                .Where(s => s.PlanArtifactId == planId)
                .GroupBy(s => s.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Status, g => g.Count, cancellationToken)
            : [];
        int Count(StepStatus s) => stepCounts.GetValueOrDefault(s);
        var execution = new ExecutionCountsDto(
            stepCounts.Values.Sum(),
            Count(StepStatus.Pending),
            Count(StepStatus.InProgress),
            Count(StepStatus.Completed),
            Count(StepStatus.Rejected),
            Count(StepStatus.Blocked));

        // Reports from versions above the current one were undone by a rollback (§4.8).
        var report = await db.ComplianceReports.AsNoTracking()
            .Where(r => r.ProjectId == id && r.ProjectVersion <= project.ProjectVersion)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => new { r.Score, r.Decision, r.DriftLevel })
            .FirstOrDefaultAsync(cancellationToken);
        var review = new ReviewStateDto(
            project.ReviewCycle,
            project.MaxReviewCycles + project.ExtraReviewCycles,
            report?.Score,
            report?.Decision,
            report?.DriftLevel);

        var now = time.GetUtcNow();
        var gates = await db.ApprovalGates.AsNoTracking()
            .Where(g => g.ProjectId == id && g.Status == GateStatus.Pending)
            .OrderBy(g => g.CreatedAt)
            .Select(g => new { g.Id, g.Type, g.CreatedAt, g.Overdue })
            .ToListAsync(cancellationToken);
        var pendingGates = gates
            .Select(g => new GateSummaryDto(g.Id, g.Type, g.CreatedAt, g.Overdue || now - g.CreatedAt >= GateTimers.OverdueAfter))
            .ToList();

        return new ProjectStateDto(
            project.Id,
            project.CurrentPhase,
            project.Status,
            project.ProjectVersion,
            project.ExecutionPaused,
            artifactStates,
            execution,
            review,
            pendingGates,
            project.TokensUsed,
            project.TokenBudget,
            ExecutionRules.Status(
                [.. stepCounts.Where(c => c.Key != StepStatus.Invalidated).SelectMany(c => Enumerable.Repeat(c.Key, c.Value))],
                project.ExecutionPaused,
                await Execution.ExecutionQueries.JobActiveAsync(db, id, cancellationToken)));
    }

    internal static ProblemHttpResult ConcurrentEdit() =>
        ApiProblems.Of(ErrorCodes.Conflict, "The project was changed by someone else. Reload and try again.");

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} updated ({Changes})")]
    private static partial void LogUpdated(ILogger logger, Guid projectId, List<string> changes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} deleted")]
    private static partial void LogDeleted(ILogger logger, Guid projectId);
}
