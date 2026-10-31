using Aide.Domain.Entities;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Databases;

/// <summary>FR-153, §7 Migrations page: a project's Supabase BaaS migration runs (newest first), and recording a run's result.</summary>
public sealed class MigrationRunReader(AideDbContext db)
{
    public const int MaxRuns = 200;

    /// <summary>The project's runs, newest first (at most 200), optionally of one release version.</summary>
    public async Task<IReadOnlyList<MigrationRunDto>> ListAsync(Guid projectId, Guid? releaseVersionId, CancellationToken cancellationToken)
    {
        var query = db.DatabaseMigrationRuns.AsNoTracking().Where(r => r.ProjectId == projectId);
        if (releaseVersionId is { } versionId)
        {
            query = query.Where(r => r.ReleaseVersionId == versionId);
        }

        var runs = await query.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).Take(MaxRuns).ToListAsync(cancellationToken);
        var providers = await ProvidersAsync(projectId, cancellationToken);
        return [.. runs.Select(r => ToDto(r, providers.GetValueOrDefault(r.ManagedDatabaseId)))];
    }

    /// <summary>One run of the project; <c>null</c> when it isn't the project's.</summary>
    public async Task<MigrationRunDto?> GetAsync(Guid projectId, Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.DatabaseMigrationRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.ProjectId == projectId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var provider = await db.ManagedDatabases.AsNoTracking().Where(d => d.Id == run.ManagedDatabaseId).Select(d => (ManagedDatabaseProvider?)d.Provider).FirstOrDefaultAsync(cancellationToken);
        return ToDto(run, provider);
    }

    /// <summary>Records a runner result on its run (status, files, error, advisor findings, finish time); the caller saves.</summary>
    public static void Record(DatabaseMigrationRun run, MigrationRunResult result, DateTimeOffset finishedAt)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(result);
        run.Status = result.Status;
        run.Applied = [.. run.Applied, .. result.Applied];
        run.ErrorCode = result.ErrorCode;
        run.ErrorMessage = result.ErrorMessage;
        run.AdvisorFindings = result.Advisors;
        run.FinishedAt = finishedAt;
    }

    public static MigrationRunDto ToDto(DatabaseMigrationRun run, ManagedDatabaseProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new MigrationRunDto(
            run.Id,
            run.Environment,
            run.ManagedDatabaseId,
            provider,
            run.ReleaseId,
            run.ReleaseVersionId,
            run.GitSha,
            run.Status,
            [.. run.Applied.Select(a => new MigrationFileDto(a.Version, a.Name, a.Ms))],
            run.Applied.Sum(a => a.Ms),
            run.ErrorCode,
            run.ErrorMessage,
            [.. run.AdvisorFindings.Select(f => new AdvisorFindingDto(f.Name, f.Level, f.Title, f.Detail, f.Remediation))],
            run.StartedAt,
            run.FinishedAt);
    }

    private async Task<Dictionary<Guid, ManagedDatabaseProvider?>> ProvidersAsync(Guid projectId, CancellationToken cancellationToken) =>
        await db.ManagedDatabases.AsNoTracking()
            .Where(d => d.ProjectId == projectId)
            .ToDictionaryAsync(d => d.Id, d => (ManagedDatabaseProvider?)d.Provider, cancellationToken);
}
