using Aide.Domain.Databases;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Databases;

/// <summary>
/// §4.21: the preview database must never be production's (<see cref="DatabaseSeparationRules"/>). Checked when a database
/// is linked or set up (either environment), and again right before AIDE resets or migrates a preview database.
/// </summary>
public sealed class DatabaseSeparation(AideDbContext db, ManagedDatabaseSecrets secrets)
{
    /// <summary>
    /// Whether <paramref name="identity"/>, a database of <paramref name="environment"/>, is the project's database of the
    /// other environment (removed rows don't count).
    /// </summary>
    public async Task<bool> SharesOtherEnvironmentAsync(Guid projectId, DeploymentEnvironment environment, DatabaseIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var others = await db.ManagedDatabases.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.Environment != environment && d.Status != ManagedDatabaseStatus.Removed)
            .ToListAsync(cancellationToken);
        return others.Any(other => DatabaseSeparationRules.Same(identity, DatabaseIdentity.Of(other, secrets.Connection(other))));
    }

    /// <summary>
    /// Whether AIDE may reset or run unmerged migrations on <paramref name="row"/> as the preview database: it is a
    /// <c>preview</c> row and is not production's database.
    /// </summary>
    public async Task<bool> IsSeparatePreviewAsync(ManagedDatabase row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Environment == DeploymentEnvironment.Preview
            && !await SharesOtherEnvironmentAsync(row.ProjectId, row.Environment, DatabaseIdentity.Of(row, secrets.Connection(row)), cancellationToken);
    }
}
