using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Versions;
using Aide.Domain.Access;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Execution;

/// <summary>
/// A file a step declares it creates or modifies (FR-29). <c>protected</c> marks a protected path (§4.6: .git/, .aide/, CI
/// workflows, lockfiles, .env files) — only an Owner-edited plan can declare one; the step may then write it.
/// </summary>
public sealed record DeclaredFileDto(string Path, FileAction Action, bool Protected);

/// <summary>A Guardian violation recorded on a step's last rejected attempt (§4.6, FR-32). Never contains secret values.</summary>
public sealed record StepViolationDto(GuardianCheck Check, string Code, ViolationSeverity Severity, string Message, string? Path);

/// <summary>
/// One execution step of a plan version (FR-30–FR-34): the plan step (number, title, task, declared files, dependencies on
/// earlier step numbers, user instructions) and its progress — status, attempt, the last attempt's violations, and the
/// branch and commit once accepted. <c>commitUrl</c> links the commit on GitHub; it is <c>null</c> for the Local provider
/// (no remote) and before the step is committed.
/// </summary>
public sealed record ExecutionStepDto(
    Guid Id,
    int Number,
    string Title,
    string Task,
    IReadOnlyList<DeclaredFileDto> DeclaredFiles,
    IReadOnlyList<int> Dependencies,
    string? UserInstructions,
    StepStatus Status,
    int Attempt,
    IReadOnlyList<StepViolationDto>? Violations,
    string? BranchName,
    string? CommitSha,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<FileManifestEntryDto>? FileManifest,
    string? CommitUrl);

/// <summary>A file an accepted step wrote (FR-34), with the SHA-256 of its content.</summary>
public sealed record FileManifestEntryDto(string Path, FileAction Action, string ContentHash);

/// <summary>
/// The execution runner's state after a start/pause/resume request (FR-31, FR-37). <c>paused</c> is the project's pause flag;
/// <c>currentStepNumber</c> is the step <c>in_progress</c>, if any; <c>counts</c> are over the current plan version's steps.
/// </summary>
public sealed record ExecutionStateDto(
    ExecutionRunStatus Status,
    bool Paused,
    int? CurrentStepNumber,
    ExecutionCountsDto Counts);

/// <summary>The plan version the steps belong to.</summary>
public sealed record ExecutionPlanDto(Guid ArtifactId, ArtifactType Type, int Version, ArtifactStatus Status);

/// <summary>A plan version's execution steps in step order (FR-30); <c>plan</c> is <c>null</c> (and no steps) before any plan is approved.</summary>
public sealed record ExecutionStepsDto(ExecutionPlanDto? Plan, IReadOnlyList<ExecutionStepDto> Steps);

