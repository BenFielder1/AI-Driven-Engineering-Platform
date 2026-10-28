using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Admin;
using Aide.Api.Features.GitHub;
using Aide.Api.Features.Release;
using Aide.Api.Features.Review;
using Aide.Domain.Access;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Projects;
using Aide.Domain.Releases;
using Aide.Domain.Review;
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
            .WithSummary("The caller's projects (every project for Admins), paged, with search by name, phase, status, has-a-version-in-development and pack (FR-117: the current version's stack composition uses that pack id, e.g. be-aspnet or deploy-vercel) filters and sorting. Rows carry the released and in-development version badges (FR-62) and, as history, the retired preset a migrated version was pinned to (FR-129).")
            .Produces<PagedResult<ProjectSummaryDto>>()
            .ProducesValidationProblem();

        group.MapGet("/kpis", GetProjectKpis)
            .WithName("GetProjectKpis")
            .WithSummary("§7.2: the projects-list KPI row over the caller's projects (every project for Admins) matching the same search, phase, status, hasDevelopmentVersion and pack filters as ListProjects: total, active (not released), inReview (status reviewing), awaitingGates (pending gates across those projects) and released (status released).")
            .Produces<ProjectKpisDto>()
            .ProducesValidationProblem();

        group.MapPost("/", CreateProject)
            .WithName("CreateProject")
            .WithSummary("Create a project from the wizard; the caller becomes its Owner. Omitted settings take the FR-5 defaults. source (FR-73) is scratch (default: initialVersion, FR-66, default 0.1.0, is the first release version, in development) or import (import {releasedTag (null: never released), developmentVersion}; idea is the version's request; git.localSourcePath for a local import where Git:Local:ImportRoot is set). git.mode (FR-82, FR-86; v1 alias git.provider) is github_app (installationId + repoFullName), github_pat (repoUrl + token) or local. 400 VERSION_INVALID, TAG_NOT_FOUND; 409 VERSION_NOT_GREATER, TAG_EXISTS, GITHUB_APP_NOT_INSTALLED when the App can't access the picked repo. An import creates an imported release version for the released tag (unless never released) and the version to develop, based on that tag's commit (or the default branch head); a version with existing code (an import, or a scratch project on a repository with code, FR-86) queues the Repository Analyst (FR-70). stack (FR-114–FR-117, optional; omitted: No stack (general)) is the stack composition, validated like POST /api/stack/validate (400 STACK_INCOMPATIBLE with violations) plus R5 (the VCS layer is the repository's host, set from it when omitted: 400 VCS_MISMATCH), and pinned with the catalog version and hash. deployment (FR-88, optional) sets up Vercel with one of the caller's own Vercel connections (403 CREDENTIAL_NOT_OWNER, 404 NOT_FOUND) and an existing (vercelProjectId) or new (create) Vercel project; 409 VERCEL_GIT_NOT_CONNECTED, VERCEL_PLAN_LIMIT. llm (FR-159, optional) sets the LLM provider, model(s) and connection (one of the caller's own: 403 CREDENTIAL_NOT_OWNER, 404 NOT_FOUND); a provider that is disabled on this server or not usable by the caller, or no usable provider when llm is omitted: 400 LLM_PROVIDER_UNAVAILABLE.")
            .Produces<ProjectDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION, VERSION_INVALID (initialVersion, import.developmentVersion or import.releasedTag), TAG_NOT_FOUND (import: the released tag isn't in the repository), STACK_INCOMPATIBLE (violations extension), VCS_MISMATCH or LLM_PROVIDER_UNAVAILABLE (llm: provider disabled or not usable by the caller)"),
                (StatusCodes.Status403Forbidden, "FORBIDDEN (github_app: the caller's GitHub account can't push to the repository) or CREDENTIAL_NOT_OWNER (deployment: another user's Vercel connection; llm: another user's LLM connection)"),
                (StatusCodes.Status404NotFound, "NOT_FOUND (import with github_pat: the repository doesn't exist or the token can't see it; deployment: unknown Vercel connection or project; llm: unknown LLM connection)"),
                (StatusCodes.Status409Conflict, "GITHUB_APP_NOT_INSTALLED (extension installUrl) or GITHUB_NOT_CONNECTED (github_app); import: VERSION_NOT_GREATER (developmentVersion not above the released tag) or TAG_EXISTS (the development version's tag exists); deployment: VERCEL_GIT_NOT_CONNECTED (extension installUrl) or VERCEL_PLAN_LIMIT"),
                (StatusCodes.Status429TooManyRequests, "GITHUB_RATE_LIMITED (Retry-After: seconds until GitHub's rate-limit reset)"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR (GitHub failed or couldn't be reached)"));

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
        Infrastructure.Stack.IStackCatalog catalog,
        CancellationToken cancellationToken,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, ProjectLimits.MaxPageLimit)] int limit = 20,
        [MaxLength(ProjectLimits.SearchMaxLength)] string? search = null,
        [WireEnum(typeof(Phase))] string? phase = null,
        [WireEnum(typeof(ProjectStatus))] string? status = null,
        bool? hasDevelopmentVersion = null,
        [MaxLength(ProjectLimits.PackMaxLength)] string? pack = null,
        [WireEnum(typeof(ProjectSortBy))] string sortBy = "updated",
        [WireEnum(typeof(SortOrder))] string sortOrder = "desc")
    {
        var userId = currentUser.RequiredUserId;
        var query = await FilteredAsync(db, roles, userId, search, phase, status, hasDevelopmentVersion, pack, cancellationToken);

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

        var rows = await ordered
            .Skip((int)skip)
            .Take(limit)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Slug,
                p.CurrentPhase,
                p.Status,
                p.Checkpoint,
                p.ReleasedVersion,
                p.DevelopmentVersion,
                p.ApprovalMode,
                MyRole = db.ProjectMembers.Where(m => m.ProjectId == p.Id && m.UserId == userId).Select(m => (ProjectRole?)m.Role).FirstOrDefault(),
                p.CreatedAt,
                p.UpdatedAt,
                p.StackPacks,
                Preset = db.ReleaseVersions
                    .Where(v => v.ProjectId == p.Id)
                    .OrderByDescending(v => v.Id == p.CurrentReleaseVersionId)
                    .ThenByDescending(v => v.CreatedAt)
                    .ThenByDescending(v => v.Id)
                    .Select(v => new { v.PresetId, v.PresetVersion })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        var items = rows.Select(r =>
        {
            var pinned = Stack.RetiredPresets.Describe(catalog, r.Preset?.PresetId, r.Preset?.PresetVersion);
            return new ProjectSummaryDto(
                r.Id,
                r.Name,
                r.Slug,
                r.CurrentPhase,
                r.Status,
                r.Checkpoint,
                r.ReleasedVersion,
                r.DevelopmentVersion,
                r.ApprovalMode,
                r.MyRole,
                r.CreatedAt,
                r.UpdatedAt,
                pinned.PresetId,
                pinned.PresetName,
                pinned.PresetVersion,
                [.. r.StackPacks]);
        }).ToList();
        return TypedResults.Ok(new PagedResult<ProjectSummaryDto>(items, page, limit, total));
    }

    private static async Task<Ok<ProjectKpisDto>> GetProjectKpis(
        ICurrentUser currentUser,
        SystemRoleLookup roles,
        AideDbContext db,
        CancellationToken cancellationToken,
        [MaxLength(ProjectLimits.SearchMaxLength)] string? search = null,
        [WireEnum(typeof(Phase))] string? phase = null,
        [WireEnum(typeof(ProjectStatus))] string? status = null,
        bool? hasDevelopmentVersion = null,
        [MaxLength(ProjectLimits.PackMaxLength)] string? pack = null)
    {
        var query = await FilteredAsync(db, roles, currentUser.RequiredUserId, search, phase, status, hasDevelopmentVersion, pack, cancellationToken);

        // One aggregate query over the filtered projects; the gate count is a correlated subquery per project row in SQL.
        var counts = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Active = g.Count(p => p.Status != ProjectStatus.Released),
                InReview = g.Count(p => p.Status == ProjectStatus.Reviewing),
                Released = g.Count(p => p.Status == ProjectStatus.Released),
                AwaitingGates = g.Sum(p => db.ApprovalGates.Count(gate => gate.ProjectId == p.Id && gate.Status == GateStatus.Pending)),
            })
            .FirstOrDefaultAsync(cancellationToken);
        return TypedResults.Ok(counts is null
            ? new ProjectKpisDto(0, 0, 0, 0, 0)
            : new ProjectKpisDto(counts.Total, counts.Active, counts.InReview, counts.AwaitingGates, counts.Released));
    }

    /// <summary>
    /// The projects the caller sees (members; Admins: every project) matching the list filters — shared by the list and
    /// its KPI row (§7.2) so both count the same projects.
    /// </summary>
    private static async Task<IQueryable<Project>> FilteredAsync(
        AideDbContext db,
        SystemRoleLookup roles,
        Guid userId,
        string? search,
        string? phase,
        string? status,
        bool? hasDevelopmentVersion,
        string? pack,
        CancellationToken cancellationToken)
    {
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

        // FR-62: the denormalised development_version column keeps this filter (and the badges) cheap.
        if (hasDevelopmentVersion is { } hasDevelopment)
        {
            query = hasDevelopment ? query.Where(p => p.DevelopmentVersion != null) : query.Where(p => p.DevelopmentVersion == null);
        }

        // FR-117, §5: projects whose current version's composition uses the pack (the denormalised, GIN-indexed stack_packs).
        if (!string.IsNullOrWhiteSpace(pack))
        {
            var wanted = pack.Trim();
            query = query.Where(p => p.StackPacks.Contains(wanted));
        }

        return query;
    }

    private static async Task<Results<Created<ProjectDto>, ValidationProblem, ProblemHttpResult>> CreateProject(
        CreateProjectRequest request,
        ICurrentUser currentUser,
        ProjectCreator creator,
        Stack.RetiredPresets presets,
        Infrastructure.GitHub.IGitHubAppStore apps,
        AideDbContext db,
        Deployments.DeploymentAccess deploymentAccess,
        Deployments.DeploymentSettingsService deploymentSettings,
        CancellationToken cancellationToken)
    {
        Deployments.PreparedDeployment? preparedDeployment = null;
        if (request.Deployment is { } deployment)
        {
            // FR-88 (wizard Deployment step): validated, the connection must be the caller's own, and Vercel is asked
            // (link or create the Vercel project) before the project is created, so a refusal saves nothing.
            var git = request.Git.ModeSent || request.Git.ProviderSent
                ? (request.Git.ModeSent ? request.Git.Mode : request.Git.Provider)
                : GitProvider.GithubApp;
            var validation = DeploymentSettingsRules.Validate(
                deployment.Provider,
                deployment.ConnectionId,
                deployment.VercelProjectId,
                deployment.Create is not null,
                deployment.Create?.Name,
                deployment.Create?.RootDirectory,
                git,
                allowFake: deploymentAccess.UsesFake,
                requireConnection: true,
                fieldPrefix: "deployment.");
            if (validation.IsFailure)
            {
                return ApiProblems.Validation(validation.Error!);
            }

            if (await Deployments.DeploymentEndpoints.CanBindAsync(db, currentUser.RequiredUserId, deployment.ConnectionId, cancellationToken) is { } bindProblem)
            {
                return bindProblem;
            }

            var repoFullName = git switch
            {
                GitProvider.GithubApp => request.Git.RepoFullName,
                GitProvider.GithubPat when ProjectSettingsRules.TryParseGitHubRepo(request.Git.RepoUrl, out var repo) => $"{repo.Owner}/{repo.Name}",
                _ => null,
            };
            var prepared = await deploymentSettings.PrepareAsync(
                deployment.Provider, deployment.ConnectionId, deployment.VercelProjectId, deployment.Create, repoFullName, "deployment.", cancellationToken);
            if (prepared.IsFailure)
            {
                return prepared.Error!.Code == ErrorCodes.Validation
                    ? ApiProblems.Validation(prepared.Error!)
                    : Deployments.DeploymentSettingsService.Problem(prepared.Error!);
            }

            preparedDeployment = prepared.Value;
        }

        var created = await creator.CreateAsync(request, currentUser.RequiredUserId, currentUser.Actor, cancellationToken);
        if (created.IsFailure)
        {
            return created.Error!.Code == ErrorCodes.Validation
                ? ApiProblems.Validation(created.Error!)
                : await GitHubResults.ProblemAsync(created.Error!, apps, cancellationToken);
        }

        var project = created.Value;
        if (preparedDeployment is not null)
        {
            await deploymentSettings.ApplyAsync(project.Id, currentUser.RequiredUserId, preparedDeployment, bindConnection: true, currentUser.Actor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Created(
            $"/api/projects/{project.Id}",
            ProjectMapping.ToDto(project, ProjectRole.Owner, await presets.ForProjectAsync(project.Id, cancellationToken), preparedDeployment is not null));
    }

    private static async Task<Results<Ok<ProjectDto>, ProblemHttpResult>> GetProject(
        Guid id,
        IProjectAccess access,
        AideDbContext db,
        Stack.RetiredPresets presets,
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
            : TypedResults.Ok(ProjectMapping.ToDto(project, grant.Value.ProjectRole, await presets.ForProjectAsync(id, cancellationToken), await DeploymentsEnabledAsync(db, id, cancellationToken)));
    }

    private static async Task<Results<Ok<ProjectDto>, ValidationProblem, ProblemHttpResult>> UpdateProject(
        Guid id,
        UpdateProjectRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        Stack.RetiredPresets presets,
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

        return TypedResults.Ok(ProjectMapping.ToDto(project, grant.Value.ProjectRole, await presets.ForProjectAsync(id, cancellationToken), await DeploymentsEnabledAsync(db, id, cancellationToken)));
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

        // §4.10: the state is that of the current release version (in development, else the latest released).
        var versionId = project.CurrentReleaseVersionId ?? await db.CurrentReleaseVersionIdAsync(id, cancellationToken);
        var versionRows = await db.ReleaseVersions.AsNoTracking()
            .Where(v => v.ProjectId == id)
            .Select(v => new ReleaseVersionRow(v.Version, v.Status))
            .ToListAsync(cancellationToken);
        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == id && a.ReleaseVersionId == versionId)
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

        // Reports undone by a rollback are not in the current review timeline (§4.8).
        var report = await ReviewTimeline.Current(db, project)
            .NewestFirst()
            .Select(r => new { r.Score, r.Decision, r.DriftLevel })
            .FirstOrDefaultAsync(cancellationToken);
        var review = new ReviewStateDto(
            project.ReviewCycle,
            project.MaxReviewCycles + project.ExtraReviewCycles,
            report?.Score,
            report?.Decision,
            report?.DriftLevel,
            ReviewStatusRules.Status(
                await db.Jobs.AsNoTracking()
                    .Where(j => j.ProjectId == id && j.Type == JobTypes.Review)
                    .OrderByDescending(j => j.CreatedAt)
                    .ThenByDescending(j => j.Id)
                    .Select(j => (JobStatus?)j.Status)
                    .FirstOrDefaultAsync(cancellationToken),
                project.ReviewCycle));

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
            project.Checkpoint,
            versionId,
            project.ReleasedVersion,
            project.DevelopmentVersion,
            ReleaseVersionRules.SuggestNext(versionRows).ToString(),
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
                await Execution.ExecutionQueries.JobActiveAsync(db, id, cancellationToken)),
            await Git.PullRequestReader.SummaryAsync(db, id, cancellationToken),
            await db.Releases.AsNoTracking()
                .Where(r => r.ProjectId == id && r.ReleaseVersionId == versionId)
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken) is { } latestRelease
                ? ReleaseRecords.ToSummary(latestRelease, project.MergeStrategy)
                : null,
            LatestDeployment: await Deployments.DeploymentReader.LatestAsync(db, id, cancellationToken));
    }

    /// <summary>FR-88, §7.2: the project has deployment settings, so its navigation shows Deployments.</summary>
    internal static Task<bool> DeploymentsEnabledAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken) =>
        db.ProjectDeploymentSettings.AsNoTracking().AnyAsync(s => s.ProjectId == projectId, cancellationToken);

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
