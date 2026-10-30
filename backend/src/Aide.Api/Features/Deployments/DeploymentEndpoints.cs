using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// FR-88, FR-90, FR-91, FR-143–FR-146, §4.15, §4.20: a project's Deployment settings — targets, readiness, env inventory
/// (§6 <c>/api/projects/:id/settings/deployment</c>)
/// and its deployments (<c>/api/projects/:id/deployments</c>). Members read settings and deployments; writers trigger a
/// preview (and redeploy one); Owners edit settings and env vars, promote and redeploy production. Env values and tokens
/// are never returned or logged. Builds and polls run in the <c>deployment</c> job (§4.15); settings, env writes and
/// promote talk to Vercel on save.
/// </summary>
public static class DeploymentEndpoints
{
    public static IEndpointRouteBuilder MapDeploymentEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/projects/{id:guid}/settings/deployment")
            .WithTags("Deployments")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        settings.MapGet("/", GetDeploymentSettings)
            .WithName("GetDeploymentSettings")
            .WithSummary("Settings → Deployment for any member (FR-144–FR-146; D-V4-4 reshaped it): one target card per component the composition hosts (provider, binding with \"connected by\", resource, root directory, derived build config, URLs, status, setup job progress, capabilities, readiness), database cards (not_configured until M30), the environment inventory (keys only) and the origin model.")
            .Produces<DeploymentSettingsDto>();

        settings.MapPut("/targets/{component}", PutDeploymentTarget)
            .WithName("PutDeploymentTarget")
            .WithSummary("Set up the component's target (Owner, FR-143, FR-144): bind one of the caller's own connections of the composition's host and create (create {name, region?, plan?}) or link (link {resourceId}) the platform resource. Answers 202 with the target (configuring); a deployment_setup job does the platform work and its progress is on the target. GitHub Pages needs no connection and enables Pages on the GitHub repository.")
            .Produces<DeploymentTargetDto>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION (body, component, or a Local Git project outside Fake mode) or STACK_INCOMPATIBLE (the composition doesn't host the component on this provider; GitHub Pages without a GitHub repository)"),
                (StatusCodes.Status403Forbidden, "FORBIDDEN (not an Owner) or CREDENTIAL_NOT_OWNER (another user's connection)"),
                (StatusCodes.Status404NotFound, "NOT_FOUND (project or connection)"),
                (StatusCodes.Status409Conflict, "PLATFORM_PERMISSION_MISSING (GitHub Pages: the AIDE GitHub App lacks Pages; extensions platform, installUrl = the App's page), PLATFORM_GIT_NOT_CONNECTED, PLATFORM_PLAN_LIMIT (extension platform; also reported by the setup job on the target) or CONFLICT"),
                (StatusCodes.Status502BadGateway, "PLATFORM_ERROR (the provider isn't supported for real yet; extension platform) or UPSTREAM_ERROR (GitHub couldn't mint a token)"));