/// <summary>FR-30–FR-34: execution of a project's plan (§6 <c>/api/projects/:id/execution</c>).</summary>
public static class ExecutionEndpoints
{
    public static IEndpointRouteBuilder MapExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}/execution")
            .WithTags("Execution")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/steps", ListExecutionSteps)
            .WithName("ListExecutionSteps")
            .WithSummary("The execution steps of a plan version, in step order (members). Without planType and planVersion this is the current plan: the most recently locked plan that owns steps (the implementation plan, or the optimisation plan of a review cycle); `plan` names its type and version. With planType only, that type's highest locked version; with planVersion, that version of planType (default implementation_plan). With no locked plan the result has no plan and no steps.")
            .Produces<ExecutionStepsDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION: unknown planType, or planVersion below 1."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project, or the requested plan version."));

        group.MapPost("/start", StartExecution)
            .WithName("StartExecution")
            .WithSummary("Start execution (Contributor or Owner): prepares the working copy and project branch if needed, clears the pause flag and queues the next runnable step of the current plan (FR-31, FR-35). Returns 202 with the execution state; progress arrives over the live channel. 409 PHASE_PRECONDITION_FAILED, BUDGET_EXCEEDED, CONFLICT.")
            .WithExecutionControlResponses("PHASE_PRECONDITION_FAILED (not in execution, no locked plan, or nothing runnable), BUDGET_EXCEEDED (token budget used up) or CONFLICT (a concurrent change).");

        group.MapPost("/pause", PauseExecution)
            .WithName("PauseExecution")
            .WithSummary("Pause execution (Contributor or Owner): a running step finishes, but no new step starts until resumed (FR-37). Idempotent. Returns 202 with the execution state.")
            .WithExecutionControlResponses("PHASE_PRECONDITION_FAILED (not in execution) or CONFLICT (a concurrent change).");

        group.MapPost("/resume", ResumeExecution)
            .WithName("ResumeExecution")
            .WithSummary("Resume paused execution (Contributor or Owner): clears the pause flag and queues the next runnable step (FR-37). Returns 202 with the execution state.")
            .WithExecutionControlResponses("PHASE_PRECONDITION_FAILED (not in execution), BUDGET_EXCEEDED (token budget used up) or CONFLICT (a concurrent change).");

        return app;
    }

    private static async Task<Results<Ok<ExecutionStepsDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> ListExecutionSteps(
        Guid id,
        IProjectAccess access,
        AideDbContext db,
        CancellationToken cancellationToken,
        [Range(1, int.MaxValue)] int? planVersion = null,
        [WireEnum(typeof(ArtifactType))] string? planType = null,
        Guid? releaseVersionId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var scope = await ReleaseVersionScope.ResolveAsync(db, id, releaseVersionId, cancellationToken);
        if (scope.IsFailure)
        {
            return ApiProblems.FromError(scope.Error!);
        }

        if (planType is null && planVersion is null && releaseVersionId is null)
        {
            // The current plan (FR-30, FR-40): the newest locked plan of either type, as the execution runner sees it.
            var currentId = await ExecutionQueries.CurrentPlanIdAsync(db, id, cancellationToken);
            var current = currentId is { } cid ? await db.Artifacts.AsNoTracking().FirstAsync(a => a.Id == cid, cancellationToken) : null;
            return current is null
                ? TypedResults.Ok(new ExecutionStepsDto(null, []))
                : TypedResults.Ok(await StepsAsync(db, id, current, cancellationToken));
        }

        var type = planType is null ? ArtifactType.ImplementationPlan : EnumStrings.Parse<ArtifactType>(planType);
        if (type is not (ArtifactType.ImplementationPlan or ArtifactType.OptimizationPlan))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["planType"] = ["Steps belong to implementation_plan or optimization_plan versions."] });
        }

        // A plan version number names one plan (numbers are per project); without one, the latest locked plan of the release
        // version (§6 Notes: default the version in development, else the latest released).
        var versionId = scope.Value;
        var plans = db.Artifacts.AsNoTracking().Where(a => a.ProjectId == id && a.Type == type && (planVersion != null || a.ReleaseVersionId == versionId));
        var plan = planVersion is { } version
            ? await plans.FirstOrDefaultAsync(a => a.Version == version, cancellationToken)
            : await plans.Where(a => a.Status == ArtifactStatus.Locked).OrderByDescending(a => a.Version).FirstOrDefaultAsync(cancellationToken);
        if (plan is null)
        {
            return planVersion is null
                ? TypedResults.Ok(new ExecutionStepsDto(null, []))
                : ApiProblems.NotFound($"Version {planVersion} of the {type.ToWire()} was not found.");
        }

        return TypedResults.Ok(await StepsAsync(db, id, plan, cancellationToken));
    }

    private static async Task<ExecutionStepsDto> StepsAsync(AideDbContext db, Guid id, Artifact plan, CancellationToken cancellationToken)
    {
        var steps = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ProjectId == id && s.PlanArtifactId == plan.Id)
            .OrderBy(s => s.StepNumber)
            .ToListAsync(cancellationToken);
        var git = await db.ProjectGitSettings.AsNoTracking()
            .Where(g => g.ProjectId == id)
            .Select(g => new { g.Provider, g.RepoUrl })
            .FirstOrDefaultAsync(cancellationToken);
        return new ExecutionStepsDto(
            new ExecutionPlanDto(plan.Id, plan.Type, plan.Version, plan.Status),
            [.. steps.Select(s => ToDto(s, git is null ? null : ExecutionQueries.CommitUrl(git.Provider, git.RepoUrl, s.CommitSha)))]);
    }

    private static RouteHandlerBuilder WithExecutionControlResponses(this RouteHandlerBuilder builder, string conflicts) => builder
        .Produces<ExecutionStateDto>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithResponseDescriptions(
            (StatusCodes.Status403Forbidden, "FORBIDDEN: Viewers can't control execution."),
            (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
            (StatusCodes.Status409Conflict, conflicts));

    private static Task<Results<Accepted<ExecutionStateDto>, ProblemHttpResult, StatusCodeHttpResult>> StartExecution(
        Guid id, IProjectAccess access, ICurrentUser currentUser, ExecutionService execution, CancellationToken cancellationToken) =>
        ControlAsync(id, access, (actor, ct) => execution.StartAsync(id, actor, ct), currentUser, cancellationToken);

    private static Task<Results<Accepted<ExecutionStateDto>, ProblemHttpResult, StatusCodeHttpResult>> PauseExecution(
        Guid id, IProjectAccess access, ICurrentUser currentUser, ExecutionService execution, CancellationToken cancellationToken) =>
        ControlAsync(id, access, (actor, ct) => execution.PauseAsync(id, actor, ct), currentUser, cancellationToken);

    private static Task<Results<Accepted<ExecutionStateDto>, ProblemHttpResult, StatusCodeHttpResult>> ResumeExecution(
        Guid id, IProjectAccess access, ICurrentUser currentUser, ExecutionService execution, CancellationToken cancellationToken) =>
        ControlAsync(id, access, (actor, ct) => execution.ResumeAsync(id, actor, ct), currentUser, cancellationToken);

    /// <summary>Writers only (§2 ControlExecution); the service decides and writes; 202 with the runner's state.</summary>
    private static async Task<Results<Accepted<ExecutionStateDto>, ProblemHttpResult, StatusCodeHttpResult>> ControlAsync(
        Guid id,
        IProjectAccess access,
        Func<string, CancellationToken, Task<Result<ExecutionStateDto>>> action,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ControlExecution, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var result = await action(currentUser.Actor, cancellationToken);
        return result.IsSuccess
            ? TypedResults.Accepted($"/api/projects/{id}/execution/steps", result.Value)
            : ApiProblems.FromError(result.Error!);
    }

    private static ExecutionStepDto ToDto(ExecutionStep s, string? commitUrl) => new(
        s.Id,
        s.StepNumber,
        s.Title,
        s.Task,
        [.. s.DeclaredFiles.Select(f => new DeclaredFileDto(f.Path, f.Action, PathRules.IsProtected(f.Path)))],
        s.Dependencies,
        s.UserInstructions,
        s.Status,
        s.Attempt,
        s.Violations is null ? null : [.. s.Violations.Select(v => new StepViolationDto(v.Check, v.Code, v.Severity, v.Message, v.Path))],
        s.BranchName,
        s.CommitSha,
        s.StartedAt,
        s.CompletedAt,
        s.FileManifest is null ? null : [.. s.FileManifest.Select(f => new FileManifestEntryDto(f.Path, f.Action, f.ContentHash))],
        commitUrl);
}
