using Aide.Api.Features.Databases;
using Aide.Api.Features.Projects;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// FR-187, FR-190, FR-191, §4.28: a project's deployment groups. <c>PUT</c> checks what it can (the name, or that the
/// connection reaches the project), saves the group with its parts <c>configuring</c> and queues one <c>deployment_setup</c>
/// job; a rebind only swaps the connection after checking it reaches the project; <c>DELETE</c> clears the group and its
/// parts and keeps every platform resource; retry resumes only failed or missing parts. The rules are in
/// <see cref="DeploymentGroupRules"/>.
/// </summary>
public sealed partial class DeploymentGroupService(
    AideDbContext db,
    DeploymentAccess access,
    DeploymentInventoryService inventory,
    DeploymentEnvSync envSync,
    DeploymentTargetService targetService,
    ManagedDatabaseService databases,
    ManagedDatabaseSecrets secrets,
    DeploymentGroupReader reader,
    JobEnqueuer jobs,
    TimeProvider time,
    ILogger<DeploymentGroupService> logger)
{
    private static readonly string[] GroupChange = ["group"];

    private static readonly DeploymentEnvironment[] Environments = [DeploymentEnvironment.Production, DeploymentEnvironment.Preview];

    public async Task<(DeploymentGroupDto? Group, DeploymentTargetFailure? Failure)> PutAsync(
        Guid projectId, DeploymentProvider provider, PutDeploymentGroupRequest request, Guid callerId, bool callerIsAdmin, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var facts = await inventory.FactsAsync(projectId, cancellationToken);
        var gitProvider = await db.ProjectGitSettings.AsNoTracking().Where(s => s.ProjectId == projectId)
            .Select(s => (GitProvider?)s.Provider).FirstOrDefaultAsync(cancellationToken) ?? GitProvider.Local;
        var existing = await db.DeploymentGroups.AsNoTracking().FirstOrDefaultAsync(g => g.ProjectId == projectId && g.Provider == provider, cancellationToken);
        var parts = DeploymentGroupRules.Spec(facts.Composition, provider, access.UsesFake)?.Parts ?? [];
        var alone = await PartsSetUpAloneAsync(projectId, parts, cancellationToken);
        var linkParts = request.Link?.Parts;
        var valid = DeploymentGroupRules.Validate(new DeploymentGroupPut(
            provider,
            facts.Composition,
            gitProvider,
            access.UsesFake,
            request.ConnectionId,
            request.Create is not null,
            request.Create?.Name,
            request.Link is not null,
            request.Link?.ResourceId,
            linkParts?.Frontend,
            linkParts?.Backend,
            linkParts?.Database,
            existing is not null,
            alone));
        if (valid.IsFailure)
        {
            return Fail(valid.Error!, provider);
        }

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

        var auth = await access.ConnectionAuthAsync(provider, request.ConnectionId, cancellationToken);
        if (auth.IsFailure)
        {
            return Fail(auth.Error!, provider);
        }

        var rebind = request.Create is null && request.Link is null;
        if (!rebind && existing is { Status: DeploymentGroupStatus.Configuring })
        {
            return Fail(new DomainError(ErrorCodes.Conflict, "The group is being set up. Wait for it to finish."), provider);
        }

        // A create repeating a failed group's name resumes it; anything else on a group with resources needs a DELETE first (D-V5-9).
        var resume = request.Create is not null && existing is not null && existing.Name == request.Create.Name;
        if (!rebind && !resume && existing is not null && await HasResourcesAsync(existing, cancellationToken))
        {
            return Fail(new DomainError(ErrorCodes.Conflict, "The group already has platform resources. Remove the group first; AIDE keeps those resources."), provider);
        }

        var checkedProject = await CheckWithPlatformAsync(provider, parts, request, existing, resume, rebind, auth.Value, projectId, cancellationToken);
        if (checkedProject.Error is { } platformError)
        {
            return Fail(platformError, provider);
        }

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ProjectLocks.LockProjectAsync(db, projectId, cancellationToken);

        // FR-79: the role re-read under the same lock as member demotions (as the target PUT).
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

        var group = await db.DeploymentGroups.FirstOrDefaultAsync(g => g.ProjectId == projectId && g.Provider == provider, cancellationToken);
        if (rebind)
        {
            if (group is null)
            {
                return Fail(new DomainError(ErrorCodes.NotFound, "The deployment group was not found."), provider);
            }

            await RebindAsync(group, request.ConnectionId!.Value, callerId, now, cancellationToken);
        }
        else
        {
            if (group is null)
            {
                group = new DeploymentGroup { ProjectId = projectId, Provider = provider };
                db.DeploymentGroups.Add(group);
            }

            group.Name = request.Create?.Name ?? checkedProject.LinkedName ?? provider.ToWire();
            group.ConnectionId = request.ConnectionId;
            group.BoundByUserId = callerId;
            group.Region = request.Create?.Region;
            group.Plan = request.Create?.Plan;
            group.SharedResourceId = resume ? group.SharedResourceId : request.Link?.ResourceId;
            group.Status = DeploymentGroupStatus.Configuring;
            group.StatusReason = null;
            group.UpdatedAt = now;
            await PreparePartsAsync(group, parts, facts, now, membersOnly: false, cancellationToken);
            await CancelQueuedAsync(projectId, group.Id, now, cancellationToken);
            jobs.Enqueue(JobTypes.DeploymentSetup, Payload(group, request.Create is not null, linkParts), projectId, now);
        }

        db.AddEvent(
            EventTypes.SettingsUpdated,
            projectId,
            new { projectId, section = "deployment", group = group.Id, provider = provider.ToWire(), changes = GroupChange, mode = rebind ? "rebind" : request.Create is not null ? "create" : "link" },
            actor,
            now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Fail(new DomainError(ErrorCodes.Conflict, "The deployment group was changed by someone else. Reload and try again."), provider);
        }

        await transaction.CommitAsync(cancellationToken);
        LogQueued(logger, projectId, provider, rebind);
        return (await CardAsync(projectId, provider, facts, cancellationToken), null);
    }

    /// <summary>FR-191: clears the group and its parts' bindings; every platform resource stays (FR-190). The parts' history stays too.</summary>
    public async Task<DomainError?> DeleteAsync(Guid projectId, DeploymentProvider provider, string actor, CancellationToken cancellationToken)
    {
        var group = await db.DeploymentGroups.FirstOrDefaultAsync(g => g.ProjectId == projectId && g.Provider == provider, cancellationToken);
        if (group is null)
        {
            return new DomainError(ErrorCodes.NotFound, "The deployment group was not found.");
        }

        var now = time.GetUtcNow();
        await CancelQueuedAsync(projectId, group.Id, now, cancellationToken);
        var components = await db.DeploymentTargets.AsNoTracking().Where(t => t.GroupId == group.Id).Select(t => t.Component).ToListAsync(cancellationToken);
        var environments = await db.ManagedDatabases.AsNoTracking().Where(d => d.GroupId == group.Id).Select(d => d.Environment).ToListAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var component in components)
        {
            if (await targetService.DeleteAsync(projectId, component, actor, cancellationToken) is { } error)
            {
                return error;
            }
        }

        foreach (var environment in environments)
        {
            if (await databases.DeleteAsync(projectId, environment, actor, cancellationToken) is { Code: not ErrorCodes.NotFound } error)
            {
                return error;
            }
        }

        var stored = await db.DeploymentGroups.FirstOrDefaultAsync(g => g.Id == group.Id, cancellationToken);
        if (stored is not null)
        {
            await db.Entry(stored).ReloadAsync(cancellationToken);
            db.DeploymentGroups.Remove(stored);
            db.AddEvent(
                EventTypes.SettingsUpdated,
                projectId,
                new { projectId, section = "deployment", group = group.Id, provider = provider.ToWire(), changes = GroupChange, cleared = true },
                actor,
                now);
            await db.SaveChangesAsync(cancellationToken);
        }

        return null;
    }

    /// <summary>FR-190 Retry: queues the group's job for the failed or missing parts; ready parts are left alone. <c>INVALID_STATE</c> when nothing can retry.</summary>
    public async Task<(DeploymentGroupDto? Group, DomainError? Error)> RetryAsync(Guid projectId, DeploymentProvider provider, string actor, CancellationToken cancellationToken)
    {
        var facts = await inventory.FactsAsync(projectId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ProjectLocks.LockProjectAsync(db, projectId, cancellationToken);
        var group = await db.DeploymentGroups.FirstOrDefaultAsync(g => g.ProjectId == projectId && g.Provider == provider, cancellationToken);
        if (group is null)
        {
            return (null, new DomainError(ErrorCodes.NotFound, "The deployment group was not found."));
        }

        var parts = DeploymentGroupRules.Spec(facts.Composition, provider, access.UsesFake)?.Parts ?? [];
        var targets = await db.DeploymentTargets.Where(t => t.ProjectId == projectId).ToListAsync(cancellationToken);
        var rows = await db.ManagedDatabases.Where(d => d.ProjectId == projectId).ToListAsync(cancellationToken);
        if (group.Status == DeploymentGroupStatus.Configuring || !DeploymentGroupReader.CanRetry(group.Id, targets, rows))
        {
            return (null, new DomainError(ErrorCodes.InvalidState, "Nothing in this group needs a retry."));
        }

        var now = time.GetUtcNow();
        var lastJob = await LatestJobPayloadAsync(projectId, group.Id, cancellationToken);
        group.Status = DeploymentGroupStatus.Configuring;
        group.StatusReason = null;
        group.UpdatedAt = now;
        await PreparePartsAsync(group, parts, facts, now, membersOnly: true, cancellationToken);
        await CancelQueuedAsync(projectId, group.Id, now, cancellationToken);
        jobs.Enqueue(JobTypes.DeploymentSetup, Payload(group, group.SharedResourceId is null, lastJob?.LinkParts), projectId, now);
        db.AddEvent(
            EventTypes.SettingsUpdated,
            projectId,
            new { projectId, section = "deployment", group = group.Id, provider = provider.ToWire(), changes = GroupChange, retried = true },
            actor,
            now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (null, new DomainError(ErrorCodes.Conflict, "The deployment group was changed by someone else. Reload and try again."));
        }

        await transaction.CommitAsync(cancellationToken);
        return (await CardAsync(projectId, provider, facts, cancellationToken), null);
    }

    private async Task<DeploymentGroupDto> CardAsync(Guid projectId, DeploymentProvider provider, DeploymentFacts facts, CancellationToken cancellationToken) =>
        (await reader.ListAsync(projectId, facts.Composition, true, cancellationToken)).First(g => g.Provider == provider);

    private static DeploymentSetupPayload Payload(DeploymentGroup group, bool create, DeploymentGroupLinkParts? linkParts) =>
        new(group.ProjectId, Guid.Empty, create, group.Name, group.Region, group.Plan, group.SharedResourceId, group.Id, linkParts);

    /// <summary>D-V5-9: parts on the group's platform with a resource of their own, which a group never takes over.</summary>
    private async Task<List<DeploymentPart>> PartsSetUpAloneAsync(Guid projectId, IReadOnlyList<DeploymentPart> parts, CancellationToken cancellationToken)
    {
        var alone = new List<DeploymentPart>();
        var targets = await db.DeploymentTargets.AsNoTracking().Where(t => t.ProjectId == projectId && t.GroupId == null && t.ResourceId != null).Select(t => t.Component).ToListAsync(cancellationToken);
        alone.AddRange(parts.Where(p => p != DeploymentPart.Database && targets.Contains(DeploymentGroupReader.ComponentOf(p))));
        if (parts.Contains(DeploymentPart.Database)
            && await db.ManagedDatabases.AsNoTracking().AnyAsync(d => d.ProjectId == projectId && d.GroupId == null && d.ResourceId != null, cancellationToken))
        {
            alone.Add(DeploymentPart.Database);
        }

        return alone;
    }

    private async Task<bool> HasResourcesAsync(DeploymentGroup group, CancellationToken cancellationToken) =>
        group.SharedResourceId is not null
        || await db.DeploymentTargets.AsNoTracking().AnyAsync(t => t.GroupId == group.Id && t.ResourceId != null, cancellationToken)
        || await db.ManagedDatabases.AsNoTracking().AnyAsync(d => d.GroupId == group.Id && d.ResourceId != null, cancellationToken);

    /// <summary>
    /// FR-190, FR-191: the checks the platform answers, before anything is saved: a new name is free
    /// (<c>PLATFORM_RESOURCE_EXISTS</c>), and a linked or rebound project is reachable with this connection
    /// (<c>PLATFORM_ERROR</c>; the old binding stays).
    /// </summary>
    private async Task<(DomainError? Error, string? LinkedName)> CheckWithPlatformAsync(
        DeploymentProvider provider, IReadOnlyList<DeploymentPart> parts, PutDeploymentGroupRequest request, DeploymentGroup? existing, bool resume, bool rebind, DeploymentAuth auth, Guid projectId, CancellationToken cancellationToken)
    {
        var platform = access.ProviderFor(provider);
        var capability = DeploymentGroupRules.Spec((await inventory.FactsAsync(projectId, cancellationToken)).Composition, provider, access.UsesFake)?.Capability;
        var display = DeploymentPlatforms.DisplayName(provider);
        try
        {
            if (request.Create is { } create)
            {
                if (capability == SharedProjectCapability.Project && !resume && await platform.FindSharedProjectAsync(auth, create.Name, cancellationToken) is not null)
                {
                    return (new DomainError(ErrorCodes.PlatformResourceExists, $"{display} already has a project with that name. Link it instead, or choose another name."), null);
                }

                return (null, null);
            }

            var projectToReach = rebind ? existing?.SharedResourceId : request.Link?.ResourceId;
            if (capability == SharedProjectCapability.Project && projectToReach is not null)
            {
                var reached = await platform.GetSharedProjectAsync(auth, projectToReach, cancellationToken);
                return reached is null
                    ? (Unreachable(display), null)
                    : (null, reached.Name);
            }

            if (rebind && capability == SharedProjectCapability.SharedSetup && existing is not null)
            {
                var repo = await access.RepoAsync(projectId, cancellationToken);
                foreach (var target in await db.DeploymentTargets.AsNoTracking().Where(t => t.GroupId == existing.Id && t.ResourceId != null).ToListAsync(cancellationToken))
                {
                    var check = await platform.VerifyTargetAsync(auth, DeploymentAccess.RefOf(target, repo), cancellationToken);
                    if (check.GitAccess == false)
                    {
                        return (Unreachable(display), null);
                    }
                }
            }

            // Vercel has no shared resource to name the group after.
            return (null, (await access.RepoAsync(projectId, cancellationToken)).Name ?? provider.ToWire());
        }
        catch (DeploymentProviderException ex)
        {
            LogRefused(logger, provider, ex.Failure);
            return (new DomainError(ErrorCodes.PlatformError, DeploymentSettingsService.Message(ex)), null);
        }
    }

    private static DomainError Unreachable(string display) =>
        new(ErrorCodes.PlatformError, $"This {display} connection can't reach the project.");

    /// <summary>FR-191: the new connection for the group and every part. Nothing on the platform changes, and no job runs.</summary>
    private async Task RebindAsync(DeploymentGroup group, Guid connectionId, Guid callerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        group.ConnectionId = connectionId;
        group.BoundByUserId = callerId;
        group.UpdatedAt = now;
        foreach (var target in await db.DeploymentTargets.Where(t => t.GroupId == group.Id).ToListAsync(cancellationToken))
        {
            target.ConnectionId = connectionId;
            target.BoundByUserId = callerId;
            target.UpdatedAt = now;
        }

        foreach (var row in await db.ManagedDatabases.Where(d => d.GroupId == group.Id).ToListAsync(cancellationToken))
        {
            if (Domain.Databases.ManagedDatabaseRules.ConnectionProvider(row.Provider) is not null)
            {
                row.ConnectionId = connectionId;
            }

            secrets.SetConnection(row, secrets.Connection(row) with { ConnectedByUserId = callerId });
            row.UpdatedAt = now;
        }
    }

    /// <summary>FR-190: the parts that (still) need setting up go to <c>configuring</c>; ready ones are left alone.</summary>
    private async Task PreparePartsAsync(DeploymentGroup group, IReadOnlyList<DeploymentPart> parts, DeploymentFacts facts, DateTimeOffset now, bool membersOnly, CancellationToken cancellationToken)
    {
        foreach (var part in parts)
        {
            if (part == DeploymentPart.Database)
            {
                await PrepareDatabaseAsync(group, now, membersOnly, cancellationToken);
                continue;
            }

            var component = DeploymentGroupReader.ComponentOf(part);
            var plan = DeploymentBuildRules.Derive(facts.Composition, component, group.Provider, facts.Stack?.MigrateCommand);
            var target = await db.DeploymentTargets.FirstOrDefaultAsync(t => t.ProjectId == group.ProjectId && t.Component == component, cancellationToken);
            if (membersOnly && target?.GroupId != group.Id)
            {
                continue;
            }

            if (target is not null && target.GroupId == group.Id && !DeploymentGroupRules.Retries(target.Status))
            {
                continue;
            }

            // D-V5-9: a part that left the group with a resource of its own stays out; joining again is explicit (groupId).
            if (target is { GroupId: null, ResourceId: not null })
            {
                continue;
            }

            if (target is null)
            {
                target = new DeploymentTarget { ProjectId = group.ProjectId, Component = component, Provider = group.Provider };
                db.DeploymentTargets.Add(target);
            }
            else if (target.Provider != group.Provider)
            {
                target.ResourceId = null;
                target.PreviewResourceId = null;
                target.Name = null;
                target.ProductionUrl = null;
                target.PreviewUrl = null;
                await envSync.ResetAsync(group.ProjectId, component, cancellationToken);
            }

            target.Provider = group.Provider;
            target.GroupId = group.Id;
            target.ConnectionId = group.ConnectionId;
            target.BoundByUserId = group.BoundByUserId;
            target.RootDirectory = plan.RootDirectory;
            target.BuildConfig = plan.Config;
            target.Status = DeploymentTargetStatus.Configuring;
            target.StatusReason = DeploymentTargetService.StepQueued;
            target.UpdatedAt = now;
        }
    }

    private async Task PrepareDatabaseAsync(DeploymentGroup group, DateTimeOffset now, bool membersOnly, CancellationToken cancellationToken)
    {
        var rows = await db.ManagedDatabases.Where(d => d.ProjectId == group.ProjectId).ToListAsync(cancellationToken);
        var provider = DeploymentGroups.DatabaseProviderOf(group.Provider);
        foreach (var environment in Environments)
        {
            var row = rows.FirstOrDefault(d => d.Environment == environment);
            if (membersOnly && row?.GroupId != group.Id)
            {
                continue;
            }

            if (row is not null && row.GroupId == group.Id && !DeploymentGroupRules.Retries(row.Status))
            {
                continue;
            }

            if (row is { GroupId: null, ResourceId: not null })
            {
                continue;
            }

            if (row is null)
            {
                row = new ManagedDatabase { ProjectId = group.ProjectId, Environment = environment, CreatedAt = now };
                db.ManagedDatabases.Add(row);
            }

            var connection = Domain.Databases.ManagedDatabaseRules.ConnectionProvider(provider) is null ? null : group.ConnectionId;
            row.Provider = provider;
            row.GroupId = group.Id;
            row.ConnectionId = connection;
            row.Region = group.Region;
            row.Status = ManagedDatabaseStatus.Provisioning;
            row.UpdatedAt = now;
            secrets.SetConnection(row, new Domain.Databases.ManagedDatabaseConnection
            {
                Name = DeploymentGroupRules.PartName(group.Name, DeploymentPart.Database),
                ConnectedByUserId = group.BoundByUserId,
                Step = ManagedDatabaseService.StepQueued,
            });
        }
    }

    private async Task CancelQueuedAsync(Guid projectId, Guid groupId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var queued = await db.Jobs.Where(j => j.ProjectId == projectId && j.Type == JobTypes.DeploymentSetup && j.Status == JobStatus.Queued).ToListAsync(cancellationToken);
        foreach (var job in queued.Where(j => DeploymentTargetService.TryPayload(j.Payload)?.GroupId == groupId))
        {
            job.Status = JobPolicy.Cancel(job.Status);
            job.CompletedAt = now;
        }
    }

    private async Task<DeploymentSetupPayload?> LatestJobPayloadAsync(Guid projectId, Guid groupId, CancellationToken cancellationToken)
    {
        var candidates = await db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.DeploymentSetup)
            .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        return candidates.Select(j => DeploymentTargetService.TryPayload(j.Payload)).FirstOrDefault(p => p?.GroupId == groupId);
    }

    private static (DeploymentGroupDto?, DeploymentTargetFailure?) Fail(DomainError error, DeploymentProvider platform) => (null, new DeploymentTargetFailure(error, platform));

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: deployment group on {Provider} saved (rebind: {Rebind})")]
    private static partial void LogQueued(ILogger logger, Guid projectId, DeploymentProvider provider, bool rebind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Provider} refused a deployment group check: {Failure}")]
    private static partial void LogRefused(ILogger logger, DeploymentProvider provider, DeploymentFailure failure);
}
