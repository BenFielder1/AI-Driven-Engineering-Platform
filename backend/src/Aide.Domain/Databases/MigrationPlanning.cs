using System.Text;
using System.Text.RegularExpressions;

namespace Aide.Domain.Databases;

/// <summary>A migration file at a commit: <c>supabase/migrations/&lt;version&gt;_&lt;name&gt;.sql</c>, its version, name and SQL.</summary>
public sealed record MigrationFile(string Path, string Version, string Name, string Sql);

/// <summary>A row of <c>supabase_migrations.schema_migrations</c>: version, name and the statements recorded (may be empty).</summary>
public sealed record RecordedMigration(string Version, string? Name, IReadOnlyList<string> Statements);

/// <summary>Why a recorded version no longer matches the repository: its file <c>changed</c> or is <c>missing</c>; or two files share a version (<c>duplicate</c>).</summary>
public sealed record MigrationDivergence(string Version, string? Name, string Reason);

/// <summary>
/// A migration file whose SQL the working copy couldn't read (binary, too large, or the repository is gone): a pending
/// file (<paramref name="Recorded"/> false) can't be applied, and a recorded one can't be checked for changes.
/// </summary>
public sealed record UnreadableMigration(string Path, string Version, string Name, bool Recorded);

/// <summary>
/// The runner's plan (§4.21): the pending files in version order, the divergences (any one makes the run
/// <c>MIGRATION_HISTORY_DIVERGED</c>), the unreadable files (any one makes the run <c>MIGRATION_FAILED</c> before anything
/// is applied; they are never in <see cref="Pending"/>), and the paths ignored because they aren't <c>&lt;version&gt;_&lt;name&gt;.sql</c>.
/// </summary>
public sealed record MigrationPlan(
    IReadOnlyList<MigrationFile> Pending, IReadOnlyList<MigrationDivergence> Diverged, IReadOnlyList<string> Ignored, IReadOnlyList<UnreadableMigration> Unreadable)
{
    public bool HasDiverged => Diverged.Count > 0;

    public bool HasUnreadable => Unreadable.Count > 0;
}

/// <summary>
/// FR-153, §4.21, D-V4-12: planning a Supabase BaaS migration run, pure. As the Supabase CLI:
/// <list type="bullet">
/// <item>Files are <c>supabase/migrations/&lt;digits&gt;_&lt;name&gt;.sql</c> (directly in the folder); the version is the digits.
/// Other files are ignored.</item>
/// <item>Pending: versions not recorded, applied in version order (numeric, then ordinal).</item>
/// <item>A recorded version is never edited or re-applied. If its file disappeared (<c>missing</c>) or its SQL differs from
/// the recorded statements (<c>changed</c>; compared ignoring whitespace and statement separators, so the CLI's split
/// statements and AIDE's whole-file statement both match), the history has diverged. A recorded row without statements
/// (e.g. <c>supabase migration repair</c>) is only checked for presence.</item>
/// </list>
/// </summary>
public static partial class MigrationPlanning
{
    public const string Folder = "supabase/migrations";

