using Aide.Api.Features.Stack;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Stack;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// The project's deployment facts: the resolved stack of its current version (or <c>null</c>), the frontend pack's public
/// prefix, the targets, and the usable databases as the derivation sees them (FR-152; values hold secrets, never logged).
/// </summary>
public sealed record DeploymentFacts(StackResolution? Stack, string? PublicPrefix, IReadOnlyList<DeploymentTarget> Targets, IReadOnlyList<DerivedEnvDatabase> Databases)
{
    public DeploymentFacts(StackResolution? stack, string? publicPrefix, IReadOnlyList<DeploymentTarget> targets)
        : this(stack, publicPrefix, targets, [])
    {
    }

    public StackComposition? Composition => Stack?.Composition;

    /// <summary>§4.22 input: the composition, the prefix, the targets, the databases and the stack's database facts.</summary>
    public DerivedEnvInput DerivedInput => new(
        Composition,
        PublicPrefix,
        [.. Targets.Select(t => new DerivedEnvTarget(t.Component, t.Provider, t.Name, t.ProductionUrl, t.PreviewUrl))],
        Databases,
        DatabaseFacts(Stack));

    /// <summary>§4.22: serverless host mode (the stack requires <c>AIDE_RUN_MIGRATIONS</c>), the data pack's direct-URL need, and the pack rule for the server-side Supabase keys.</summary>
    public static DerivedEnvDatabaseFacts DatabaseFacts(StackResolution? stack) => stack is null ? DerivedEnvDatabaseFacts.None : new(
        stack.Env.Any(e => e.Key == DerivedEnvRules.RunMigrations),
        stack.Packs.FirstOrDefault(p => p.Layer == StackLayers.Data)?.Pack.Manifest.Migrate?.NeedsDirectUrl ?? false,
        stack.Env.Any(e => e.Key == DerivedEnvRules.SupabaseSecretKey));

    public override string ToString() => $"DeploymentFacts {{ Targets = {Targets.Count}, Databases = {Databases.Count} }}";
}

/// <summary>A variable AIDE no longer writes under its old platform ids: the ids to delete on the targets of its components.</summary>
public sealed record RemovedEnvVar(string Key, IReadOnlyList<string> AppliesTo, IReadOnlyDictionary<string, string>? PlatformIds);

/// <summary>The outcome of <see cref="DeploymentInventoryService.RecomputeDerivedAsync"/>: rows to push, and platform vars to delete.</summary>
public sealed record DerivedRecompute(IReadOnlyList<DeploymentEnvVar> Changed, IReadOnlyList<RemovedEnvVar> Removed);

