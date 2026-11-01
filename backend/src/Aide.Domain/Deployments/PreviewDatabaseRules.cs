using Aide.Domain.Entities;

namespace Aide.Domain.Deployments;

/// <summary>How AIDE resets a preview database (FR-148).</summary>
public enum PreviewResetMethod
{
    /// <summary>No preview database AIDE can reset (none, removed, or preview uses the production database).</summary>
    None = 0,

    /// <summary>
    /// Supabase (and External, Fake): AIDE connects and drops the <c>public</c> schema and the migration history, recreates
    /// <c>public</c> with Supabase's default grants, then (Supabase BaaS) re-applies the migrations of the base commit.
    /// </summary>
    ResetSchema = 1,

    /// <summary>
    /// Render: as <see cref="ResetSchema"/> over Render's external connection string (the internal one only works inside
    /// Render); the next backend preview's pre-deploy re-applies every migration.
    /// </summary>
    ResetSchemaExternal = 2,

    /// <summary>
    /// Railway: the preview Postgres is only reachable on Railway's private network and AIDE has no verified API call to
    /// wipe its volume, so the Owner empties it in Railway; AIDE records the reset (clearing the divergence) and the next
    /// backend preview's pre-deploy re-applies every migration.
    /// </summary>
    Manual = 3,
}

/// <summary>
/// FR-148, §4.21: preview database divergence and reset, pure.
/// <list type="bullet">
/// <item>A path is a migration file when it sits under a <c>migrations</c> folder (any depth, any case: Supabase
/// <c>supabase/migrations</c>, EF Core <c>Migrations/</c>, Prisma <c>prisma/migrations/&lt;dir&gt;/migration.sql</c>, Django
/// <c>&lt;app&gt;/migrations</c>) or under Alembic's <c>versions</c>; generated companions (EF <c>*.Designer.cs</c> and
/// <c>*ModelSnapshot.cs</c>, Prisma <c>migration_lock.toml</c>, Python <c>__init__.py</c>, Drizzle <c>meta/</c>) aren't.</item>
/// <item>The migrations applied to the preview database are those of the last commit its migrations ran for (the last
/// successful preview migration run, or the last ready backend preview whose pre-deploy ran), since the last reset. Any of
/// them missing from the branch head means the database diverged: <c>PREVIEW_DATABASE_DIVERGED</c>.</item>
/// </list>
/// </summary>
public static class PreviewDatabaseRules
{
    private static readonly string[] CompanionSuffixes = [".designer.cs", "modelsnapshot.cs", "migration_lock.toml", "__init__.py", ".md", ".gitkeep"];

    /// <summary>Whether <paramref name="path"/> is a migration file (see the class remarks).</summary>
    public static bool IsMigrationFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/');
        if (segments.Length < 2)
        {
            return false;
        }

        var lower = normalized.ToLowerInvariant();
        if (CompanionSuffixes.Any(lower.EndsWith) || segments.Any(s => s.Equals("meta", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var folders = segments[..^1];
        return folders.Any(s => s.Equals("migrations", StringComparison.OrdinalIgnoreCase))
            || (folders.Length >= 2 && folders[^1].Equals("versions", StringComparison.OrdinalIgnoreCase) && folders.Any(s => s.Equals("alembic", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The migration files among <paramref name="paths"/> (ordinal order).</summary>
    public static IReadOnlyList<string> MigrationFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return [.. paths.Where(IsMigrationFile).Select(p => p.Replace('\\', '/').Trim('/')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// FR-148: the applied migration files (at the commit last migrated) that the branch head no longer has; empty when
    /// the preview database hasn't diverged. Unknown sides (<c>null</c>) never diverge.
    /// </summary>
    public static IReadOnlyList<string> Missing(IReadOnlyList<string>? appliedCommitPaths, IReadOnlyList<string>? headPaths)
    {
        if (appliedCommitPaths is null || headPaths is null)
        {
            return [];
        }

        var head = MigrationFiles(headPaths).ToHashSet(StringComparer.Ordinal);
        return [.. MigrationFiles(appliedCommitPaths).Where(p => !head.Contains(p))];
    }

    /// <summary>The user-facing description of a divergence (no SQL): the missing files, at most five named.</summary>
    public static string Describe(IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        var named = string.Join(", ", missing.Take(5));
        var more = missing.Count > 5 ? $" and {missing.Count - 5} more" : string.Empty;
        return $"The preview database has migrations this branch no longer has ({named}{more}). Reset the preview database to continue.";
    }

    /// <summary>FR-148: how the preview database of <paramref name="provider"/> is reset (<c>null</c> provider: no preview database).</summary>
    public static PreviewResetMethod ResetMethod(ManagedDatabaseProvider? provider, ManagedDatabaseStatus? status) =>
        provider is null || status is null or ManagedDatabaseStatus.Removed or ManagedDatabaseStatus.Provisioning
            ? PreviewResetMethod.None
            : provider switch
            {
                ManagedDatabaseProvider.Supabase or ManagedDatabaseProvider.External or ManagedDatabaseProvider.Fake => PreviewResetMethod.ResetSchema,
                ManagedDatabaseProvider.Render => PreviewResetMethod.ResetSchemaExternal,
                ManagedDatabaseProvider.Railway => PreviewResetMethod.Manual,
                _ => PreviewResetMethod.None,
            };
}

/// <summary>
/// FR-150, §4.21: the rollback dialog's migration warning, pure. Rolling back never rolls back the database, so a backend
/// deployment is "older than the latest applied migration" when it was created before the latest production migration:
/// <list type="bullet">
/// <item>Supabase BaaS: the latest successful production migration run that applied at least one file (its finish time).</item>
/// <item>Host mode (the ORM migrates in the backend's pre-deploy, FR-154): the latest ready production deploy of the backend
/// (kind <c>deploy</c>) of another commit whose migration files differ, approximated by: any later deploy of a commit
/// whose pre-deploy ran; the warning is conservative (it may warn when that deploy applied nothing).</item>
/// </list>
/// Only backend deployments (and the Next.js full-stack frontend in host mode) carry the warning; others are <c>null</c>.
/// </summary>
public static class MigrationWarningRules
{
    /// <summary><c>true</c>/<c>false</c> when known; <c>null</c> when no migration was ever applied (or not applicable).</summary>
    public static bool? OlderThanLatestMigration(DateTimeOffset deploymentCreatedAt, DateTimeOffset? latestMigrationAt) =>
        latestMigrationAt is { } applied ? deploymentCreatedAt < applied : null;

    /// <summary>The latest migration time from either source (the later one wins).</summary>
    public static DateTimeOffset? Latest(DateTimeOffset? baasRunAt, DateTimeOffset? hostDeployAt) =>
        (baasRunAt, hostDeployAt) switch
        {
            (null, null) => null,
            ({ } a, null) => a,
            (null, { } b) => b,
            ({ } a, { } b) => a > b ? a : b,
        };
}
