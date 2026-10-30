using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// FR-145, §4.20 env sync: pushes the inventory to each target through <see cref="IDeploymentProvider.SyncEnvAsync"/>, one
/// platform variable per (key, environment): a variable reaches the targets of the components it applies to. User values
/// are the same in every environment; derived ones carry a value per environment (<see cref="DerivedEnvValues"/>). Platform
/// ids are kept per target in <c>vercel_env_ids</c> (frontend: <c>preview</c>/<c>production</c> as in v2; backend:
/// <c>backend:preview</c>/<c>backend:production</c>). Values are decrypted only to send them and are never logged or
/// returned. Changes are made on tracked entities; the caller saves.
/// </summary>
public sealed partial class DeploymentEnvSync(AideDbContext db, DeploymentAccess access, TimeProvider time, ILogger<DeploymentEnvSync> logger)
{
    /// <summary>Pushes the given vars (tracked) to <paramref name="context"/>'s target. Throws <see cref="DeploymentProviderException"/>; nothing is recorded then.</summary>
    public async Task PushAsync(DeploymentContext context, IReadOnlyList<DeploymentEnvVar> vars, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(vars);
        var component = context.Target.Component;
        var mine = vars.Where(v => AppliesTo(v, component)).ToList();
        if (mine.Count == 0 || string.IsNullOrEmpty(context.Target.ResourceId))
        {
            return;
        }

        var writes = new List<DeploymentEnvWrite>();
        var deletes = new List<string>();
        foreach (var v in mine)
        {
            if (ValuesOf(v) is not { } values)
            {
                v.SyncError = "The saved value can't be decrypted; save it again.";
                continue;
            }

            var plan = EnvSyncRules.Plan([.. values.Keys], ScopedIds(v.VercelEnvIds, component));
            writes.AddRange(plan.Writes.Select(w => new DeploymentEnvWrite(v.Key, values[w.Target], w.Target, v.Sensitive, w.ExistingVercelId)));
            deletes.AddRange(plan.Deletes.Select(d => d.VercelId));
        }

        var written = await context.Provider.SyncEnvAsync(context.Auth, context.TargetRef, new DeploymentEnvSyncRequest(writes, deletes), cancellationToken);
        var now = time.GetUtcNow();
        foreach (var v in mine.Where(v => writes.Any(w => w.Key == v.Key)))
        {
            v.VercelEnvIds = MergeIds(v.VercelEnvIds, component, written.Where(w => w.Key == v.Key).ToDictionary(w => w.Target, w => w.Id));
            v.SyncedAt = now;
            v.SyncError = null;
        }

        LogPushed(logger, component, mine.Count, writes.Count, deletes.Count);
    }

    /// <summary>
    /// FR-145 "sync on save": pushes <paramref name="vars"/> to every set-up target of the components they apply to. A
    /// failure keeps the values (each var gets <c>syncError</c>); a rejected token marks the connection invalid. Returns the
    /// first failure message, or <c>null</c>.
    /// </summary>
    public async Task<string?> PushToTargetsAsync(Guid projectId, IReadOnlyList<DeploymentEnvVar> vars, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vars);
        string? failure = null;
        var targets = await db.DeploymentTargets.AsNoTracking().Where(t => t.ProjectId == projectId && t.ResourceId != null).ToListAsync(cancellationToken);
        foreach (var target in targets.OrderBy(t => t.Component))
        {
            var applying = vars.Where(v => AppliesTo(v, target.Component)).ToList();
            if (applying.Count == 0)
            {
                continue;
            }

            var context = await access.ResolveAsync(target, cancellationToken);
            if (context.IsFailure)
            {
                continue;
            }

            try
            {
                await PushAsync(context.Value, applying, cancellationToken);
            }
            catch (DeploymentProviderException ex)
            {
                if (ex.Failure == DeploymentFailure.CredentialRejected)
                {
                    await access.MarkInvalidAsync(context.Value.Connection?.Id, projectId, actor, time.GetUtcNow(), cancellationToken);
                }

                var message = DeploymentSettingsService.Message(ex);
                foreach (var v in applying)
                {
                    v.SyncError = message;
                }

                failure ??= message;
            }
        }