        settings.MapDelete("/targets/{component}", DeleteDeploymentTarget)
            .WithName("DeleteDeploymentTarget")
            .WithSummary("Unbind and clear the component's target (Owner, FR-144). The platform resource is never deleted; deployments and env vars stay as history; derived env vars are recomputed.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem();

        settings.MapPost("/targets/{component}/verify", VerifyDeploymentTarget)
            .WithName("VerifyDeploymentTarget")
            .WithSummary("Run the readiness checklist of the component's target with the platform (Owner, FR-144): connection, git_access, required_env, branch_control, preview_environment, plan_notes; each ok, warning, error or na with a message and an optional fix link. A ready target with an error item becomes attention.")
            .Produces<DeploymentVerifyDto>()
            .ProducesValidationProblem();

        settings.MapGet("/env", ListDeploymentEnv)
            .WithName("ListDeploymentEnv")
            .WithSummary("The environment inventory (Owner, FR-145): user, derived and required variables with appliesTo, environments, sensitive, public, source, derivedFrom, hasValue and the sync state. Values are never returned.")
            .Produces<IReadOnlyList<DeploymentEnvVarDto>>();

        settings.MapPut("/env/{key}", PutDeploymentEnv)
            .WithName("PutDeploymentEnv")
            .WithSummary("Create or replace a user environment variable (Owner, FR-145): key ^[A-Za-z_][A-Za-z0-9_]*$ (up to 256), value up to 64 KB, targets preview and/or production, sensitive, appliesTo (frontend, backend; default frontend) and public (default: the key has the frontend's public prefix). VALIDATION for a derived key or a public value that looks secret (\"secret values can't be public\"). The value is encrypted and synced to the targets of its components; a failed sync keeps the value with syncError and answers 502 UPSTREAM_ERROR.")
            .Produces<DeploymentEnvVarDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION (including a derived key, or secret values can't be public)"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR (saved in AIDE with syncError; a platform failed or couldn't be reached)"));

        settings.MapDelete("/env/{key}", DeleteDeploymentEnv)
            .WithName("DeleteDeploymentEnv")
            .WithSummary("Delete a user environment variable in AIDE and on its targets (Owner, FR-145). If a platform can't delete it, nothing is deleted (502 UPSTREAM_ERROR). Derived variables can't be deleted (VALIDATION).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status502BadGateway);

        var deployments = app.MapGroup("/api/projects/{id:guid}/deployments")
            .WithTags("Deployments")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        deployments.MapGet("/", ListDeployments)
            .WithName("ListDeployments")
            .WithSummary("The project's deployments, newest first (members, FR-91): target, version, SHA, state, URL, time and who triggered it. Filter by target (preview, production) and releaseVersionId.")
            .Produces<IReadOnlyList<DeploymentDto>>()
            .ProducesValidationProblem();

        deployments.MapPost("/preview", CreatePreviewDeployment)
            .WithName("CreatePreviewDeployment")
            .WithSummary("Deploy the head of the version branch as a preview now (writer, FR-90); AIDE polls it (§4.15). 409 DEPLOYMENT_NOT_CONFIGURED without deployment settings.")
            .Produces<DeploymentDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status409Conflict, "DEPLOYMENT_NOT_CONFIGURED (no deployment settings or no bound connection) or DEPLOYMENT_ACTION_UNSUPPORTED (the frontend's provider has no previews, e.g. GitHub Pages; extension platform)"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR (Vercel failed or couldn't be reached)"));

        deployments.MapPost("/{deploymentId:guid}/promote", PromoteDeployment)
            .WithName("PromoteDeployment")
            .WithSummary("Promote an earlier ready production deployment (Owner, FR-91): a production rollback that leaves Git alone. Records a new promote row. 409 INVALID_DEPLOYMENT when the deployment isn't a ready production deployment.")
            .Produces<DeploymentDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status409Conflict, "INVALID_DEPLOYMENT (not a ready production deployment), DEPLOYMENT_ACTION_UNSUPPORTED (the provider has no rollback, e.g. GitHub Pages; extension platform) or DEPLOYMENT_NOT_CONFIGURED"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR (Vercel failed or couldn't be reached)"));

        deployments.MapPost("/{deploymentId:guid}/redeploy", RedeployDeployment)
            .WithName("RedeployDeployment")
            .WithSummary("Deploy the same commit again as a new row (FR-90, FR-91): a preview needs a writer, production an Owner. AIDE polls it (§4.15).")
            .Produces<DeploymentDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status403Forbidden, "FORBIDDEN (a Viewer, or a Contributor redeploying production)"),
                (StatusCodes.Status409Conflict, "DEPLOYMENT_NOT_CONFIGURED"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR (Vercel failed or couldn't be reached)"));

        return app;
    }

    private static async Task<Results<Ok<DeploymentSettingsDto>, ProblemHttpResult>> GetDeploymentSettings(
        Guid id, IProjectAccess access, DeploymentTargetService targets, CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        return grant.IsFailure ? ApiProblems.FromError(grant.Error!) : TypedResults.Ok(await targets.GetAsync(id, grant.Value.ProjectRole, cancellationToken));
    }

    private static async Task<Results<Accepted<DeploymentTargetDto>, ValidationProblem, ProblemHttpResult>> PutDeploymentTarget(
        Guid id,
        string component,
        PutDeploymentTargetRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        DeploymentTargetService targets,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        if (!DeploymentPlatforms.TryParseComponent(component, out var parsed))
        {
            return InvalidComponent();
        }

        var (target, failure) = await targets.PutAsync(id, parsed, request, grant.Value.UserId, grant.Value.SystemRole == SystemRole.Admin, currentUser.Actor, cancellationToken);
        if (failure is not null)
        {
            // PLATFORM_* problems carry the platform (and install link); STACK_INCOMPATIBLE keeps its violations.
            return failure.Error.Code == ErrorCodes.Validation ? ApiProblems.Validation(failure.Error)
                : failure.Error.Code.StartsWith("PLATFORM_", StringComparison.Ordinal) ? ApiProblems.Platform(failure.Error, failure.Platform)
                : ApiProblems.FromError(failure.Error);
        }

        return TypedResults.Accepted($"/api/projects/{id}/settings/deployment", target!);
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> DeleteDeploymentTarget(
        Guid id, string component, IProjectAccess access, ICurrentUser currentUser, DeploymentTargetService targets, CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        if (!DeploymentPlatforms.TryParseComponent(component, out var parsed))
        {
            return InvalidComponent();
        }

        return await targets.DeleteAsync(id, parsed, currentUser.Actor, cancellationToken) is { } error ? ApiProblems.FromError(error) : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<DeploymentVerifyDto>, ValidationProblem, ProblemHttpResult>> VerifyDeploymentTarget(
        Guid id, string component, IProjectAccess access, ICurrentUser currentUser, DeploymentTargetService targets, CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        if (!DeploymentPlatforms.TryParseComponent(component, out var parsed))
        {
            return InvalidComponent();
        }

        var (result, error) = await targets.VerifyAsync(id, parsed, grant.Value.ProjectRole, currentUser.Actor, cancellationToken);
        return error is not null ? ApiProblems.FromError(error) : TypedResults.Ok(result!);
    }

    private static async Task<Results<Ok<IReadOnlyList<DeploymentEnvVarDto>>, ProblemHttpResult>> ListDeploymentEnv(
        Guid id, IProjectAccess access, DeploymentInventoryService inventory, CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var rows = await inventory.RowsAsync(id, await inventory.FactsAsync(id, cancellationToken), cancellationToken);
        return TypedResults.Ok<IReadOnlyList<DeploymentEnvVarDto>>([.. rows.Select(DeploymentInventoryService.ToDto)]);
    }

    private static ValidationProblem InvalidComponent() =>
        ApiProblems.Validation(new Dictionary<string, string[]> { ["component"] = ["The component is frontend or backend."] });

    private static async Task<Results<Ok<DeploymentEnvVarDto>, ValidationProblem, ProblemHttpResult>> PutDeploymentEnv(
        Guid id,
        string key,
        PutDeploymentEnvRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        DeploymentAccess deployments,
        DeploymentInventoryService inventory,
        DeploymentEnvSync envSync,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var facts = await inventory.FactsAsync(id, cancellationToken);
        var isPublic = EnvInventoryRules.IsPublic(key, request.Public, facts.PublicPrefix);
        var validation = EnvInventoryRules.ValidateUser(key, request.Value, request.Targets, request.AppliesTo, isPublic, DerivedEnvRules.DerivedKeys(facts.DerivedInput));
        if (validation.IsFailure)
        {
            return ApiProblems.Validation(validation.Error!);
        }

        var now = Now(time);
        var variable = await db.DeploymentEnvVars.FirstOrDefaultAsync(v => v.ProjectId == id && v.Key == key, cancellationToken);
        if (variable is null)
        {
            variable = new DeploymentEnvVar { ProjectId = id, Key = key };
            db.DeploymentEnvVars.Add(variable);
        }

        variable.Targets = [.. request.Targets.Distinct().OrderBy(t => t).Select(t => t.ToWire())];
        variable.AppliesTo = [.. (request.AppliesTo ?? [DeploymentComponent.Frontend]).Distinct().Order().Select(c => c.ToWire())];
        variable.Public = isPublic;
        variable.Source = DeploymentEnvSource.User;
        variable.DerivedFrom = null;
        variable.ValueEncrypted = deployments.Values.Protect(request.Value);
        variable.Sensitive = request.Sensitive;
        variable.UpdatedAt = now;
        variable.SyncedAt = null;
        variable.SyncError = null;
        db.AddEvent(
            EventTypes.SettingsUpdated,
            id,
            new { projectId = id, section = "deployment_env", changes = new[] { key }, targets = variable.Targets, appliesTo = variable.AppliesTo, sensitive = variable.Sensitive, @public = variable.Public },
            currentUser.Actor,
            now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ApiProblems.Of(ErrorCodes.Conflict, "The variable was changed by someone else. Reload and try again.");
        }

        // FR-145: synced to the targets of its components on save; a failed sync keeps the value with syncError.
        var failure = await envSync.PushToTargetsAsync(id, [variable], currentUser.Actor, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return failure is null
            ? TypedResults.Ok(DeploymentInventoryService.ToDto(variable))
            : ApiProblems.Of(ErrorCodes.UpstreamError, $"Saved in AIDE, but a deployment platform wasn't updated: {failure} It is pushed again on the next save or deployment.");
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> DeleteDeploymentEnv(
        Guid id,
        string key,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        DeploymentAccess deployments,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        if (!DeploymentEnvRules.IsKey(key))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["key"] = ["Not an environment variable key."] });
        }

        var variable = await db.DeploymentEnvVars.FirstOrDefaultAsync(v => v.ProjectId == id && v.Key == key, cancellationToken);
        if (variable is null)
        {
            return ApiProblems.NotFound("The environment variable was not found.");
        }

        if (variable.Source == DeploymentEnvSource.Derived)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["key"] = [$"AIDE derives {key}; it changes with the deployment targets."] });
        }

        // FR-88, FR-145: deleting in AIDE deletes on every target; if a platform can't, nothing is deleted (all or nothing).
        foreach (var component in new[] { DeploymentComponent.Frontend, DeploymentComponent.Backend })
        {
            var ids = EnvSyncRules.AllIds(DeploymentEnvSync.ScopedIds(variable.VercelEnvIds, component));
            if (ids.Count == 0)
            {
                continue;
            }

            var context = await deployments.ResolveAsync(id, cancellationToken, component: component);
            if (context.IsFailure)
            {
                return ApiProblems.Of(ErrorCodes.UpstreamError, $"The variable is on the {component.ToWire()} target and can't be deleted there: {context.Error!.Message}");
            }

            try
            {
                await context.Value.Provider.SyncEnvAsync(context.Value.Auth, context.Value.TargetRef, new DeploymentEnvSyncRequest([], ids), cancellationToken);
            }
            catch (DeploymentProviderException ex)
            {
                if (ex.Failure == DeploymentFailure.CredentialRejected)
                {
                    await deployments.MarkInvalidAsync(context.Value.Connection?.Id, id, currentUser.Actor, Now(time), cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                }

                return ApiProblems.Of(ErrorCodes.UpstreamError, $"Nothing was deleted: {DeploymentSettingsService.Message(ex)}");
            }
        }

        db.DeploymentEnvVars.Remove(variable);
        db.AddEvent(EventTypes.SettingsUpdated, id, new { projectId = id, section = "deployment_env", changes = new[] { key }, deleted = true }, currentUser.Actor, Now(time));
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<IReadOnlyList<DeploymentDto>>, ProblemHttpResult>> ListDeployments(
        Guid id,
        IProjectAccess access,
        AideDbContext db,
        CancellationToken cancellationToken,
        [WireEnum(typeof(DeploymentEnvironment))] string? target = null,
        Guid? releaseVersionId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        DeploymentEnvironment? filter = target is not null && EnumStrings.TryParse<DeploymentEnvironment>(target, out var parsed) ? parsed : null;
        return TypedResults.Ok(await DeploymentReader.ListAsync(db, id, filter, releaseVersionId, cancellationToken));
    }

    private static async Task<Results<Accepted<DeploymentDto>, ProblemHttpResult>> CreatePreviewDeployment(
        Guid id,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        DeploymentScheduler scheduler,
        WorkingCopyPreparer preparer,
        IWorkingCopies workingCopies,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.TriggerDeployment, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        if (!await DeploymentScheduler.ConfiguredAsync(db, id, cancellationToken))
        {
            return NotConfigured();
        }

        // §4.20: a provider without previews (GitHub Pages) can't deploy one, as promote checks rollback.
        var provider = await db.DeploymentTargets.AsNoTracking()
            .Where(t => t.ProjectId == id && t.Component == DeploymentComponent.Frontend)
            .Select(t => (DeploymentProvider?)t.Provider)
            .FirstOrDefaultAsync(cancellationToken);
        if (provider is { } platform && !DeploymentPlatforms.Capabilities(platform).Previews)
        {
            return ApiProblems.Platform(
                new DomainError(ErrorCodes.DeploymentActionUnsupported, $"{DeploymentPlatforms.DisplayName(platform)} doesn't support this action."), platform);
        }

        var version = await db.CurrentReleaseVersionAsync(id, cancellationToken);
        if (version?.BranchName is not { } branch)
        {
            return ApiProblems.Of(ErrorCodes.Conflict, "Nothing to deploy yet: the version branch is created when execution starts.");
        }

        var sha = await HeadAsync(workingCopies, id, branch, cancellationToken);
        if (sha is null && (await preparer.TryPrepareAsync(id, cancellationToken)).Result.IsSuccess)
        {
            sha = await HeadAsync(workingCopies, id, branch, cancellationToken);
        }

        if (sha is null)
        {
            return ApiProblems.Of(ErrorCodes.Conflict, "The version branch can't be read yet; try again once execution has committed a step.");
        }

        var deployment = await scheduler.AddAsync(
            new NewDeployment(id, version.Id, null, DeploymentEnvironment.Preview, branch, sha, DeploymentTrigger.Manual, grant.Value.UserId),
            currentUser.Actor,
            Now(time),
            cancellationToken);
        return await AcceptedAsync(db, id, deployment, cancellationToken);
    }

    private static async Task<Results<Accepted<DeploymentDto>, ProblemHttpResult>> PromoteDeployment(
        Guid id,
        Guid deploymentId,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        DeploymentAccess deployments,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var source = await FindAsync(db, id, deploymentId, cancellationToken);
        if (source is null)
        {
            return DeploymentNotFound();
        }

        var promotable = DeploymentActionRules.CanPromote(source.Environment, source.Status);
        if (promotable.IsFailure)
        {
            return ApiProblems.FromError(promotable.Error!);
        }

        var context = await deployments.ResolveAsync(id, cancellationToken, component: source.Component);
        if (context.IsFailure)
        {
            return context.Error!.Code == ErrorCodes.DeploymentNotConfigured ? ApiProblems.FromError(context.Error!) : ApiProblems.Of(ErrorCodes.UpstreamError, context.Error!.Message);
        }

        // FR-150: only where the provider can make an earlier deployment current (GitHub Pages can't).
        if (!context.Value.Provider.Capabilities.Rollback)
        {
            return ApiProblems.Platform(
                new DomainError(ErrorCodes.DeploymentActionUnsupported, $"{DeploymentPlatforms.DisplayName(context.Value.Target.Provider)} doesn't support this action."),
                context.Value.Target.Provider);
        }

        var now = Now(time);
        try
        {
            // FR-91: Vercel re-points production at the earlier build (no rebuild); Git is left alone.
            await context.Value.Provider.RollbackAsync(context.Value.Auth, context.Value.TargetRef, source.ProviderDeploymentId, cancellationToken);
        }
        catch (DeploymentProviderException ex)
        {
            if (ex.Failure == DeploymentFailure.CredentialRejected)
            {
                await deployments.MarkInvalidAsync(context.Value.Connection?.Id, id, currentUser.Actor, now, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return ex.Failure == DeploymentFailure.NotFound
                ? ApiProblems.Of(ErrorCodes.InvalidDeployment, "The platform no longer has this deployment, so it can't be promoted.")
                : ApiProblems.Of(ErrorCodes.UpstreamError, DeploymentSettingsService.Message(ex));
        }

        var promoted = new Deployment
        {
            ProjectId = id,
            ReleaseVersionId = source.ReleaseVersionId,
            ReleaseId = source.ReleaseId,
            Provider = source.Provider,
            TargetId = source.TargetId,
            Component = source.Component,
            Kind = DeploymentKind.Promote,
            Environment = DeploymentEnvironment.Production,
            ProviderDeploymentId = source.ProviderDeploymentId,
            Url = source.Url,
            InspectorUrl = source.InspectorUrl,
            GitRef = source.GitRef,
            GitSha = source.GitSha,
            Status = DeploymentStatus.Ready,
            Trigger = DeploymentTrigger.Promote,
            TriggeredBy = grant.Value.UserId,
            PromotedFromDeploymentId = source.Id,
            CreatedAt = now,
            ReadyAt = now,
            UpdatedAt = now,
        };
        db.Deployments.Add(promoted);
        DeploymentEvents.Created(db, promoted, await DeploymentScheduler.VersionTextAsync(db, source.ReleaseVersionId, cancellationToken), currentUser.Actor, now);
        return await AcceptedAsync(db, id, promoted, cancellationToken);
    }

    private static async Task<Results<Accepted<DeploymentDto>, ProblemHttpResult>> RedeployDeployment(
        Guid id,
        Guid deploymentId,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        DeploymentScheduler scheduler,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        // Writers first (non-members get 404 before the deployment is looked up), then Owners for production.
        var grant = await access.CheckAsync(id, ProjectAction.TriggerDeployment, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var deployment = await FindAsync(db, id, deploymentId, cancellationToken);
        if (deployment is null)
        {
            return DeploymentNotFound();
        }

        var action = DeploymentActionRules.RedeployAction(deployment.Environment);
        if (action != ProjectAction.TriggerDeployment)
        {
            var owner = await access.CheckAsync(id, action, cancellationToken);
            if (owner.IsFailure)
            {
                return ApiProblems.FromError(owner.Error!);
            }
        }

        if (!await DeploymentScheduler.ConfiguredAsync(db, id, cancellationToken))
        {
            return NotConfigured();
        }

        var redeploy = await scheduler.AddAsync(
            new NewDeployment(id, deployment.ReleaseVersionId, deployment.ReleaseId, deployment.Environment, deployment.GitRef, deployment.GitSha, DeploymentTrigger.Redeploy, grant.Value.UserId),
            currentUser.Actor,
            Now(time),
            cancellationToken);
        return await AcceptedAsync(db, id, redeploy, cancellationToken);
    }

    /// <summary>
    /// FR-88: whether the caller (who passed the Owner check: an Owner or an Admin) may bind <paramref name="connectionId"/>,
    /// their own Vercel platform connection; <c>null</c> when allowed, else the problem (<c>NOT_FOUND</c> — also for a
    /// connection of another provider —, <c>CREDENTIAL_NOT_OWNER</c>).
    /// </summary>
    internal static async Task<ProblemHttpResult?> CanBindAsync(AideDbContext db, Guid callerId, Guid connectionId, CancellationToken cancellationToken, string field = "connectionId")
    {
        ArgumentNullException.ThrowIfNull(db);
        var ownerId = await db.PlatformConnections.AsNoTracking()
            .Where(c => c.Id == connectionId && c.Provider == PlatformProvider.Vercel)
            .Select(c => (Guid?)c.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        var allowed = DeploymentSettingsRules.CanBind(callerId, ProjectRole.Owner, ownerId);
        return allowed.IsFailure ? ApiProblems.FromError(allowed.Error! with { Field = field }) : null;
    }

    /// <summary>Saves the new deployment row (with its job and event) and answers 202 with it.</summary>
    private static async Task<Results<Accepted<DeploymentDto>, ProblemHttpResult>> AcceptedAsync(AideDbContext db, Guid projectId, Deployment deployment, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        var dto = (await DeploymentReader.ToDtosAsync(db, [deployment], cancellationToken))[0];
        return TypedResults.Accepted($"/api/projects/{projectId}/deployments", dto);
    }

    private static async Task<string?> HeadAsync(IWorkingCopies workingCopies, Guid projectId, string branch, CancellationToken cancellationToken)
    {
        try
        {
            return await workingCopies.ResolveCommitAsync(projectId, $"refs/heads/{branch}", cancellationToken);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            return null;
        }
    }

    /// <summary>The current time at PostgreSQL's <c>timestamptz</c> precision (microseconds).</summary>
    private static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private static Task<Deployment?> FindAsync(AideDbContext db, Guid projectId, Guid deploymentId, CancellationToken cancellationToken) =>
        db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deploymentId && d.ProjectId == projectId, cancellationToken);

    private static ProblemHttpResult DeploymentNotFound() => ApiProblems.NotFound("The deployment was not found.");

    private static ProblemHttpResult NotConfigured() =>
        ApiProblems.Of(ErrorCodes.DeploymentNotConfigured, "Deployment isn't set up for this project: set up the frontend target in Settings → Deployment.");
}
