using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Databases;

/// <summary>
/// FR-152, FR-153, §4.21: the <c>database_provisioning</c> job of one database. Phase <c>setup</c> creates or links the
/// resource (AIDE generates the password for a Supabase or Railway create, and resets a linked Supabase project's when it
/// doesn't hold it), then phase <c>poll</c> reads its state every 10 seconds (deferring itself, so polling survives
/// restarts) until it is available, within 10 minutes (<c>DATABASE_PROVISIONING_TIMEOUT</c>). Then the connection details
/// are stored encrypted, a Supabase project's Auth URLs are set from the frontend URLs, and the derived env vars are
/// recomputed and synced. Refusals make the database <c>error</c> with the code on the card; a platform 5xx retries; a
/// rate limit waits for its reset. Only the database's newest job applies its result.
/// </summary>
public sealed partial class DatabaseProvisioningJobHandler(
    AideDbContext db,
    ManagedDatabaseService databases,
    ManagedDatabaseSecrets secrets,
    IDatabaseProviderResolver providers,
    DatabaseSeparation separation,
    TimeProvider time,
    ILogger<DatabaseProvisioningJobHandler> logger) : IJobHandler
{
    private static readonly string[] DatabaseChange = ["database"];

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (TryPayload(job.Payload) is not { } payload)
        {
            throw new JobFailedException("The database provisioning job payload can't be read.", transient: false, ErrorCodes.Validation);
        }

        var row = await db.ManagedDatabases.FirstOrDefaultAsync(d => d.Id == payload.DatabaseId && d.ProjectId == payload.ProjectId, cancellationToken);
        var connection = row is null ? null : secrets.Connection(row);
        if (row is null || connection is null || connection.JobId != job.Id || row.Status != ManagedDatabaseStatus.Provisioning)
        {
            LogSkipped(logger, payload.DatabaseId);
            return;
        }

        var auth = await databases.AuthAsync(row, cancellationToken);
        if (auth.IsFailure)
        {
            await FailAsync(row, connection, auth.Error!.Code, auth.Error.Message, cancellationToken);
        }

        var provider = providers.ForHost(row.Provider);
        var host = await databases.HostContextAsync(row.ProjectId, cancellationToken);
        var now = time.GetUtcNow();
        try
        {
            if (payload.Phase == DatabaseProvisioningPayload.SetupPhase)
            {
                connection = await SetupAsync(row, connection, payload, provider, auth.Value.Auth, host, now, cancellationToken);
                throw Defer(now, payload with { Phase = DatabaseProvisioningPayload.PollPhase }, "Waiting for the database to become available.");
            }

            if (ManagedDatabaseRules.TimedOut(connection.ProvisioningStartedAt ?? now, now))
            {
                await FailAsync(row, connection, ErrorCodes.DatabaseProvisioningTimeout, "The database took too long to become available (10 minutes).", cancellationToken);
            }

            var poll = await provider.PollAsync(
                auth.Value.Auth, new DatabaseRef(row.ResourceId, row.Environment, connection.Name, secrets.Password(row), connection, host), cancellationToken);
            if (!poll.Terminal)
            {
                secrets.SetConnection(row, (poll.Connection ?? connection) with { Step = StepOf(poll) });
                row.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                throw Defer(now, payload, poll.Detail ?? "Waiting for the database.");
            }

            if (poll.Status is ManagedDatabaseStatus.Error or ManagedDatabaseStatus.Removed)
            {
                await FailAsync(row, connection, ErrorCodes.DatabaseProvisioningFailed, poll.Detail ?? "The platform reported the database as failed.", cancellationToken);
            }

            await CompleteAsync(row, connection, poll, provider, auth.Value.Auth, now, cancellationToken);
        }
        catch (DatabaseProviderException ex) when (ex.Failure == DeploymentFailure.RateLimited)
        {
            var until = JobPolicy.DeferUntil(now, ex.RetryAt ?? now + TimeSpan.FromMinutes(1));
            throw new JobDeferredException($"{ErrorCodes.PlatformRateLimited}: {ManagedDatabaseRules.DisplayName(ex.Provider)} is rate limiting requests; provisioning waits until {until:u}.", until);
        }
        catch (DatabaseProviderException ex) when (ex.Failure == DeploymentFailure.Upstream)
        {
            throw new JobFailedException(SecretRedaction.SanitizeProviderError(ex.Message), transient: true, ErrorCodes.UpstreamError);
        }
        catch (DatabaseProviderException ex)
        {
            var code = ex.Failure switch
            {
                DeploymentFailure.CredentialRejected => ErrorCodes.CredentialInvalid,
                DeploymentFailure.Forbidden => ErrorCodes.ConnectionScopeInsufficient,
                DeploymentFailure.PlanLimit => ErrorCodes.PlatformPlanLimit,
                _ => ErrorCodes.DatabaseProvisioningFailed,
            };
            await MarkConnectionAsync(auth.Value.Connection?.Id, row.ProjectId, ex, now, cancellationToken);
            await FailAsync(row, connection, code, ex.Message, cancellationToken);
        }
    }

    private async Task<ManagedDatabaseConnection> SetupAsync(
        ManagedDatabase row, ManagedDatabaseConnection connection, DatabaseProvisioningPayload payload, IDatabaseProvider provider, DatabaseAuth auth,
        DatabaseHostContext host, DateTimeOffset now, CancellationToken cancellationToken)
    {
        secrets.SetConnection(row, connection with { Step = payload.Mode == DatabaseSetupMode.Create ? ManagedDatabaseService.StepCreating : ManagedDatabaseService.StepLinking });
        await db.SaveChangesAsync(cancellationToken);

        // §4.21: the other environment's database may have been linked since the PUT: never touch it (a linked Supabase
        // project would get a new password).
        if (payload.Mode == DatabaseSetupMode.Link)
        {
            var linked = new DatabaseIdentity(row.Provider, payload.ResourceId, row.Provider == ManagedDatabaseProvider.Supabase ? payload.ResourceId : null, []);
            await EnsureSeparateAsync(row, connection, linked, cancellationToken);
        }

        // §4.21: AIDE generates the password of what it creates, and resets a linked Supabase project's it doesn't hold.
        var needsPassword = payload.Mode == DatabaseSetupMode.Create
            ? row.Provider is ManagedDatabaseProvider.Supabase or ManagedDatabaseProvider.Railway
            : payload.Mode == DatabaseSetupMode.Link && row.Provider == ManagedDatabaseProvider.Supabase && secrets.Password(row) is null;
        var password = needsPassword ? ManagedDatabaseSecrets.GeneratePassword() : null;
        var result = await provider.SetupAsync(
            auth,
            new DatabaseSetupRequest(row.ProjectId, row.Environment, payload.Mode, payload.Name, payload.Region, payload.Plan, payload.ResourceId, password, host),
            cancellationToken);

        // §4.21: a create can adopt an existing resource by name; it must not be the other environment's database.
        await EnsureSeparateAsync(row, connection, new DatabaseIdentity(row.Provider, result.ResourceId, null, []), cancellationToken);
        row.ResourceId = result.ResourceId;
        row.Region = result.Region ?? row.Region;
        row.Plan = result.Plan ?? row.Plan;
        if (result.PasswordSet && password is not null)
        {
            secrets.SetPassword(row, password);
        }

        var updated = connection with
        {
            Name = result.Name,
            DashboardUrl = result.DashboardUrl,
            Step = ManagedDatabaseService.StepWaiting,
            ProvisioningStartedAt = now,
            ErrorMessage = null,
        };
        secrets.SetConnection(row, updated);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        LogSetUp(logger, row.ProjectId, row.Environment, row.Provider);
        return updated;
    }

    private async Task CompleteAsync(
        ManagedDatabase row, ManagedDatabaseConnection connection, DatabasePoll poll, IDatabaseProvider provider, DatabaseAuth auth, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = (poll.Connection ?? connection) with { Step = ManagedDatabaseService.StepSyncingEnv, JobId = connection.JobId, ErrorMessage = null };

        // §4.21: checked again with the connection details the platform returned, before they are saved.
        await EnsureSeparateAsync(row, connection, DatabaseIdentity.Of(row, stored), cancellationToken);
        row.Status = poll.Status == ManagedDatabaseStatus.Expiring ? ManagedDatabaseStatus.Available : poll.Status;
        row.ExpiresAt = poll.ExpiresAt;
        row.Region = poll.Region ?? row.Region;
        row.Plan = poll.Plan ?? row.Plan;
        row.UpdatedAt = now;

        // FR-153: Auth URLs from the frontend target (best effort: a refusal is logged, the database stays available).
        if (stored.SupabaseRef is not null && row.ResourceId is not null)
        {
            stored = await databases.ApplyAuthUrlsAsync(row, stored, provider, auth, cancellationToken);
        }

        secrets.SetConnection(row, stored with { Step = null });
        await db.SaveChangesAsync(cancellationToken);
        await databases.SyncDerivedAsync(row.ProjectId, SystemActor.Name, cancellationToken);
        db.AddEvent(
            EventTypes.SettingsUpdated,
            row.ProjectId,
            new { projectId = row.ProjectId, section = "deployment", database = row.Environment.ToWire(), changes = DatabaseChange, status = row.Status.ToWire() },
            SystemActor.Name,
            now);
        await db.SaveChangesAsync(cancellationToken);
        LogAvailable(logger, row.ProjectId, row.Environment, row.Status);
    }

    /// <summary>A rejected token makes the connection <c>invalid</c>; a missing permission <c>insufficient_scope</c> with the names (shown on the card).</summary>
    private async Task MarkConnectionAsync(Guid? connectionId, Guid projectId, DatabaseProviderException ex, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (connectionId is not { } id)
        {
            return;
        }

        if (ex.Failure == DeploymentFailure.CredentialRejected)
        {
            await Connections.PlatformConnectionService.MarkInvalidAsync(db, id, projectId, SystemActor.Name, now, cancellationToken);
            return;
        }

        if (ex.Failure == DeploymentFailure.Forbidden && await db.PlatformConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken) is { } platform)
        {
            platform.Status = PlatformConnectionStatus.InsufficientScope;
            platform.Metadata = platform.Metadata with
            {
                MissingPermissions = [.. platform.Metadata.MissingPermissions.Union(ex.MissingPermissions.Count > 0 ? ex.MissingPermissions : ["unknown"], StringComparer.Ordinal)],
            };
            platform.LastValidatedAt = now;
        }
    }

    /// <summary>
    /// §4.21: when <paramref name="identity"/> is the other environment's database, this database forgets the resource and
    /// its secrets and becomes <c>error</c> (<c>VALIDATION</c>), and the job fails for good; otherwise nothing.
    /// </summary>
    private async Task EnsureSeparateAsync(ManagedDatabase row, ManagedDatabaseConnection connection, DatabaseIdentity identity, CancellationToken cancellationToken)
    {
        if (!await separation.SharesOtherEnvironmentAsync(row.ProjectId, row.Environment, identity, cancellationToken))
        {
            return;
        }

        row.ResourceId = null;
        secrets.SetPassword(row, null);
        var cleared = new ManagedDatabaseConnection { Name = connection.Name, ConnectedByUserId = connection.ConnectedByUserId, JobId = connection.JobId };
        await FailAsync(row, cleared, ErrorCodes.Validation, $"{DatabaseSeparationRules.NotSeparateMessage}: this is the {(row.Environment == DeploymentEnvironment.Preview ? "production" : "preview")} database. Choose another one.", cancellationToken);
    }

    /// <summary>Records the failure on the database (saved), then fails the job for good.</summary>
    private async Task FailAsync(ManagedDatabase row, ManagedDatabaseConnection connection, string code, string message, CancellationToken cancellationToken)
    {
        var clean = SecretRedaction.SanitizeProviderError(message);
        row.Status = ManagedDatabaseStatus.Error;
        row.UpdatedAt = time.GetUtcNow();
        secrets.SetConnection(row, connection with { Step = code, ErrorMessage = clean });
        await db.SaveChangesAsync(cancellationToken);
        LogFailed(logger, row.ProjectId, row.Environment, code);
        throw new JobFailedException(clean, transient: false, code);
    }

    private static string StepOf(DatabasePoll poll) => ManagedDatabaseService.StepWaiting;

    private static JobDeferredException Defer(DateTimeOffset now, DatabaseProvisioningPayload payload, string message) =>
        new(message, now + ManagedDatabaseRules.PollInterval, AideJson.Serialize(payload));

    public static DatabaseProvisioningPayload? TryPayload(string payload)
    {
        try
        {
            return AideJson.Deserialize<DatabaseProvisioningPayload>(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database provisioning of {DatabaseId} did nothing: it is gone, superseded or no longer provisioning")]
    private static partial void LogSkipped(ILogger logger, Guid databaseId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {Environment} database set up on {Provider}; polling")]
    private static partial void LogSetUp(ILogger logger, Guid projectId, DeploymentEnvironment environment, ManagedDatabaseProvider provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {Environment} database is {Status}")]
    private static partial void LogAvailable(ILogger logger, Guid projectId, DeploymentEnvironment environment, ManagedDatabaseStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: {Environment} database provisioning failed ({Code})")]
    private static partial void LogFailed(ILogger logger, Guid projectId, DeploymentEnvironment environment, string code);
}

/// <summary>A provisioning job that ended without succeeding (retries used up) leaves its database <c>error</c>, not <c>provisioning</c>.</summary>
public sealed class DatabaseProvisioningJobEndedHook(ManagedDatabaseSecrets secrets, TimeProvider time) : IJobEndedHook
{
    public async Task OnJobEndedAsync(AideDbContext db, JobEnded job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobType != JobTypes.DatabaseProvisioning || job.ProjectId is not { } projectId)
        {
            return;
        }

        var payload = await db.Jobs.AsNoTracking().Where(j => j.Id == job.JobId).Select(j => j.Payload).FirstOrDefaultAsync(cancellationToken);
        if (payload is null || DatabaseProvisioningJobHandler.TryPayload(payload) is not { } parsed)
        {
            return;
        }

        var row = await db.ManagedDatabases.FirstOrDefaultAsync(d => d.Id == parsed.DatabaseId && d.ProjectId == projectId, cancellationToken);
        if (row is { Status: ManagedDatabaseStatus.Provisioning } && secrets.Connection(row) is { } connection && connection.JobId == job.JobId)
        {
            row.Status = ManagedDatabaseStatus.Error;
            row.UpdatedAt = time.GetUtcNow();
            secrets.SetConnection(row, connection with { Step = ErrorCodes.UpstreamError, ErrorMessage = job.Error });
        }
    }
}
