using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Databases;

/// <summary>Payload of the <c>database_provisioning</c> job (FR-152): the database, what to do, and the phase (<c>setup</c>, then <c>poll</c>).</summary>
public sealed record DatabaseProvisioningPayload(
    Guid ProjectId, Guid DatabaseId, DatabaseSetupMode Mode, string Name, string? Region, string? Plan, string? ResourceId, string Phase)
{
    public const string SetupPhase = "setup";
    public const string PollPhase = "poll";
}

public sealed partial class ManagedDatabaseService
{
    private static readonly string[] DatabaseChange = ["database"];

    /// <summary>
    /// FR-152 <c>PUT …/databases/:environment</c> (the caller passed the Owner check): validates, binds the caller's own
    /// connection, stores an External string encrypted, and queues the <c>database_provisioning</c> job; the database is
    /// <c>provisioning</c> until it finishes.
    /// </summary>
    public async Task<(DeploymentDatabaseDto? Card, DomainError? Error)> PutAsync(
        Guid projectId, DeploymentEnvironment environment, PutManagedDatabaseRequest request, Guid callerId, bool callerIsAdmin, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = request.Provider;
        var modes = new[] { request.Create is null ? (DatabaseSetupMode?)null : DatabaseSetupMode.Create, request.Link is null ? null : DatabaseSetupMode.Link, request.External is null ? null : DatabaseSetupMode.External }
            .OfType<DatabaseSetupMode>().ToList();
        if (modes.Count > 1)
        {
            return (null, new DomainError(ErrorCodes.Validation, "The database settings are invalid.") { Details = ["create: Send one of create, link or external."] });
        }

        var mode = modes.Count == 1 ? modes[0] : (DatabaseSetupMode?)null;
        var facts = await inventory.FactsAsync(projectId, cancellationToken);
        var valid = ManagedDatabaseRules.Validate(
            provider, facts.Composition, providers.UsesFake, request.ConnectionId, mode, request.Create?.Region, request.Link?.ResourceId, request.External?.ConnectionString);
        if (valid.IsFailure)
        {
            return (null, valid.Error);
        }

        Guid? connectionId = null;
        if (ManagedDatabaseRules.ConnectionProvider(provider) is { } platform && request.ConnectionId is { } requested)
        {
            var ownerId = await db.PlatformConnections.AsNoTracking()
                .Where(c => c.Id == requested && c.Provider == platform)
                .Select(c => (Guid?)c.UserId)
                .FirstOrDefaultAsync(cancellationToken);
            var bindable = DeploymentSettingsRules.CanBind(callerId, ProjectRole.Owner, ownerId);
            if (bindable.IsFailure)
            {
                return (null, bindable.Error! with { Field = "connectionId" });
            }

            connectionId = requested;
        }

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ProjectLocks.LockProjectAsync(db, projectId, cancellationToken);

        // FR-79: the Owner role re-read under the project lock (as the target PUT).
        var callerRole = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.UserId == callerId)
            .Select(m => (ProjectRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);
        if (!callerIsAdmin && callerRole != ProjectRole.Owner)
        {
            return (null, callerRole is null
                ? new DomainError(ErrorCodes.NotFound, "The project was not found.")
                : new DomainError(ErrorCodes.Forbidden, "Your project role doesn't allow this action."));
        }

        var rows = await db.ManagedDatabases.Where(d => d.ProjectId == projectId).ToListAsync(cancellationToken);
        var row = rows.FirstOrDefault(d => d.Environment == environment);
        var linkId = request.Link?.ResourceId.Trim();

        // §4.21: the preview database is never production's (either side may be the one being set): a reset or an
        // unmerged migration would otherwise reach production. A create that adopts by name is checked by the job.
        if (mode is DatabaseSetupMode.Link or DatabaseSetupMode.External)
        {
            var wanted = new DatabaseIdentity(
                provider,
                mode == DatabaseSetupMode.Link ? linkId : null,
                mode == DatabaseSetupMode.Link && provider == ManagedDatabaseProvider.Supabase ? linkId : null,
                [request.External?.ConnectionString]);
            if (rows.Any(other => other.Environment != environment && other.Status != ManagedDatabaseStatus.Removed
                && DatabaseSeparationRules.Same(wanted, DatabaseIdentity.Of(other, secrets.Connection(other)))))
            {
                return (null, new DomainError(ErrorCodes.Validation, DatabaseSeparationRules.NotSeparateMessage)
                {
                    Field = mode == DatabaseSetupMode.Link ? "link.resourceId" : "external.connectionString",
                });
            }
        }

        var name = await NameAsync(projectId, environment, request.Create?.Name, rows, cancellationToken);
        if (row is null)
        {
            row = new ManagedDatabase { ProjectId = projectId, Environment = environment, CreatedAt = now };
            db.ManagedDatabases.Add(row);
        }

        var keepsResource = row.Provider == provider && row.ResourceId is not null && mode == DatabaseSetupMode.Link && linkId == row.ResourceId;
        if (!keepsResource)
        {
            // A different database: AIDE no longer holds its password or its connection details.
            row.ResourceId = null;
            secrets.SetPassword(row, null);
        }

        row.Provider = provider;
        row.ConnectionId = connectionId;
        row.Region = request.Create?.Region;
        row.Plan = request.Create?.Plan;
        row.ExpiresAt = null;
        row.Status = ManagedDatabaseStatus.Provisioning;
        row.UpdatedAt = now;
        var payload = new DatabaseProvisioningPayload(
            projectId, row.Id, mode!.Value, name, request.Create?.Region, request.Create?.Plan, linkId, DatabaseProvisioningPayload.SetupPhase);
        var job = jobs.Enqueue(JobTypes.DatabaseProvisioning, payload, projectId, now);
        secrets.SetConnection(row, new ManagedDatabaseConnection
        {
            Url = request.External?.ConnectionString.Trim(),
            Name = mode == DatabaseSetupMode.External ? "External" : name,
            ConnectedByUserId = callerId,
            Step = StepQueued,
            JobId = job.Id,
        });
        db.AddEvent(
            EventTypes.SettingsUpdated,
            projectId,
            new { projectId, section = "deployment", database = environment.ToWire(), provider = provider.ToWire(), changes = DatabaseChange, mode = mode.Value.ToWire() },
            actor,
            now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (null, new DomainError(ErrorCodes.Conflict, "The database was changed by someone else. Reload and try again."));
        }

        await transaction.CommitAsync(cancellationToken);
        LogQueued(logger, projectId, environment, provider, mode.Value);
        return (await CardAsync(projectId, environment, true, cancellationToken), null);
    }