    /// <summary>The file at <paramref name="path"/> as a migration, or <c>null</c> when it isn't one.</summary>
    public static MigrationFile? Parse(string path, string sql)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith(Folder + "/", StringComparison.Ordinal))
        {
            return null;
        }

        var file = normalized[(Folder.Length + 1)..];
        var match = FileName().Match(file);
        return match.Success ? new MigrationFile(normalized, match.Groups["version"].Value, match.Groups["name"].Value, sql ?? string.Empty) : null;
    }

    /// <summary>
    /// Plans a run. A file whose SQL is <c>null</c> couldn't be read: it is never treated as missing, changed or applied, but
    /// reported in <see cref="MigrationPlan.Unreadable"/>.
    /// </summary>
    public static MigrationPlan Plan(IReadOnlyList<(string Path, string? Sql)> files, IReadOnlyList<RecordedMigration> recorded)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(recorded);
        var parsed = new List<MigrationFile>();
        var ignored = new List<string>();
        var unreadablePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, sql) in files)
        {
            if (Parse(path, sql ?? string.Empty) is { } file)
            {
                parsed.Add(file);
                if (sql is null)
                {
                    unreadablePaths.Add(file.Path);
                }
            }
            else
            {
                ignored.Add(path);
            }
        }

        var diverged = new List<MigrationDivergence>();
        foreach (var duplicate in parsed.GroupBy(f => f.Version, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            diverged.Add(new MigrationDivergence(duplicate.Key, string.Join(", ", duplicate.Select(f => f.Name)), "duplicate"));
        }

        var byVersion = parsed.GroupBy(f => f.Version, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var unreadable = new List<UnreadableMigration>();
        foreach (var row in recorded.OrderBy(r => r.Version, VersionComparer.Instance))
        {
            if (!byVersion.TryGetValue(row.Version, out var file))
            {
                diverged.Add(new MigrationDivergence(row.Version, row.Name, "missing"));
            }
            else if (unreadablePaths.Contains(file.Path))
            {
                unreadable.Add(new UnreadableMigration(file.Path, file.Version, file.Name, Recorded: true));
            }
            else if (row.Statements.Count > 0 && Fingerprint(string.Join('\n', row.Statements)) != Fingerprint(file.Sql))
            {
                diverged.Add(new MigrationDivergence(row.Version, row.Name ?? file.Name, "changed"));
            }
        }

        var done = recorded.Select(r => r.Version).ToHashSet(StringComparer.Ordinal);
        var pending = new List<MigrationFile>();
        foreach (var file in byVersion.Values.Where(f => !done.Contains(f.Version)).OrderBy(f => f.Version, VersionComparer.Instance))
        {
            if (unreadablePaths.Contains(file.Path))
            {
                unreadable.Add(new UnreadableMigration(file.Path, file.Version, file.Name, Recorded: false));
            }
            else
            {
                pending.Add(file);
            }
        }

        return new MigrationPlan(pending, diverged, ignored, [.. unreadable.OrderBy(u => u.Version, VersionComparer.Instance)]);
    }

    /// <summary>The statements AIDE records for a file: the whole file as one statement (the CLI splits; both compare equal).</summary>
    public static IReadOnlyList<string> Statements(MigrationFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return [file.Sql];
    }

    /// <summary>A divergence as the run's error message (no SQL).</summary>
    public static string Describe(IReadOnlyList<MigrationDivergence> diverged)
    {
        ArgumentNullException.ThrowIfNull(diverged);
        return string.Join("; ", diverged.Select(d => d.Reason switch
        {
            "missing" => $"{d.Version}_{d.Name} is recorded in the database but its file is gone",
            "changed" => $"{d.Version}_{d.Name} changed after it was applied",
            _ => $"version {d.Version} is used by more than one file ({d.Name})",
        }));
    }

    /// <summary>The unreadable files as the run's error message (paths only, at most ten named).</summary>
    public static string DescribeUnreadable(IReadOnlyList<UnreadableMigration> unreadable)
    {
        ArgumentNullException.ThrowIfNull(unreadable);
        const int MaxNamed = 10;
        var named = unreadable.Take(MaxNamed).Select(u => u.Recorded
            ? $"{u.Path} is recorded in the database but couldn't be read, so it can't be checked for changes"
            : $"{u.Path} couldn't be read, so it can't be applied");
        var more = unreadable.Count > MaxNamed ? $"; and {unreadable.Count - MaxNamed} more" : string.Empty;
        return string.Join("; ", named) + more + " (binary, too large, or the repository is unavailable). Nothing was applied.";
    }

    /// <summary>SQL without whitespace and statement separators, for comparing recorded and current text.</summary>
    internal static string Fingerprint(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        foreach (var ch in sql)
        {
            if (!char.IsWhiteSpace(ch) && ch != ';')
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^(?<version>[0-9]+)_(?<name>[^/]+)\.sql$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FileName();

    /// <summary>Versions are digit strings: shorter is smaller, then ordinal (so <c>0003</c> &lt; <c>20240101…</c>).</summary>
    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = (x ?? string.Empty).TrimStart('0');
            var b = (y ?? string.Empty).TrimStart('0');
            var byLength = a.Length.CompareTo(b.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a, b) is var c && c != 0 ? c : string.CompareOrdinal(x, y);
        }
    }
}
