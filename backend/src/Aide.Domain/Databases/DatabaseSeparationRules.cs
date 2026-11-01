using Aide.Domain.Entities;

namespace Aide.Domain.Databases;

/// <summary>
/// Where a Postgres database lives, from a connection string: host (lower case), port, database name and user (as given,
/// lower case; <c>null</c> when none).
/// </summary>
public sealed record DatabaseEndpoint(string Host, int Port, string Database, string? User = null)
{
    /// <summary>Whether the host is a Supabase Supavisor pooler (<c>aws-&lt;n&gt;-&lt;region&gt;.pooler.supabase.com</c>), shared by many projects.</summary>
    public bool IsSupabasePooler => Host.Contains(".pooler.supabase.", StringComparison.Ordinal);
}

/// <summary>
/// What identifies a managed database for the separation check (§4.21): its provider, the platform resource id, the
/// Supabase project ref, and the connection strings AIDE holds for it (URIs or Npgsql keyword strings).
/// </summary>
public sealed record DatabaseIdentity(ManagedDatabaseProvider Provider, string? ResourceId, string? SupabaseRef, IReadOnlyList<string?> ConnectionStrings)
{
    /// <summary>
    /// The identity of a stored database row and its decrypted connection details: its URLs and, for a Supabase project,
    /// its session pooler endpoint (pooler host and <c>postgres.&lt;ref&gt;</c> user; never the password).
    /// </summary>
    public static DatabaseIdentity Of(ManagedDatabase row, ManagedDatabaseConnection? connection)
    {
        ArgumentNullException.ThrowIfNull(row);
        var pooler = connection is { PoolerHost: { Length: > 0 } host, PoolerUser: { Length: > 0 } user }
            ? $"postgresql://{Uri.EscapeDataString(user)}@{host}:{ManagedDatabaseRules.SupabaseSessionPort}/{Uri.EscapeDataString(connection.DatabaseName ?? ManagedDatabaseRules.DefaultDatabaseName)}"
            : null;
        return new DatabaseIdentity(row.Provider, row.ResourceId, connection?.SupabaseRef, [connection?.Url, connection?.ExternalUrl, pooler]);
    }
}

/// <summary>
/// FR-148, FR-153, §4.21: the preview database must never be the production database. Unmerged migrations run on the
/// preview database and a preview reset drops its schema, so the two are compared conservatively (any match is "the same"):
/// <list type="bullet">
/// <item>the same provider with the same resource id (trimmed, case-insensitive: Supabase ref, Render <c>dpg-…</c> id,
/// Railway <c>project:service:environment</c>);</item>
/// <item>the same Supabase project ref, from any source: the ref field, a Supabase resource id, a direct host
/// <c>db.&lt;ref&gt;.supabase.co</c>, or the user <c>postgres.&lt;ref&gt;</c> on a Supabase pooler (session :5432 and
/// transaction :6543 alike);</item>
/// <item>any other connection string of one with the same host, port (default 5432) and database name (default
/// <c>postgres</c>) as any of the other's (case-insensitive). A Supabase pooler endpoint matches on host and user only
/// (any port): the pooler host alone is shared by every project in the region and never matches by itself.</item>
/// </list>
/// </summary>
public static class DatabaseSeparationRules
{
    /// <summary>The VALIDATION message when the preview database would be production's.</summary>
    public const string NotSeparateMessage = "The preview database must be separate from production";

    private const int DefaultPort = 5432;
    private const string DirectHostSuffix = ".supabase.co";
    private const string PoolerUserPrefix = "postgres.";

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are (or may be) the same database.</summary>
    public static bool Same(DatabaseIdentity a, DatabaseIdentity b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Provider == b.Provider && NormaliseId(a.ResourceId) is { } id && id == NormaliseId(b.ResourceId))
        {
            return true;
        }

        if (SupabaseRefs(a).Overlaps(SupabaseRefs(b)))
        {
            return true;
        }

