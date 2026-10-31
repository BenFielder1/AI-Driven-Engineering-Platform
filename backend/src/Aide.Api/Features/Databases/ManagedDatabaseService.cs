using Aide.Api.Features.Deployments;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Databases;

/// <summary>
/// FR-152, FR-153, §4.21: a project's managed databases (one per environment): the Settings → Deployment cards, setting
/// one up (a <c>database_provisioning</c> job does the platform work; progress is on the card), unlinking, and the Supabase
/// restore action. Connection strings, keys and passwords are encrypted and never returned or logged.
/// </summary>
public sealed partial class ManagedDatabaseService(
    AideDbContext db,
    ManagedDatabaseSecrets secrets,
    ISecretProtectors protectors,
    IDatabaseProviderResolver providers,
    DeploymentInventoryService inventory,
    DeploymentEnvSync envSync,
    JobEnqueuer jobs,
    TimeProvider time,
    ILogger<ManagedDatabaseService> logger)
{
    public const string StepQueued = "queued";
    public const string StepCreating = "creating";
    public const string StepLinking = "linking";
    public const string StepWaiting = "waiting";
    public const string StepSyncingEnv = "syncing_env";

    public const string WarningRenderFreeExpiry = "render_free_expiry";
    public const string WarningSupabaseFreePausing = "supabase_free_pausing";
    public const string WarningSupabaseProjectLimit = "supabase_free_project_limit";
    public const string WarningPreviewUsesProduction = "preview_uses_production_database";

    private static readonly DeploymentEnvironment[] Environments = [DeploymentEnvironment.Production, DeploymentEnvironment.Preview];

    /// <summary>FR-152: the database cards (production, preview) when the composition hosts a database or one is set.</summary>
    public async Task<IReadOnlyList<DeploymentDatabaseDto>> CardsAsync(Guid projectId, StackCompositionView composition, ProjectRole? callerRole, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var rows = await db.ManagedDatabases.AsNoTracking().Where(d => d.ProjectId == projectId).ToListAsync(cancellationToken);
        var host = ManagedDatabaseRules.ProviderForHost(composition.DatabaseHost);
        if (host is null && rows.All(Unlinked))
        {
            return [];
        }

        var cards = new List<DeploymentDatabaseDto>();
        foreach (var environment in Environments)
        {
            cards.Add(await CardAsync(projectId, environment, rows, host, composition.DatabaseHost, callerRole == ProjectRole.Owner, cancellationToken));
        }

        return cards;
    }

    /// <summary>One card, read fresh.</summary>
    public async Task<DeploymentDatabaseDto> CardAsync(Guid projectId, DeploymentEnvironment environment, bool canBind, CancellationToken cancellationToken)
    {
        var rows = await db.ManagedDatabases.AsNoTracking().Where(d => d.ProjectId == projectId).ToListAsync(cancellationToken);
        var facts = await inventory.FactsAsync(projectId, cancellationToken);
        var hostOption = facts.Composition?.Deploy.DatabaseHost;
        return await CardAsync(projectId, environment, rows, ManagedDatabaseRules.ProviderForHost(hostOption), hostOption, canBind, cancellationToken);
    }

    private async Task<DeploymentDatabaseDto> CardAsync(
        Guid projectId, DeploymentEnvironment environment, List<ManagedDatabase> rows, ManagedDatabaseProvider? host, string? hostOption, bool canBind, CancellationToken cancellationToken)
    {
        var row = rows.FirstOrDefault(r => r.Environment == environment && !Unlinked(r));
        var provider = row?.Provider ?? host ?? ManagedDatabaseProvider.External;
        var connection = row is null ? null : secrets.Connection(row);
        var now = time.GetUtcNow();
        var status = row is null ? "not_configured" : ManagedDatabaseRules.Effective(row.Status, row.ExpiresAt, now).ToWire();

        DeploymentTargetBindingDto? binding = null;
        IReadOnlyList<string> missing = [];
        if (row?.ConnectionId is { } connectionId)
        {
            var platform = await db.PlatformConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
            var userId = connection?.ConnectedByUserId ?? platform?.UserId;
            var user = userId is null ? null
                : await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Id, u.Name }).FirstOrDefaultAsync(cancellationToken);
            if (platform is not null && user is not null)
            {
                binding = new DeploymentTargetBindingDto(
                    platform.Id, platform.Label, platform.Last4, platform.Metadata.TeamSlug ?? platform.AccountName, new DeploymentUserDto(user.Id, user.Name), platform.Status);
            }

            if (platform is { Status: PlatformConnectionStatus.InsufficientScope })
            {
                missing = platform.Metadata.MissingPermissions;
            }
        }

        var warnings = new List<string>();
        var freePlan = row?.Plan is null || string.Equals(row.Plan, "free", StringComparison.OrdinalIgnoreCase);
        if (provider == ManagedDatabaseProvider.Render && freePlan)
        {
            warnings.Add(WarningRenderFreeExpiry);
        }

        if (provider == ManagedDatabaseProvider.Supabase && freePlan)
        {
            warnings.Add(WarningSupabaseFreePausing);
        }

        if (provider == ManagedDatabaseProvider.Supabase && connection?.Step == ErrorCodes.PlatformPlanLimit)
        {
            warnings.Add(WarningSupabaseProjectLimit);
        }

        if (environment == DeploymentEnvironment.Preview && row is null && rows.Any(r => r.Environment == DeploymentEnvironment.Production && !Unlinked(r)))
        {
            warnings.Add(WarningPreviewUsesProduction);
        }

        return new DeploymentDatabaseDto(
            environment,
            provider.ToWire(),
            status,
            hostOption,
            host is { } h && (h == provider || provider == ManagedDatabaseProvider.Fake),
            ManagedDatabaseRules.ConnectionProvider(provider) is not null,
            binding,
            canBind,
            row?.ResourceId,
            connection?.Name,
            row?.Region,
            row?.Plan,
            row?.ExpiresAt,
            row?.Status == ManagedDatabaseStatus.Paused,
            canBind && row?.Status == ManagedDatabaseStatus.Paused && provider is ManagedDatabaseProvider.Supabase or ManagedDatabaseProvider.Fake,
            connection?.DashboardUrl,
            row is null ? null : ManagedDatabaseRules.Summary(provider, connection),
            missing,
            row is null ? null : await ProvisioningAsync(row, connection!, cancellationToken),
            warnings,
            row?.UpdatedAt);
    }

    /// <summary>The card's provisioning progress from its newest <c>database_provisioning</c> job.</summary>
    private async Task<DatabaseProvisioningDto?> ProvisioningAsync(ManagedDatabase row, ManagedDatabaseConnection connection, CancellationToken cancellationToken)
    {
        var job = connection.JobId is { } jobId ? await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken) : null;
        if (job is null)
        {
            return null;
        }

        return row.Status switch
        {
            ManagedDatabaseStatus.Provisioning => new DatabaseProvisioningDto(
                job.Id, job.Status == JobStatus.Running ? "running" : "queued", connection.Step ?? StepQueued, null, null, job.CreatedAt, null),
            ManagedDatabaseStatus.Error => new DatabaseProvisioningDto(
                job.Id, "failed", null, connection.Step ?? ErrorCodes.DatabaseProvisioningFailed, connection.ErrorMessage ?? job.LastError, job.CreatedAt, job.CompletedAt),
            _ => new DatabaseProvisioningDto(job.Id, "succeeded", null, null, null, job.CreatedAt, job.CompletedAt),
        };
    }

    /// <summary>
    /// The provider's credential for <paramref name="row"/>: the bound connection's token and account (Supabase: the
    /// organisation slug). External needs none; the Fake uses the bound token when there is one (for its <c>fake_*</c>
    /// behaviours), else <c>fake</c>. <c>DEPLOYMENT_NOT_CONFIGURED</c> without a usable connection.
    /// </summary>
    public async Task<Result<(DatabaseAuth Auth, PlatformConnection? Connection)>> AuthAsync(ManagedDatabase row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        var expected = ManagedDatabaseRules.ConnectionProvider(row.Provider);
        var connection = row.ConnectionId is { } id && expected is { } p
            ? await db.PlatformConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.Provider == p, cancellationToken)
            : null;
        if (expected is null || (connection is null && providers.UsesFake))
        {
            return Result.Success<(DatabaseAuth, PlatformConnection?)>((new DatabaseAuth(providers.UsesFake ? "fake" : "none", null), null));
        }

        if (connection is null)
        {
            return Result.Failure<(DatabaseAuth, PlatformConnection?)>(new DomainError(
                ErrorCodes.DeploymentNotConfigured, $"The {ManagedDatabaseRules.DisplayName(row.Provider)} connection is gone: set the database up again with one of your connections."));
        }

        if (!protectors.ForPurpose(SecretPurposes.PlatformToken).TryUnprotect(connection.SecretEncrypted, out var token) || token.Length == 0)
        {
            return Result.Failure<(DatabaseAuth, PlatformConnection?)>(new DomainError(
                ErrorCodes.CredentialInvalid, "The bound platform token can't be decrypted; its owner needs to add it again."));
        }

        var account = row.Provider == ManagedDatabaseProvider.Supabase
            ? connection.Metadata.Accounts.FirstOrDefault(a => a.Id == connection.AccountId)?.Slug ?? connection.AccountId
            : connection.AccountId;
        return Result.Success<(DatabaseAuth, PlatformConnection?)>((new DatabaseAuth(token, account), connection));
    }

    /// <summary>Where the app runs (§4.21): the Render service and the Railway target (backend first, else the frontend).</summary>
    public async Task<DatabaseHostContext> HostContextAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var targets = await db.DeploymentTargets.AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.ResourceId != null)
            .OrderByDescending(t => t.Component == DeploymentComponent.Backend)
            .ToListAsync(cancellationToken);
        var render = targets.FirstOrDefault(t => t.Provider == DeploymentProvider.Render);
        var railway = targets.FirstOrDefault(t => t.Provider == DeploymentProvider.Railway);
        return new DatabaseHostContext(render?.ResourceId, railway?.ResourceId, railway?.PreviewResourceId);
    }

    /// <summary>A row that was unlinked (DELETE keeps it as <c>removed</c> without a resource, for the migration history).</summary>
    internal static bool Unlinked(ManagedDatabase row) => row.Status == ManagedDatabaseStatus.Removed && row.ResourceId is null && string.IsNullOrEmpty(row.ConnectionEncrypted);
}

/// <summary>What the cards need from the composition: its <c>databaseHost</c> option id.</summary>
public sealed record StackCompositionView(string? DatabaseHost);