/// <summary>
/// FR-145, FR-146, §4.22: the environment inventory of a project and its derived variables. Derived rows are computed with
/// the pure <see cref="DerivedEnvRules.Derive"/> and stored (source <c>derived</c>, per-environment values encrypted as
/// <see cref="DerivedEnvValues"/>) so they can be synced; the inventory is always computed from the current composition and
/// targets, so it is right even before a recompute.
/// </summary>
public sealed partial class DeploymentInventoryService(
    AideDbContext db,
    VersionStackLoader stacks,
    Databases.ManagedDatabaseSecrets databaseSecrets,
    DeploymentAccess access,
    TimeProvider time,
    ILogger<DeploymentInventoryService> logger)
{
    public async Task<DeploymentFacts> FactsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var stack = (await stacks.CurrentAsync(projectId, cancellationToken, readBase: false)).Stack;
        var prefix = stack?.Packs.FirstOrDefault(p => p.Layer == StackLayers.Frontend)?.Pack.Manifest.PublicEnvPrefix;
        var targets = await db.DeploymentTargets.AsNoTracking().Where(t => t.ProjectId == projectId).ToListAsync(cancellationToken);
        var databases = await db.ManagedDatabases.AsNoTracking().Where(d => d.ProjectId == projectId).ToListAsync(cancellationToken);
        return new DeploymentFacts(stack, prefix, targets, [.. databases.Select(databaseSecrets.ForDerivation).OfType<DerivedEnvDatabase>()]);
    }

    /// <summary>The inventory rows (no values).</summary>
    public async Task<IReadOnlyList<EnvInventoryRow>> RowsAsync(Guid projectId, DeploymentFacts facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var stored = await db.DeploymentEnvVars.AsNoTracking().Where(v => v.ProjectId == projectId).ToListAsync(cancellationToken);
        var derived = DerivedEnvRules.Derive(facts.DerivedInput);
        var derivedSync = stored.Where(v => v.Source == DeploymentEnvSource.Derived)
            .ToDictionary(v => v.Key, v => new EnvSyncState(v.UpdatedAt, v.SyncedAt, v.SyncError), StringComparer.Ordinal);
        var users = stored.Where(v => v.Source == DeploymentEnvSource.User).Select(ToUser).ToList();
        return EnvInventoryRules.Build(derived, derivedSync, users, facts.Stack?.Env ?? []);
    }

    public static DeploymentEnvVarDto ToDto(EnvInventoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new DeploymentEnvVarDto(
            row.Key, row.AppliesTo, row.Environments, row.Sensitive, row.Public, row.Source, row.DerivedFrom, row.Description, row.HasValue, row.UpdatedAt, row.SyncedAt, row.SyncError);
    }

    /// <summary>A stored user variable as an inventory row.</summary>
    public static DeploymentEnvVarDto ToDto(DeploymentEnvVar v)
    {
        var u = ToUser(v);
        return ToDto(new EnvInventoryRow(u.Key, u.AppliesTo, u.Environments, v.Sensitive, v.Public, v.Source, v.DerivedFrom, null, true, v.UpdatedAt, v.SyncedAt, v.SyncError));
    }

    /// <summary>
    /// §4.22: writes the derived rows for the current composition and targets (tracked): new or changed values are saved and
    /// marked unsynced, rows AIDE no longer derives are removed (a user row on a derived key becomes derived: user variables
    /// can't override derived keys: the replacement is logged and recorded as an activity event, and the user row's platform
    /// vars are deleted and written again rather than updated in place). Returns the rows to push and the platform vars to
    /// delete (removed rows, replaced user rows). The caller saves.
    /// </summary>
    public async Task<DerivedRecompute> RecomputeDerivedAsync(Guid projectId, DeploymentFacts facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var now = time.GetUtcNow();
        var derived = DerivedEnvRules.Derive(facts.DerivedInput).Where(d => d.Values.Count > 0).ToDictionary(d => d.Key, StringComparer.Ordinal);
        var stored = await db.DeploymentEnvVars.Where(v => v.ProjectId == projectId).ToListAsync(cancellationToken);
        var changed = new List<DeploymentEnvVar>();
        var removed = new List<RemovedEnvVar>();
        foreach (var stale in stored.Where(v => v.Source == DeploymentEnvSource.Derived && !derived.ContainsKey(v.Key)))
        {
            // FR-145: deleted on the platform too (by the caller's sync), so a stale API_ORIGIN doesn't linger in Vercel.
            removed.Add(new RemovedEnvVar(stale.Key, stale.AppliesTo, stale.VercelEnvIds));
            db.DeploymentEnvVars.Remove(stale);
        }

        foreach (var d in derived.Values)
        {
            var json = DerivedEnvValues.Serialize(d.Values);
            var row = stored.FirstOrDefault(v => v.Key == d.Key);
            if (row is null)
            {
                row = new DeploymentEnvVar { ProjectId = projectId, Key = d.Key };
                db.DeploymentEnvVars.Add(row);
            }
            else if (row.Source == DeploymentEnvSource.Derived
                && access.Values.TryUnprotect(row.ValueEncrypted, out var current) && current == json
                && row.AppliesTo.SequenceEqual(d.AppliesTo.Select(a => a.ToWire())))
            {
                continue;
            }
            else if (row.Source == DeploymentEnvSource.User)
            {
                // §4.22: a key that became derived replaces the Owner's value. Never silently: log it, record an event,
                // and delete the user's platform vars so the derived ones are written fresh (not updated by stale ids).
                LogUserValueReplaced(logger, projectId, d.Key);
                db.AddEvent(
                    EventTypes.SettingsUpdated,
                    projectId,
                    new { projectId, section = "deployment_env", changes = new[] { d.Key }, replacedUserValue = true, derivedFrom = d.DerivedFrom },
                    SystemActor.Name,
                    now);
                removed.Add(new RemovedEnvVar(row.Key, row.AppliesTo, row.VercelEnvIds));
                row.VercelEnvIds = null;
            }

            row.Source = DeploymentEnvSource.Derived;
            row.DerivedFrom = d.DerivedFrom;
            row.AppliesTo = [.. d.AppliesTo.Select(a => a.ToWire())];
            row.Targets = [.. d.Values.Keys.Order().Select(e => e.ToWire())];
            row.Public = d.Public;
            row.Sensitive = d.Sensitive;
            row.ValueEncrypted = access.Values.Protect(json);
            row.UpdatedAt = now;
            row.SyncedAt = null;
            row.SyncError = null;
            changed.Add(row);
        }

        LogRecomputed(logger, projectId, derived.Count, changed.Count);
        return new DerivedRecompute(changed, removed);
    }

    private static EnvUserVar ToUser(DeploymentEnvVar v) => new(
        v.Key,
        [.. v.AppliesTo.Select(a => EnumStrings.TryParse<DeploymentComponent>(a, out var c) ? (DeploymentComponent?)c : null).OfType<DeploymentComponent>()],
        [.. DeploymentSettingsService.Targets(v.Targets).Distinct().Order()],
        v.Sensitive,
        v.Public,
        v.UpdatedAt,
        v.SyncedAt,
        v.SyncError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: {Key} is now derived by AIDE; the Owner's value was replaced")]
    private static partial void LogUserValueReplaced(ILogger logger, Guid projectId, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {Derived} derived environment variable(s), {Changed} changed")]
    private static partial void LogRecomputed(ILogger logger, Guid projectId, int derived, int changed);
}