    /// <summary>
    /// FR-152 <c>DELETE …/databases/:environment</c>: unlinks the database (the platform resource is never deleted). The row
    /// is kept as <c>removed</c> without a resource or secrets, so its migration runs stay; derived env vars are recomputed.
    /// </summary>
    public async Task<DomainError?> DeleteAsync(Guid projectId, DeploymentEnvironment environment, string actor, CancellationToken cancellationToken)
    {
        // Under the project lock (as PUT), so a concurrent database_provisioning job's write is a 409, not a 500.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ProjectLocks.LockProjectAsync(db, projectId, cancellationToken);
        var row = await db.ManagedDatabases.FirstOrDefaultAsync(d => d.ProjectId == projectId && d.Environment == environment, cancellationToken);
        if (row is null || Unlinked(row))
        {
            return new DomainError(ErrorCodes.NotFound, "The database was not found.");
        }

        var now = time.GetUtcNow();
        row.Status = ManagedDatabaseStatus.Removed;
        row.ResourceId = null;
        row.ConnectionId = null;
        row.ConnectionEncrypted = string.Empty;
        row.DbPasswordEncrypted = null;
        row.Region = null;
        row.Plan = null;
        row.ExpiresAt = null;
        row.UpdatedAt = now;
        db.AddEvent(
            EventTypes.SettingsUpdated,
            projectId,
            new { projectId, section = "deployment", database = environment.ToWire(), changes = DatabaseChange, cleared = true },
            actor,
            now);
        if (await SaveOrConflictAsync(cancellationToken) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(cancellationToken);
        await SyncDerivedAsync(projectId, actor, cancellationToken);
        return null;
    }

    /// <summary>
    /// FR-152 Supabase "Paused" action (<c>POST …/databases/:environment/restore</c>): <c>POST /projects/{ref}/restore</c>,
    /// then a poll job follows the project until it is available again.
    /// </summary>
    public async Task<(DeploymentDatabaseDto? Card, DomainError? Error)> RestoreAsync(Guid projectId, DeploymentEnvironment environment, string actor, CancellationToken cancellationToken)
    {
        var row = await db.ManagedDatabases.AsNoTracking().FirstOrDefaultAsync(d => d.ProjectId == projectId && d.Environment == environment, cancellationToken);
        if (row is null || Unlinked(row) || row.ResourceId is null)
        {
            return (null, new DomainError(ErrorCodes.NotFound, "The database was not found."));
        }

        if (row.Provider is not (ManagedDatabaseProvider.Supabase or ManagedDatabaseProvider.Fake))
        {
            return (null, new DomainError(ErrorCodes.DeploymentActionUnsupported, $"{ManagedDatabaseRules.DisplayName(row.Provider)} databases have no restore action."));
        }

        if (row.Status != ManagedDatabaseStatus.Paused)
        {
            return (null, new DomainError(ErrorCodes.Conflict, "Only a paused database can be restored."));
        }

        var auth = await AuthAsync(row, cancellationToken);
        if (auth.IsFailure)
        {
            return (null, auth.Error);
        }

        try
        {
            await providers.ForHost(row.Provider).RestoreAsync(auth.Value.Auth, row.ResourceId, cancellationToken);
        }
        catch (DatabaseProviderException ex)
        {
            var message = SecretRedaction.SanitizeProviderError(ex.Message);
            return (null, ex.Failure switch
            {
                DeploymentFailure.RateLimited => new DomainError(ErrorCodes.UpstreamRateLimited, message)
                {
                    RetryAfter = ex.RetryAt is { } at && at > time.GetUtcNow() ? at - time.GetUtcNow() : TimeSpan.FromMinutes(1),
                },
                DeploymentFailure.CredentialRejected => new DomainError(ErrorCodes.CredentialInvalid, message),
                _ => new DomainError(ErrorCodes.UpstreamError, message),
            });
        }

        // The provider call is made outside the lock; the row is then re-read under the project lock (as PUT), so a
        // concurrent change (the database_provisioning job, a PUT or DELETE) is a 409 CONFLICT, not a 500.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ProjectLocks.LockProjectAsync(db, projectId, cancellationToken);
        var (rowId, resourceId) = (row.Id, row.ResourceId);
        row = await db.ManagedDatabases.FirstOrDefaultAsync(d => d.Id == rowId, cancellationToken);
        if (row is null || Unlinked(row) || row.ResourceId != resourceId || row.Status != ManagedDatabaseStatus.Paused)
        {
            return (null, new DomainError(ErrorCodes.Conflict, "The database was changed by someone else. Reload and try again."));
        }

        var now = time.GetUtcNow();
        var payload = new DatabaseProvisioningPayload(
            projectId, row.Id, DatabaseSetupMode.Link, row.ResourceId, row.Region, row.Plan, row.ResourceId, DatabaseProvisioningPayload.PollPhase);
        var job = jobs.Enqueue(JobTypes.DatabaseProvisioning, payload, projectId, now);
        secrets.SetConnection(row, secrets.Connection(row) with { Step = StepWaiting, JobId = job.Id, ProvisioningStartedAt = now, ErrorMessage = null });
        row.Status = ManagedDatabaseStatus.Provisioning;
        row.UpdatedAt = now;
        db.AddEvent(
            EventTypes.SettingsUpdated,
            projectId,
            new { projectId, section = "deployment", database = environment.ToWire(), changes = DatabaseChange, restored = true },
            actor,
            now);
        if (await SaveOrConflictAsync(cancellationToken) is { } conflict)
        {
            return (null, conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return (await CardAsync(projectId, environment, true, cancellationToken), null);
    }

    /// <summary>Saves; a concurrent write (the <c>xmin</c> token) is <c>CONFLICT</c> (409), and the caller's transaction rolls back.</summary>
    private async Task<DomainError?> SaveOrConflictAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return new DomainError(ErrorCodes.Conflict, "The database was changed by someone else. Reload and try again.");
        }
    }

    /// <summary>§4.22: recomputes the derived env vars after a database change and syncs them to the targets (saved).</summary>
    public async Task SyncDerivedAsync(Guid projectId, string actor, CancellationToken cancellationToken)
    {
        var recomputed = await inventory.RecomputeDerivedAsync(projectId, await inventory.FactsAsync(projectId, cancellationToken), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await envSync.DeleteOnTargetsAsync(projectId, recomputed.Removed, cancellationToken);
        var pending = await db.DeploymentEnvVars.Where(v => v.ProjectId == projectId && (v.SyncedAt == null || v.SyncError != null)).ToListAsync(cancellationToken);
        var toPush = pending.Union(recomputed.Changed).ToList();
        if (toPush.Count > 0)
        {
            await envSync.PushToTargetsAsync(projectId, toPush, actor, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The resource name: as asked, else from the project's name; a Render preview is the production one's <c>-preview</c>.</summary>
    private async Task<string> NameAsync(Guid projectId, DeploymentEnvironment environment, string? requested, List<ManagedDatabase> rows, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return ManagedDatabaseRules.ResourceName(requested.Trim(), environment);
        }

        var production = rows.FirstOrDefault(r => r.Environment == DeploymentEnvironment.Production && !Unlinked(r));
        if (environment == DeploymentEnvironment.Preview && production is not null && secrets.Connection(production).Name is { } productionName && productionName != "External")
        {
            return ManagedDatabaseRules.RenderPreviewName(productionName);
        }

        var projectName = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken);
        return ManagedDatabaseRules.ResourceName(projectName, environment);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {Environment} database queued ({Provider}, {Mode})")]
    private static partial void LogQueued(ILogger logger, Guid projectId, DeploymentEnvironment environment, ManagedDatabaseProvider provider, DatabaseSetupMode mode);
}
