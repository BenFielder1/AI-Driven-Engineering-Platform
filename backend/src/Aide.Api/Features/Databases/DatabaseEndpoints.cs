using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Databases;

/// <summary>
/// FR-152, FR-153, §6: a project's managed databases (<c>/api/projects/:id/settings/deployment/databases/:environment</c>,
/// Owner) and its Supabase BaaS migration runs (<c>/api/projects/:id/migrations</c>, any member). Secrets are never returned.
/// </summary>
public static class DatabaseEndpoints
{
    public static IEndpointRouteBuilder MapDatabaseEndpoints(this IEndpointRouteBuilder app)
    {
        var databases = app.MapGroup("/api/projects/{id:guid}/settings/deployment/databases")
            .WithTags("Deployments")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        databases.MapPut("/{environment}", PutManagedDatabase)
            .WithName("PutManagedDatabase")
            .WithSummary("Set the environment's database (Owner, FR-152): provider (the composition's database host; fake in Fake mode), one of the caller's own connections, and one of create {region, plan, name?}, link {resourceId} or external {connectionString} (External only, stored encrypted). Answers 202 with the database card (provisioning); a database_provisioning job creates or links it and polls until available (10 s, 10-minute ceiling), then derived env vars are synced. Job failures (DATABASE_PROVISIONING_FAILED, DATABASE_PROVISIONING_TIMEOUT, PLATFORM_PLAN_LIMIT, CREDENTIAL_INVALID, CONNECTION_SCOPE_INSUFFICIENT, UPSTREAM_ERROR) are on the card's provisioning.")
            .Produces<DeploymentDatabaseDto>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION (body, environment) or STACK_INCOMPATIBLE (the composition's database host doesn't allow this provider)"),
                (StatusCodes.Status403Forbidden, "FORBIDDEN (not an Owner) or CREDENTIAL_NOT_OWNER (another user's connection)"),
                (StatusCodes.Status404NotFound, "NOT_FOUND (project or connection)"),
                (StatusCodes.Status409Conflict, "CONFLICT (changed concurrently)"));

        databases.MapDelete("/{environment}", DeleteManagedDatabase)
            .WithName("DeleteManagedDatabase")
            .WithSummary("Unlink the environment's database (Owner, FR-152). The platform resource is never deleted; migration runs stay; derived env vars are recomputed and synced.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status404NotFound, "NOT_FOUND (project, or no database for the environment)"),
                (StatusCodes.Status409Conflict, "CONFLICT (changed concurrently, e.g. by the provisioning job)"));

        databases.MapPost("/{environment}/restore", RestoreManagedDatabase)
            .WithName("RestoreManagedDatabase")
            .WithSummary("Restore a paused Supabase project (Owner, FR-152, §4.21 \"Paused\"): POST /projects/{ref}/restore, then a database_provisioning job polls until it is available. Answers 202 with the card (provisioning).")
            .Produces<DeploymentDatabaseDto>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION (environment) or CREDENTIAL_INVALID (the bound token was rejected)"),
                (StatusCodes.Status409Conflict, "DEPLOYMENT_ACTION_UNSUPPORTED (not Supabase), CONFLICT (not paused, or changed concurrently) or DEPLOYMENT_NOT_CONFIGURED (the connection is gone)"),
                (StatusCodes.Status429TooManyRequests, "UPSTREAM_RATE_LIMITED (Supabase's 429; Retry-After)"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR"));

        var migrations = app.MapGroup("/api/projects/{id:guid}/migrations")
            .WithTags("Deployments")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        migrations.MapGet("/", ListMigrationRuns)
            .WithName("ListMigrationRuns")
            .WithSummary("The project's Supabase BaaS migration runs, newest first (any member, FR-153, §7 Migrations): environment, commit, files with durations, status, error and advisor findings; ?releaseVersionId filters to one version.")
            .Produces<IReadOnlyList<MigrationRunDto>>();

        migrations.MapGet("/{runId:guid}", GetMigrationRun)
            .WithName("GetMigrationRun")
            .WithSummary("One migration run (any member, FR-153).")
            .Produces<MigrationRunDto>();
        return app;
    }

    private static async Task<Results<Accepted<DeploymentDatabaseDto>, ValidationProblem, ProblemHttpResult>> PutManagedDatabase(
        Guid id,
        string environment,
        PutManagedDatabaseRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        ManagedDatabaseService databases,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        if (!EnumStrings.TryParse<DeploymentEnvironment>(environment, out var parsed))
        {
            return InvalidEnvironment();
        }

        var (card, error) = await databases.PutAsync(id, parsed, request, grant.Value.UserId, grant.Value.SystemRole == SystemRole.Admin, currentUser.Actor, cancellationToken);
        if (error is not null)
        {
            return error.Code == ErrorCodes.Validation ? ApiProblems.Validation(error) : ApiProblems.FromError(error);
        }

        return TypedResults.Accepted($"/api/projects/{id}/settings/deployment", card!);
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> DeleteManagedDatabase(
        Guid id, string environment, IProjectAccess access, ICurrentUser currentUser, ManagedDatabaseService databases, CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        if (!EnumStrings.TryParse<DeploymentEnvironment>(environment, out var parsed))
        {
            return InvalidEnvironment();
        }

        return await databases.DeleteAsync(id, parsed, currentUser.Actor, cancellationToken) is { } error ? ApiProblems.FromError(error) : TypedResults.NoContent();
    }

    private static async Task<Results<Accepted<DeploymentDatabaseDto>, ValidationProblem, ProblemHttpResult>> RestoreManagedDatabase(
        Guid id, string environment, IProjectAccess access, ICurrentUser currentUser, ManagedDatabaseService databases, CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        if (!EnumStrings.TryParse<DeploymentEnvironment>(environment, out var parsed))
        {
            return InvalidEnvironment();
        }

        var (card, error) = await databases.RestoreAsync(id, parsed, currentUser.Actor, cancellationToken);
        return error is not null ? ApiProblems.FromError(error) : TypedResults.Accepted($"/api/projects/{id}/settings/deployment", card!);
    }

    private static async Task<Results<Ok<IReadOnlyList<MigrationRunDto>>, ProblemHttpResult>> ListMigrationRuns(
        Guid id, Guid? releaseVersionId, IProjectAccess access, MigrationRunReader runs, CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        return grant.IsFailure ? ApiProblems.FromError(grant.Error!) : TypedResults.Ok(await runs.ListAsync(id, releaseVersionId, cancellationToken));
    }

    private static async Task<Results<Ok<MigrationRunDto>, ProblemHttpResult>> GetMigrationRun(
        Guid id, Guid runId, IProjectAccess access, MigrationRunReader runs, CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        return await runs.GetAsync(id, runId, cancellationToken) is { } run ? TypedResults.Ok(run) : ApiProblems.NotFound("The migration run was not found.");
    }

    private static ValidationProblem InvalidEnvironment() =>
        ApiProblems.Validation(new Dictionary<string, string[]> { ["environment"] = ["The environment is production or preview."] });
}
