using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Projects;
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
/// branch and commit once accepted.
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
    DateTimeOffset? CompletedAt);

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
            .WithSummary("The execution steps of a plan version, in step order (members). planType defaults to implementation_plan and planVersion to its current (highest locked) version; with no locked plan the result has no plan and no steps.")
            .Produces<ExecutionStepsDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION: unknown planType, or planVersion below 1."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project, or the requested plan version."));

        return app;
    }

    private static async Task<Results<Ok<ExecutionStepsDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> ListExecutionSteps(
        Guid id,
        IProjectAccess access,
        AideDbContext db,
        CancellationToken cancellationToken,
        [Range(1, int.MaxValue)] int? planVersion = null,
        [WireEnum(typeof(ArtifactType))] string? planType = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var type = planType is null ? ArtifactType.ImplementationPlan : EnumStrings.Parse<ArtifactType>(planType);
        if (type is not (ArtifactType.ImplementationPlan or ArtifactType.OptimizationPlan))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["planType"] = ["Steps belong to implementation_plan or optimization_plan versions."] });
        }

        var plans = db.Artifacts.AsNoTracking().Where(a => a.ProjectId == id && a.Type == type);
        var plan = planVersion is { } version
            ? await plans.FirstOrDefaultAsync(a => a.Version == version, cancellationToken)
            : await plans.Where(a => a.Status == ArtifactStatus.Locked).OrderByDescending(a => a.Version).FirstOrDefaultAsync(cancellationToken);
        if (plan is null)
        {
            return planVersion is null
                ? TypedResults.Ok(new ExecutionStepsDto(null, []))
                : ApiProblems.NotFound($"Version {planVersion} of the {type.ToWire()} was not found.");
        }

        var steps = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ProjectId == id && s.PlanArtifactId == plan.Id)
            .OrderBy(s => s.StepNumber)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new ExecutionStepsDto(
            new ExecutionPlanDto(plan.Id, plan.Type, plan.Version, plan.Status),
            [.. steps.Select(ToDto)]));
    }

    private static ExecutionStepDto ToDto(ExecutionStep s) => new(
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
        s.CompletedAt);
}