        var endpoints = Endpoints(a);
        return Endpoints(b).Any(e => endpoints.Any(other => SameEndpoint(e, other)));
    }

    /// <summary>A resource id trimmed and lower-cased; <c>null</c> when blank.</summary>
    public static string? NormaliseId(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim().ToLowerInvariant();

    /// <summary>
    /// The Supabase project ref a connection names: the direct host <c>db.&lt;ref&gt;.supabase.co</c>, or the user
    /// <c>postgres.&lt;ref&gt;</c> on a Supabase pooler host; <c>null</c> otherwise.
    /// </summary>
    public static string? SupabaseRefOf(DatabaseEndpoint? endpoint)
    {
        if (endpoint is null)
        {
            return null;
        }

        if (endpoint.Host.StartsWith("db.", StringComparison.Ordinal) && endpoint.Host.EndsWith(DirectHostSuffix, StringComparison.Ordinal))
        {
            return NormaliseId(endpoint.Host["db.".Length..^DirectHostSuffix.Length]) is { } direct && !direct.Contains('.', StringComparison.Ordinal) ? direct : null;
        }

        return endpoint.IsSupabasePooler && endpoint.User is { } user && user.StartsWith(PoolerUserPrefix, StringComparison.Ordinal)
            ? NormaliseId(user[PoolerUserPrefix.Length..])
            : null;
    }

    /// <summary>
    /// The endpoint of a <c>postgres://</c>/<c>postgresql://</c> URI or an Npgsql keyword string (<c>Host=…;Port=…;Database=…;Username=…</c>);
    /// <c>null</c> when it names no host.
    /// </summary>
    public static DatabaseEndpoint? Endpoint(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
        {
            return null;
        }

        var text = connection.Trim();
        if (text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            {
                return null;
            }

            var path = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            var uriUser = uri.UserInfo.Split(':', 2)[0];
            return Of(uri.Host, uri.Port > 0 ? uri.Port : DefaultPort, path, Uri.UnescapeDataString(uriUser));
        }

        string? host = null;
        string? database = null;
        string? user = null;
        var port = DefaultPort;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
            {
                continue;
            }

            switch (pair[0].ToLowerInvariant())
            {
                case "host" or "server":
                    host = pair[1];
                    break;
                case "port" when int.TryParse(pair[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                    port = parsed;
                    break;
                case "database" or "db" or "initial catalog":
                    database = pair[1];
                    break;
                case "username" or "user id" or "userid" or "user":
                    user = pair[1];
                    break;
            }
        }

        return string.IsNullOrWhiteSpace(host) ? null : Of(host, port, database, user);
    }

    private static bool SameEndpoint(DatabaseEndpoint a, DatabaseEndpoint b) =>
        a.IsSupabasePooler || b.IsSupabasePooler
            ? a.Host == b.Host && a.User is { } user && user == b.User
            : a.Host == b.Host && a.Port == b.Port && a.Database == b.Database;

    private static List<DatabaseEndpoint> Endpoints(DatabaseIdentity identity) =>
        [.. identity.ConnectionStrings.Select(Endpoint).OfType<DatabaseEndpoint>()];

    private static HashSet<string> SupabaseRefs(DatabaseIdentity identity)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        if (NormaliseId(identity.SupabaseRef) is { } field)
        {
            refs.Add(field);
        }

        if (identity.Provider == ManagedDatabaseProvider.Supabase && NormaliseId(identity.ResourceId) is { } resource)
        {
            refs.Add(resource);
        }

        foreach (var endpoint in Endpoints(identity))
        {
            if (SupabaseRefOf(endpoint) is { } fromConnection)
            {
                refs.Add(fromConnection);
            }
        }

        return refs;
    }

    private static DatabaseEndpoint Of(string host, int port, string? database, string? user) =>
        new(
            host.Trim().TrimEnd('.').ToLowerInvariant(),
            port,
            (string.IsNullOrWhiteSpace(database) ? ManagedDatabaseRules.DefaultDatabaseName : database.Trim()).ToLowerInvariant(),
            string.IsNullOrWhiteSpace(user) ? null : user.Trim().ToLowerInvariant());
}
