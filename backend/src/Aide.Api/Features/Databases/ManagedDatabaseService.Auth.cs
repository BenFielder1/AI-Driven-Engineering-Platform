using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Databases;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Databases;

public sealed partial class ManagedDatabaseService
{
    /// <summary>
    /// FR-153: sets a Supabase project's Auth <c>site_url</c> and <c>uri_allow_list</c> from the frontend target
    /// (<see cref="SupabaseAuthRules"/>) when they differ from what AIDE last applied; returns the connection with the applied
    /// values (the caller saves). Best effort: a refusal is logged and the database stays as it is.
    /// </summary>
    public async Task<ManagedDatabaseConnection> ApplyAuthUrlsAsync(
        ManagedDatabase row, ManagedDatabaseConnection connection, IDatabaseProvider provider, DatabaseAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(provider);
        if (row.ResourceId is null || connection.SupabaseRef is null)
        {
            return connection;
        }

        var frontend = await db.DeploymentTargets.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ProjectId == row.ProjectId && t.Component == DeploymentComponent.Frontend, cancellationToken);
        var facts = await inventory.FactsAsync(row.ProjectId, cancellationToken);
        var urls = SupabaseAuthRules.AuthUrls(
            frontend is null ? null : new DerivedEnvTarget(frontend.Component, frontend.Provider, frontend.Name, frontend.ProductionUrl, frontend.PreviewUrl),
            row.Environment,
            facts.Composition?.Frontend?.Pack);
        if (urls is not { } applied || (applied.SiteUrl == connection.AuthSiteUrl && applied.AllowList == connection.AuthAllowList))
        {
            return connection;
        }

        try
        {
            await provider.ApplyAuthUrlsAsync(auth, row.ResourceId, applied.SiteUrl, applied.AllowList, cancellationToken);
            LogAuthUrlsApplied(logger, row.ProjectId, row.Environment);
            return connection with { AuthSiteUrl = applied.SiteUrl, AuthAllowList = applied.AllowList };
        }
        catch (DatabaseProviderException ex)
        {
            LogAuthUrlsFailed(logger, row.ProjectId, row.Environment, ex.Failure);
            return connection;
        }
    }

    /// <summary>
    /// FR-153: re-applies the Auth URLs of the project's available Supabase databases after a frontend target URL changed
    /// (called from the target's setup job). Saved.
    /// </summary>
    public async Task RefreshAuthUrlsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var rows = await db.ManagedDatabases
            .Where(d => d.ProjectId == projectId && d.ResourceId != null && d.Status == ManagedDatabaseStatus.Available)
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            var connection = secrets.Connection(row);
            if (connection.SupabaseRef is null || await AuthAsync(row, cancellationToken) is not { IsSuccess: true } auth)
            {
                continue;
            }

            var updated = await ApplyAuthUrlsAsync(row, connection, providers.ForHost(row.Provider), auth.Value.Auth, cancellationToken);
            if (updated != connection)
            {
                secrets.SetConnection(row, updated);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {Environment} Supabase Auth URLs applied")]
    private static partial void LogAuthUrlsApplied(ILogger logger, Guid projectId, DeploymentEnvironment environment);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: {Environment} Supabase Auth URLs couldn't be set ({Failure})")]
    private static partial void LogAuthUrlsFailed(ILogger logger, Guid projectId, DeploymentEnvironment environment, DeploymentFailure failure);
}