        return failure;
    }

    /// <summary>
    /// FR-145, §4.22: deletes the platform vars of variables AIDE no longer writes under those ids (stale derived rows, user
    /// rows replaced by derived ones) on each bound target of their components. Best effort: a failure is logged and
    /// returned, and the vars stay on the platform (Owners see them in the platform's dashboard).
    /// </summary>
    public async Task<string?> DeleteOnTargetsAsync(Guid projectId, IReadOnlyList<RemovedEnvVar> removed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(removed);
        if (removed.Count == 0)
        {
            return null;
        }

        string? failure = null;
        var targets = await db.DeploymentTargets.AsNoTracking().Where(t => t.ProjectId == projectId && t.ResourceId != null).ToListAsync(cancellationToken);
        foreach (var target in targets.OrderBy(t => t.Component))
        {
            var ids = removed
                .Where(r => r.AppliesTo.Contains(target.Component.ToWire(), StringComparer.Ordinal))
                .SelectMany(r => ScopedIds(r.PlatformIds, target.Component).Values)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (ids.Count == 0)
            {
                continue;
            }

            var context = await access.ResolveAsync(target, cancellationToken);
            if (context.IsFailure)
            {
                continue;
            }

            try
            {
                await context.Value.Provider.SyncEnvAsync(context.Value.Auth, context.Value.TargetRef, new DeploymentEnvSyncRequest([], ids), cancellationToken);
            }
            catch (DeploymentProviderException ex)
            {
                failure ??= DeploymentSettingsService.Message(ex);
                LogDeleteFailed(logger, target.Component, ex.Failure, ex.Status);
            }
        }

        return failure;
    }

    /// <summary>
    /// Before a deployment: pushes the target's vars not yet synced (or whose last push failed). A failure is recorded on the
    /// vars (<c>syncError</c>) and logged, and the deployment goes ahead; a rejected token propagates.
    /// </summary>
    public async Task PushPendingAsync(DeploymentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pending = (await db.DeploymentEnvVars
            .Where(v => v.ProjectId == context.Target.ProjectId && (v.SyncedAt == null || v.SyncError != null))
            .OrderBy(v => v.Key)
            .ToListAsync(cancellationToken))
            .Where(v => AppliesTo(v, context.Target.Component))
            .ToList();
        try
        {
            await PushAsync(context, pending, cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure != DeploymentFailure.CredentialRejected)
        {
            foreach (var v in pending)
            {
                v.SyncError = DeploymentSettingsService.Message(ex);
            }

            LogPendingFailed(logger, ex.Failure, ex.Status);
        }
    }

    /// <summary>Another resource for <paramref name="component"/>: its vars have to be pushed to it again.</summary>
    public async Task ResetAsync(Guid projectId, DeploymentComponent component, CancellationToken cancellationToken)
    {
        foreach (var v in (await db.DeploymentEnvVars.Where(v => v.ProjectId == projectId).ToListAsync(cancellationToken)).Where(v => AppliesTo(v, component)))
        {
            v.VercelEnvIds = MergeIds(v.VercelEnvIds, component, null);
            v.SyncedAt = null;
            v.SyncError = null;
        }
    }

    /// <summary>The values per environment of a stored var (derived: per environment; user: the one value in each), or <c>null</c>.</summary>
    public IReadOnlyDictionary<DeploymentEnvironment, string>? ValuesOf(DeploymentEnvVar v)
    {
        ArgumentNullException.ThrowIfNull(v);
        if (!access.Values.TryUnprotect(v.ValueEncrypted, out var value))
        {
            return null;
        }

        if (v.Source == DeploymentEnvSource.Derived)
        {
            return DerivedEnvValues.Parse(value);
        }

        return DeploymentSettingsService.Targets(v.Targets).Distinct().ToDictionary(t => t, _ => value);
    }

    public static bool AppliesTo(DeploymentEnvVar v, DeploymentComponent component)
    {
        ArgumentNullException.ThrowIfNull(v);
        return v.AppliesTo.Contains(component.ToWire(), StringComparer.Ordinal);
    }

    /// <summary>The platform ids of <paramref name="component"/>'s target, keyed by environment wire value.</summary>
    public static IReadOnlyDictionary<string, string> ScopedIds(IReadOnlyDictionary<string, string>? ids, DeploymentComponent component)
    {
        var prefix = Prefix(component);
        return (ids ?? new Dictionary<string, string>())
            .Where(p => prefix.Length == 0 ? !p.Key.Contains(':', StringComparison.Ordinal) : p.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(p => p.Key[prefix.Length..], p => p.Value, StringComparer.Ordinal);
    }

    /// <summary><paramref name="ids"/> with <paramref name="component"/>'s entries replaced by <paramref name="written"/> (<c>null</c>: removed).</summary>
    public static IReadOnlyDictionary<string, string>? MergeIds(
        IReadOnlyDictionary<string, string>? ids, DeploymentComponent component, IReadOnlyDictionary<DeploymentEnvironment, string>? written)
    {
        var prefix = Prefix(component);
        var merged = (ids ?? new Dictionary<string, string>())
            .Where(p => prefix.Length == 0 ? p.Key.Contains(':', StringComparison.Ordinal) : !p.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var (environment, id) in written ?? new Dictionary<DeploymentEnvironment, string>())
        {
            merged[prefix + environment.ToWire()] = id;
        }

        return merged.Count == 0 ? null : merged;
    }

    private static string Prefix(DeploymentComponent component) => component == DeploymentComponent.Frontend ? string.Empty : $"{component.ToWire()}:";

    [LoggerMessage(Level = LogLevel.Information, Message = "Pushed {VarCount} environment variable(s) to the {Component} target ({Writes} writes, {Deletes} deletes)")]
    private static partial void LogPushed(ILogger logger, DeploymentComponent component, int varCount, int writes, int deletes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting replaced environment variables on the {Component} target failed: {Failure} (HTTP {Status}); they stay on the platform")]
    private static partial void LogDeleteFailed(ILogger logger, DeploymentComponent component, DeploymentFailure failure, int? status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pushing pending environment variables failed: {Failure} (HTTP {Status}); the deployment goes ahead")]
    private static partial void LogPendingFailed(ILogger logger, DeploymentFailure failure, int? status);
}
