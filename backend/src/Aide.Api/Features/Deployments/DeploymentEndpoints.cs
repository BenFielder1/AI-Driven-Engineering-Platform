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
/// FR-88, FR-90, FR-91, §4.15: a project's Deployment settings and env vars (§6 <c>/api/projects/:id/settings/deployment</c>)
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
            .WithSummary("Settings → Deployment for any member (FR-88): provider, supported, the binding (\"connected by <name>\", no token) with canBind for the caller, the linked Vercel project, root directory, framework, the stack's required env vars (§4.22) with present flags, and whether Vercel's GitHub App can access the repo (installUrl when not).")
            .Produces<DeploymentSettingsDto>();

        settings.MapPatch("/", UpdateDeploymentSettings)
            .WithName("UpdateDeploymentSettings")
            .WithSummary("Set the deployment provider (vercel, or null to turn deployment off), bind one of the caller's own Vercel connections (connectionId), and link an existing Vercel project (vercelProjectId) or create one linked to the GitHub repo (create {name, rootDirectory}) (Owner, FR-88). A Local Git project can't deploy (400 VALIDATION).")
            .Produces<DeploymentSettingsDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION (including a Local Git project, or both vercelProjectId and create)"),
                (StatusCodes.Status403Forbidden, "FORBIDDEN (not an Owner) or CREDENTIAL_NOT_OWNER (another user's connection, or changing the deployment with another Owner's bound connection without sending your own connectionId)"),
                (StatusCodes.Status404NotFound, "NOT_FOUND (project, connection or Vercel project)"),
                (StatusCodes.Status409Conflict, "VERCEL_GIT_NOT_CONNECTED (extension installUrl: Vercel's GitHub App install link), VERCEL_PLAN_LIMIT (Hobby plan with an organisation-owned repo) or CONFLICT (concurrent change)"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR (Vercel failed or couldn't be reached)"));

        settings.MapGet("/env", ListDeploymentEnv)
            .WithName("ListDeploymentEnv")
            .WithSummary("The project's environment variables (Owner, FR-88): key, targets, sensitive, hasValue (always true), updatedAt and the Vercel sync state. Values are never returned.")
            .Produces<IReadOnlyList<DeploymentEnvVarDto>>();

        settings.MapPut("/env/{key}", PutDeploymentEnv)
            .WithName("PutDeploymentEnv")
            .WithSummary("Create or replace an environment variable (Owner, FR-88): key ^[A-Za-z_][A-Za-z0-9_]*$ (up to 256), value up to 64 KB, targets preview and/or production, sensitive. The value is encrypted and pushed to Vercel (upsert by key and target). If the push fails the value is still saved, with syncError set, and the response is 502 UPSTREAM_ERROR; the next save or deployment pushes it again.")
            .Produces<DeploymentEnvVarDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR (saved in AIDE with syncError; Vercel failed or couldn't be reached)"));

        settings.MapDelete("/env/{key}", DeleteDeploymentEnv)
            .WithName("DeleteDeploymentEnv")
            .WithSummary("Delete an environment variable in AIDE and in Vercel (Owner, FR-88). If Vercel can't delete it, nothing is deleted (502 UPSTREAM_ERROR) so it can be retried.")
            .Produces(StatusCodes.Status204NoContent)
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
                (StatusCodes.Status409Conflict, "DEPLOYMENT_NOT_CONFIGURED (no deployment settings or no bound connection)"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR (Vercel failed or couldn't be reached)"));

        deployments.MapPost("/{deploymentId:guid}/promote", PromoteDeployment)
            .WithName("PromoteDeployment")
            .WithSummary("Promote an earlier ready production deployment (Owner, FR-91): a production rollback that leaves Git alone. Records a new promote row. 409 INVALID_DEPLOYMENT when the deployment isn't a ready production deployment.")
            .Produces<DeploymentDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status409Conflict, "INVALID_DEPLOYMENT (not a ready production deployment) or DEPLOYMENT_NOT_CONFIGURED"),
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
        Guid id, IProjectAccess access, DeploymentSettingsService settings, CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        return grant.IsFailure ? ApiProblems.FromError(grant.Error!) : TypedResults.Ok(await settings.GetAsync(id, grant.Value.ProjectRole, cancellationToken));
    }

    private static async Task<Results<Ok<DeploymentSettingsDto>, ValidationProblem, ProblemHttpResult>> UpdateDeploymentSettings(
        Guid id,
        UpdateDeploymentSettingsRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        DeploymentAccess deployments,
        DeploymentSettingsService settings,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var validation = DeploymentSettingsRules.Validate(
            request.Provider,
            request.ConnectionId,
            request.VercelProjectId,
            request.Create is not null,
            request.Create?.Name,
            request.Create?.RootDirectory,
            await GitProviderAsync(db, id, cancellationToken),
            allowFake: deployments.UsesFake,
            requireConnection: false);
        if (validation.IsFailure)
        {
            return ApiProblems.Validation(validation.Error!);
        }

        var callerId = grant.Value.UserId;
        if (request.ConnectionId is { } connectionId
            && await CanBindAsync(db, callerId, connectionId, cancellationToken) is { } bindProblem)
        {
            return bindProblem;
        }

        PreparedDeployment? prepared = null;
        if (request.Provider is { } provider)
        {
            var existing = await DeploymentAccess.FrontendTargetAsync(db, id, cancellationToken);
            if ((request.ConnectionId ?? existing?.ConnectionId) is not { } effective)
            {
                return ApiProblems.Validation(new Dictionary<string, string[]> { ["connectionId"] = ["Choose one of your Vercel connections."] });
            }

            // §2: connections are personal. Without their own connectionId, only the Owner whose connection is bound may
            // change what the project deploys to (another Owner would act with that Owner's token).
            var boundOwner = request.ConnectionId is null
                ? await db.PlatformConnections.AsNoTracking().Where(c => c.Id == effective).Select(c => (Guid?)c.UserId).FirstOrDefaultAsync(cancellationToken)
                : null;
            var mayUse = DeploymentSettingsRules.CanUseBinding(callerId, boundOwner, request.ConnectionId, turningOff: false);
            if (mayUse.IsFailure)
            {
                return ApiProblems.FromError(mayUse.Error!);
            }

            var repo = await deployments.RepoAsync(id, cancellationToken);
            var checkedWithVercel = await settings.PrepareAsync(provider, effective, request.VercelProjectId, request.Create, repo.FullName, string.Empty, cancellationToken);
            if (checkedWithVercel.IsFailure)
            {
                return checkedWithVercel.Error!.Code == ErrorCodes.Validation
                    ? ApiProblems.Validation(checkedWithVercel.Error!)
                    : DeploymentSettingsService.Problem(checkedWithVercel.Error!);
            }

            prepared = checkedWithVercel.Value;
        }

        // FR-88 (as FR-79): the binding and a membership change of the same Owner can't interleave.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ProjectLocks.LockProjectAsync(db, id, cancellationToken);
        var still = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (still.IsFailure)
        {
            return ApiProblems.FromError(still.Error!);
        }

        if (prepared is null)
        {
            await settings.TurnOffAsync(id, currentUser.Actor, cancellationToken);
        }
        else
        {
            await settings.ApplyAsync(id, callerId, prepared, bindConnection: request.ConnectionId is not null, currentUser.Actor, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ProjectEndpoints.ConcurrentEdit();
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(await settings.GetAsync(id, grant.Value.ProjectRole, cancellationToken));
    }

    private static async Task<Results<Ok<IReadOnlyList<DeploymentEnvVarDto>>, ProblemHttpResult>> ListDeploymentEnv(
        Guid id, IProjectAccess access, AideDbContext db, CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var vars = await db.DeploymentEnvVars.AsNoTracking().Where(v => v.ProjectId == id).OrderBy(v => v.Key).ToListAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<DeploymentEnvVarDto>>([.. vars.Select(ToDto)]);
    }

    private static async Task<Results<Ok<DeploymentEnvVarDto>, ValidationProblem, ProblemHttpResult>> PutDeploymentEnv(
        Guid id,
        string key,
        PutDeploymentEnvRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        DeploymentAccess deployments,
        DeploymentEnvSync envSync,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageDeployments, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var validation = DeploymentEnvRules.Validate(key, request.Value, request.Targets);
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

        // FR-145 (v2 flow, slice 1): Owner variables of the Vercel frontend; NEXT_PUBLIC_ ones reach the browser.
        variable.AppliesTo = [DeploymentComponent.Frontend.ToWire()];
        variable.Public = DeploymentEnvRules.IsPublicKey(key);
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
            new { projectId = id, section = "deployment_env", changes = new[] { key }, targets = variable.Targets, sensitive = variable.Sensitive },
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

        // FR-88: pushed to Vercel on save (when deployment is set up); a failed push keeps the value with syncError.
        var context = await deployments.ResolveAsync(id, cancellationToken);
        if (context.IsFailure)
        {
            return TypedResults.Ok(ToDto(variable));
        }

        try
        {
            await envSync.PushAsync(context.Value, [variable], cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToDto(variable));
        }
        catch (DeploymentProviderException ex)
        {
            if (ex.Failure == VercelFailure.CredentialRejected)
            {
                await deployments.MarkInvalidAsync(context.Value.Connection.Id, id, currentUser.Actor, now, cancellationToken);
            }

            variable.SyncError = DeploymentSettingsService.Message(ex);
            await db.SaveChangesAsync(cancellationToken);
            return ApiProblems.Of(ErrorCodes.UpstreamError, $"Saved in AIDE, but Vercel wasn't updated: {variable.SyncError} It is pushed again on the next save or deployment.");
        }
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

        // FR-88, §4.15: deleting in AIDE deletes in Vercel; if Vercel can't, nothing is deleted (all or nothing).
        var vercelIds = EnvSyncRules.AllIds(variable.VercelEnvIds);
        if (vercelIds.Count > 0)
        {
            var context = await deployments.ResolveAsync(id, cancellationToken);
            if (context.IsFailure)
            {
                return ApiProblems.Of(ErrorCodes.UpstreamError, $"The variable is in Vercel and can't be deleted there: {context.Error!.Message}");
            }

            try
            {
                foreach (var vercelId in vercelIds)
                {
                    await context.Value.Provider.DeleteEnvAsync(context.Value.Auth, context.Value.Target.ResourceId!, vercelId, cancellationToken);
                }
            }
            catch (DeploymentProviderException ex)
            {
                if (ex.Failure == VercelFailure.CredentialRejected)
                {
                    await deployments.MarkInvalidAsync(context.Value.Connection.Id, id, currentUser.Actor, Now(time), cancellationToken);
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

        var context = await deployments.ResolveAsync(id, cancellationToken);
        if (context.IsFailure)
        {
            return context.Error!.Code == ErrorCodes.DeploymentNotConfigured ? ApiProblems.FromError(context.Error!) : ApiProblems.Of(ErrorCodes.UpstreamError, context.Error!.Message);
        }

        var now = Now(time);
        try
        {
            // FR-91: Vercel re-points production at the earlier build (no rebuild); Git is left alone.
            await context.Value.Provider.PromoteAsync(context.Value.Auth, context.Value.Target.ResourceId!, source.ProviderDeploymentId, cancellationToken);
        }
        catch (DeploymentProviderException ex)
        {
            if (ex.Failure == VercelFailure.CredentialRejected)
            {
                await deployments.MarkInvalidAsync(context.Value.Connection.Id, id, currentUser.Actor, now, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return ex.Failure == VercelFailure.NotFound
                ? ApiProblems.Of(ErrorCodes.InvalidDeployment, "Vercel no longer has this deployment, so it can't be promoted.")
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

    internal static DeploymentEnvVarDto ToDto(DeploymentEnvVar v) =>
        new(v.Key, [.. DeploymentSettingsService.Targets(v.Targets)], v.Sensitive, true, v.UpdatedAt, v.SyncedAt, v.SyncError);

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

    private static async Task<GitProvider> GitProviderAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken) =>
        await db.ProjectGitSettings.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .Select(s => (GitProvider?)s.Provider)
            .FirstOrDefaultAsync(cancellationToken) ?? GitProvider.Local;

    private static Task<Deployment?> FindAsync(AideDbContext db, Guid projectId, Guid deploymentId, CancellationToken cancellationToken) =>
        db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deploymentId && d.ProjectId == projectId, cancellationToken);

    private static ProblemHttpResult DeploymentNotFound() => ApiProblems.NotFound("The deployment was not found.");

    private static ProblemHttpResult NotConfigured() =>
        ApiProblems.Of(ErrorCodes.DeploymentNotConfigured, "Deployment isn't set up for this project: bind a Vercel connection and link a Vercel project in Settings → Deployment.");
}
