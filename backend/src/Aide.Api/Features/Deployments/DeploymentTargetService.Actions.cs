using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>A refused target action: the error and the platform it is about (for the <c>platform</c> of <c>PLATFORM_*</c> problems).</summary>
public sealed record DeploymentTargetFailure(DomainError Error, DeploymentProvider Platform);

public sealed partial class DeploymentTargetService
{
    private static readonly string[] TargetChange = ["target"];

    /// <summary>
    /// FR-143, FR-144 <c>PUT …/targets/:component</c> (the caller passed the Owner check): validates, binds the caller's
    /// connection, derives the build configuration and queues the <c>deployment_setup</c> job; the target is
    /// <c>configuring</c> until it finishes.
    /// </summary>
    public async Task<(DeploymentTargetDto? Target, DeploymentTargetFailure? Failure)> PutAsync(
        Guid projectId, DeploymentComponent component, PutDeploymentTargetRequest request, Guid callerId, bool callerIsAdmin, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = request.Provider;

        // Neither create nor link: a rebind that keeps the target's resource when it has one on this provider.
        var current = await db.DeploymentTargets.AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.Component == component)
            .Select(t => new { t.Provider, t.ResourceId })
            .FirstOrDefaultAsync(cancellationToken);
        var rebind = request.Create is null && request.Link is null
            && current is { ResourceId: not null } && current.Provider == provider;
        var facts = await inventory.FactsAsync(projectId, cancellationToken);
        var gitProvider = await db.ProjectGitSettings.AsNoTracking().Where(s => s.ProjectId == projectId)
            .Select(s => (GitProvider?)s.Provider).FirstOrDefaultAsync(cancellationToken) ?? GitProvider.Local;
        var valid = DeploymentTargetSetupRules.Validate(
            component, provider, facts.Composition, gitProvider, access.UsesFake, request.ConnectionId, request.Create?.Name, request.Create is not null, request.Link?.ResourceId, rebind);
        if (valid.IsFailure)
        {
            return Fail(valid.Error!, provider);
        }

        if (!access.UsesFake && !DeploymentPlatforms.ImplementedForReal(provider))
        {
            return Fail(new DomainError(ErrorCodes.PlatformError, $"{DeploymentPlatforms.DisplayName(provider)} deployments aren't supported yet."), provider);
        }

        Guid? connectionId = null;
        if (DeploymentPlatforms.NeedsConnection(provider))
        {
            var expected = DeploymentPlatforms.ConnectionProvider(provider);
            var ownerId = await db.PlatformConnections.AsNoTracking()
                .Where(c => c.Id == request.ConnectionId && c.Provider == expected)
                .Select(c => (Guid?)c.UserId)
                .FirstOrDefaultAsync(cancellationToken);
            var bindable = DeploymentSettingsRules.CanBind(callerId, ProjectRole.Owner, ownerId);
            if (bindable.IsFailure)
            {
                return Fail(bindable.Error! with { Field = "connectionId" }, provider);
            }

            connectionId = request.ConnectionId;
        }

        if (provider == DeploymentProvider.GithubPages && !access.UsesFake && await PagesPermissionProblemAsync(projectId, cancellationToken) is { } pagesProblem)
        {
            return Fail(pagesProblem, provider);
        }

        var now = time.GetUtcNow();
        var plan = DeploymentBuildRules.Derive(facts.Composition, component, provider, facts.Stack?.MigrateCommand);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ProjectLocks.LockProjectAsync(db, projectId, cancellationToken);

        // FR-79: re-read the caller's role under the same lock as member demotions and removals, so a concurrent demotion
        // either commits first (and is seen here) or waits for this bind (and then clears it).
        var callerRole = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.UserId == callerId)
            .Select(m => (ProjectRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);
        if (!callerIsAdmin && callerRole != ProjectRole.Owner)
        {
            return Fail(callerRole is null
                ? new DomainError(ErrorCodes.NotFound, "The project was not found.")
                : new DomainError(ErrorCodes.Forbidden, "Your project role doesn't allow this action."), provider);
        }

        var target = await db.DeploymentTargets.FirstOrDefaultAsync(t => t.ProjectId == projectId && t.Component == component, cancellationToken);
        if (rebind && (target?.ResourceId is null || target.Provider != provider))
        {
            // The resource went away while this request waited for the lock.
            return Fail(new DomainError(ErrorCodes.Validation, "The deployment target is invalid.") { Details = ["create: Create a resource or link an existing one."] }, provider);
        }

        var payload = request.Create is { } create
            ? new DeploymentSetupPayload(projectId, Guid.Empty, true, create.Name, create.Region, create.Plan, null)
            : request.Link is { } link
                ? new DeploymentSetupPayload(projectId, Guid.Empty, false, null, null, null, link.ResourceId)
                : rebind
                    ? new DeploymentSetupPayload(projectId, Guid.Empty, false, null, null, null, target!.ResourceId)

                    // GitHub Pages without create or link: setup enables Pages on the repository (it takes no name).
                    : new DeploymentSetupPayload(projectId, Guid.Empty, true, null, null, null, null);
        if (target is null)
        {
            target = new DeploymentTarget { ProjectId = projectId, Component = component, Provider = provider };
            db.DeploymentTargets.Add(target);
        }
        else if (target.Provider != provider)
        {
            target.ResourceId = null;
            target.PreviewResourceId = null;
            target.Name = null;
            target.ProductionUrl = null;
            target.PreviewUrl = null;
            await envSync.ResetAsync(projectId, component, cancellationToken);
        }

        target.Provider = provider;
        target.ConnectionId = connectionId;
        target.BoundByUserId = connectionId is null ? null : callerId;
        target.RootDirectory = plan.RootDirectory;
        target.BuildConfig = plan.Config;
        target.Status = DeploymentTargetStatus.Configuring;
        target.StatusReason = StepQueued;
        target.UpdatedAt = now;
        await CancelQueuedSetupAsync(projectId, target.Id, now, cancellationToken);
        jobs.Enqueue(
            JobTypes.DeploymentSetup,
            payload with { TargetId = target.Id },
            projectId,
            now);
        db.AddEvent(
            EventTypes.SettingsUpdated,
            projectId,
            new { projectId, section = "deployment", component = component.ToWire(), provider = provider.ToWire(), changes = TargetChange, mode = request.Create is not null ? "create" : request.Link is not null ? "link" : rebind ? "rebind" : "create" },
            actor,
            now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Fail(new DomainError(ErrorCodes.Conflict, "The deployment target was changed by someone else. Reload and try again."), provider);
        }

        await transaction.CommitAsync(cancellationToken);
        LogQueued(logger, projectId, component, provider);
        return (await TargetDtoAsync(projectId, component, true, null, cancellationToken), null);
    }

    /// <summary>FR-144 <c>DELETE …/targets/:component</c>: unbinds and clears the target (the platform resource is never deleted).</summary>
    public async Task<DomainError?> DeleteAsync(Guid projectId, DeploymentComponent component, string actor, CancellationToken cancellationToken)
    {
        var target = await db.DeploymentTargets.FirstOrDefaultAsync(t => t.ProjectId == projectId && t.Component == component, cancellationToken);
        if (target is null)
        {
            return new DomainError(ErrorCodes.NotFound, "The deployment target was not found.");
        }

        var now = time.GetUtcNow();
        await CancelQueuedSetupAsync(projectId, target.Id, now, cancellationToken);
        db.DeploymentTargets.Remove(target);
        await envSync.ResetAsync(projectId, component, cancellationToken);
        db.AddEvent(
            EventTypes.SettingsUpdated,
            projectId,
            new { projectId, section = "deployment", component = component.ToWire(), changes = TargetChange, cleared = true },
            actor,
            now);
        await db.SaveChangesAsync(cancellationToken);

        // §4.22: derived values follow the remaining targets.
        var recomputed = await inventory.RecomputeDerivedAsync(projectId, await inventory.FactsAsync(projectId, cancellationToken), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        // The remaining targets drop what AIDE no longer derives (the cleared target's resource is left as it is).
        await envSync.DeleteOnTargetsAsync(projectId, recomputed.Removed, cancellationToken);
        var pending = recomputed.Changed.ToList();
        if (pending.Count > 0)
        {
            await envSync.PushToTargetsAsync(projectId, pending, actor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return null;
    }

    /// <summary>FR-144 <c>POST …/targets/:component/verify</c>: the readiness checklist, checked with the platform.</summary>
    public async Task<(DeploymentVerifyDto? Result, DomainError? Error)> VerifyAsync(
        Guid projectId, DeploymentComponent component, ProjectRole? callerRole, string actor, CancellationToken cancellationToken)
    {
        var target = await db.DeploymentTargets.FirstOrDefaultAsync(t => t.ProjectId == projectId && t.Component == component, cancellationToken);
        if (target is null)
        {
            return (null, new DomainError(ErrorCodes.NotFound, "The deployment target was not found."));
        }

        var now = time.GetUtcNow();
        var facts = await inventory.FactsAsync(projectId, cancellationToken);
        var rows = await inventory.RowsAsync(projectId, facts, cancellationToken);
        var vcsHost = DeploymentPlatforms.VcsHost(facts.Composition);
        var connection = target.ConnectionId is { } id ? await db.PlatformConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken) : null;
        var connectionState = ConnectionState(target.Provider, connection);
        bool? gitAccess = null;
        string? gitMessage = null;
        var gitFix = DeploymentPlatforms.GitConnectUrl(target.Provider, vcsHost);
        bool? previewReady = PreviewReady(target);
        var context = await access.ResolveAsync(target, cancellationToken);
        if (context.IsFailure)
        {
            gitMessage = context.Error!.Message;
        }
        else
        {
            try
            {
                var check = await context.Value.Provider.VerifyTargetAsync(context.Value.Auth, context.Value.TargetRef, cancellationToken);
                (gitAccess, gitMessage) = (check.GitAccess, check.GitMessage);
                previewReady = check.PreviewReady ?? previewReady;
            }
            catch (DeploymentProviderException ex)
            {
                if (ex.Failure == DeploymentFailure.CredentialRejected)
                {
                    await access.MarkInvalidAsync(context.Value.Connection?.Id, projectId, actor, now, cancellationToken);
                    connectionState = ReadinessConnection.Invalid;
                }

                gitMessage = DeploymentSettingsService.Message(ex);
                if (ex.Failure is DeploymentFailure.GitNotConnected or DeploymentFailure.PermissionMissing or DeploymentFailure.NotFound)
                {
                    gitAccess = false;
                }
            }
        }

        if (target.Provider == DeploymentProvider.GithubPages && !access.UsesFake && await PagesPermissionProblemAsync(projectId, cancellationToken) is { } pages)
        {
            (gitAccess, gitMessage, gitFix) = (false, pages.Message, pages.Reason);
        }

        var branch = await BranchControlAsync(projectId, target, cancellationToken);
        var items = ReadinessRules.Checklist(new ReadinessInput(
            target.Provider,
            Checked: true,
            connectionState,
            DeploymentPlatforms.NeedsConnection(target.Provider) ? "/account/connections" : null,
            gitAccess,
            gitFix,
            gitMessage,
            EnvInventoryRules.MissingRequired(rows, component),
            branch,
            BranchControlPath(target.Provider, target.RootDirectory),
            previewReady));
        (target.Status, target.StatusReason) = ReadinessRules.StatusAfterVerify(target.Status, target.StatusReason, items);
        target.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        var dto = await TargetDtoAsync(projectId, component, callerRole == ProjectRole.Owner, items, cancellationToken);
        return (new DeploymentVerifyDto(dto, dto.Readiness), null);
    }

    /// <summary>The card of one component (after a change).</summary>
    public async Task<DeploymentTargetDto> TargetDtoAsync(Guid projectId, DeploymentComponent component, bool canBind, IReadOnlyList<ReadinessItem>? items, CancellationToken cancellationToken)
    {
        var facts = await inventory.FactsAsync(projectId, cancellationToken);
        var rows = await inventory.RowsAsync(projectId, facts, cancellationToken);
        return await ToDtoAsync(projectId, component, facts.Targets.FirstOrDefault(t => t.Component == component), facts, rows, canBind, items, cancellationToken);
    }

    /// <summary>
    /// FR-143: <c>PLATFORM_PERMISSION_MISSING</c> (with the App's page) when the project's GitHub App installation token lacks
    /// Pages write; <c>UPSTREAM_ERROR</c> when GitHub can't mint one; <c>null</c> when allowed (or a PAT, checked at setup).
    /// </summary>
    private async Task<DomainError?> PagesPermissionProblemAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var resolved = await github.ResolveAsync(projectId, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        if (resolved.Value?.InstallationPermissions is { } permissions && GitHubRules.PagesPermission(permissions) != GitHubPagesAccess.Granted)
        {
            return new DomainError(ErrorCodes.PlatformPermissionMissing, "The AIDE GitHub App needs the Pages permission. Update it in GitHub settings, then accept it on the installation.")
            {
                Reason = await AppSettingsUrlAsync(cancellationToken),
            };
        }

        return null;
    }

    /// <summary>§4.18 <c>deploy_branch_control</c> as readiness: Vercel's <c>vercel.json</c>, Netlify's <c>netlify.toml</c> on the default branch.</summary>
    private async Task<BranchControlState> BranchControlAsync(Guid projectId, DeploymentTarget target, CancellationToken cancellationToken)
    {
        if (target.Provider == DeploymentProvider.GithubPages)
        {
            return BranchControlState.NotApplicable;
        }

        if (access.UsesFake || BranchControlPath(target.Provider, target.RootDirectory) is not { } path)
        {
            return BranchControlState.PlatformManaged;
        }

        var branch = await db.ProjectGitSettings.AsNoTracking().Where(s => s.ProjectId == projectId).Select(s => s.DefaultBranch).FirstOrDefaultAsync(cancellationToken) ?? "main";
        try
        {
            var files = await workingCopies.ReadCommittedAsync(projectId, branch, [path], cancellationToken);
            if (files.Count == 0)
            {
                return BranchControlState.Missing;
            }

            var content = files[0].Content;
            return target.Provider == DeploymentProvider.Netlify
                ? content.Contains("aide/", StringComparison.Ordinal) ? BranchControlState.Ok : BranchControlState.Missing
                : VercelConfigRules.DisablesAideBranches(content) ? BranchControlState.Ok : BranchControlState.Missing;
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            return BranchControlState.Unknown;
        }
    }

    private async Task CancelQueuedSetupAsync(Guid projectId, Guid targetId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var queued = await db.Jobs.Where(j => j.ProjectId == projectId && j.Type == JobTypes.DeploymentSetup && j.Status == JobStatus.Queued).ToListAsync(cancellationToken);
        foreach (var job in queued.Where(j => TryPayload(j.Payload)?.TargetId == targetId))
        {
            job.Status = JobPolicy.Cancel(job.Status);
            job.CompletedAt = now;
        }
    }

    private static (DeploymentTargetDto?, DeploymentTargetFailure?) Fail(DomainError error, DeploymentProvider platform) => (null, new DeploymentTargetFailure(error, platform));

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {Component} target setup queued on {Provider}")]
    private static partial void LogQueued(ILogger logger, Guid projectId, DeploymentComponent component, DeploymentProvider provider);
}
