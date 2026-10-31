using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Platforms;
using Aide.Domain.Stack;

namespace Aide.Domain.Databases;

/// <summary>What a database PUT asks for (FR-152): create, link an existing resource, or store an External connection string.</summary>
public enum DatabaseSetupMode
{
    Create = 0,
    Link = 1,
    External = 2,
}

/// <summary>A provider's state of a database, mapped to AIDE's (§4.21): the status and whether polling ends.</summary>
public sealed record DatabaseStateMapping(ManagedDatabaseStatus Status, bool Terminal);

/// <summary>
/// FR-153: what an environment's database means for a Supabase BaaS migration step. <see cref="Absent"/> skips the step;
/// <see cref="Unavailable"/> is a real database that can't take migrations now, so the step runs and fails.
/// </summary>
public enum DatabaseMigrationTarget
{
    Absent = 0,
    Usable = 1,
    Unavailable = 2,
}

/// <summary>What a provider reports for an existing database (FR-155 sweep): its state and its expiry (<c>null</c>: it doesn't expire).</summary>
public sealed record DatabaseLiveState(ManagedDatabaseStatus Status, DateTimeOffset? ExpiresAt);

/// <summary>
/// FR-155: what the expiry sweep does to a database: its next stored status and expiry, and the reason Owners are told
/// (<c>null</c>: a silent change).
/// </summary>
public sealed record DatabaseSweepChange(ManagedDatabaseStatus Status, DateTimeOffset? ExpiresAt, string? ExpiringReason);

/// <summary>
/// FR-152, FR-153, §4.21: the pure rules of managed databases: which provider the composition allows, the PUT checks,
/// the provider state mappings (Supabase, Render), Supabase's create body and pooler URLs, Render's names and expiry, the
/// Railway reference, the provisioning poll (10 s, 10-minute ceiling), and the card's connection summary (no secrets).
/// </summary>
public static partial class ManagedDatabaseRules
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>§4.21: provisioning gives up after 10 minutes (<c>DATABASE_PROVISIONING_TIMEOUT</c>).</summary>
    public static readonly TimeSpan ProvisioningCeiling = TimeSpan.FromMinutes(10);

    /// <summary>§4.21: a Render free database is <c>expiring</c> within 7 days of <c>expiresAt</c>.</summary>
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(7);

    public const int SupabaseSessionPort = 5432;
    public const int SupabaseTransactionPort = 6543;
    public const string DefaultDatabaseName = "postgres";
    public const string RailwayServiceName = "Postgres";
    public const string RenderPostgresVersion = "17";
    public const int ConnectionStringMaxLength = 2000;
    public const int ResourceIdMaxLength = 200;

    /// <summary>The Supabase smart region groups (<c>region_selection: {type: "smartGroup", code}</c>).</summary>
    public static IReadOnlyList<string> SupabaseSmartGroups { get; } = ["americas", "emea", "apac"];

    /// <summary>The provider of a composition's <c>databaseHost</c> option; <c>null</c> for none or an unknown option.</summary>
    public static ManagedDatabaseProvider? ProviderForHost(string? databaseHost) => databaseHost switch
    {
        StackIds.SupabaseHost => ManagedDatabaseProvider.Supabase,
        "render-postgres" => ManagedDatabaseProvider.Render,
        "railway-postgres" => ManagedDatabaseProvider.Railway,
        "external" => ManagedDatabaseProvider.External,
        _ => null,
    };

    public static string DisplayName(ManagedDatabaseProvider provider) => provider switch
    {
        ManagedDatabaseProvider.Supabase => "Supabase",
        ManagedDatabaseProvider.Render => "Render Postgres",
        ManagedDatabaseProvider.Railway => "Railway Postgres",
        ManagedDatabaseProvider.External => "External",
        _ => "Fake",
    };

    /// <summary>The platform connection a provider uses; <c>null</c> for External and Fake.</summary>
    public static PlatformProvider? ConnectionProvider(ManagedDatabaseProvider provider) => provider switch
    {
        ManagedDatabaseProvider.Supabase => PlatformProvider.Supabase,
        ManagedDatabaseProvider.Render => PlatformProvider.Render,
        ManagedDatabaseProvider.Railway => PlatformProvider.Railway,
        _ => null,
    };

    /// <summary>
    /// FR-152 PUT checks that need no platform call: the provider is the composition's database host
    /// (<c>STACK_INCOMPATIBLE</c>; the Fake only in Fake mode), exactly one of create/link/external fits the provider, a
    /// connection where one is needed, a resource id, a Supabase region or smart group, and an External connection string
    /// (<c>VALIDATION</c>).
    /// </summary>
    public static Result Validate(
        ManagedDatabaseProvider provider,
        StackComposition? composition,
        bool fakeMode,
        Guid? connectionId,
        DatabaseSetupMode? mode,
        string? region,
        string? linkResourceId,
        string? connectionString)
    {
        var host = ProviderForHost(composition?.Deploy.DatabaseHost);
        if (host != provider && !(fakeMode && provider == ManagedDatabaseProvider.Fake))
        {
            var reason = host is { } h
                ? $"The stack hosts its database on {DisplayName(h)}, not {DisplayName(provider)}."
                : "The stack has no database host; choose one in Settings → Stack first.";
            return Result.Failure(StackCompatibility.IncompatibleError([new StackViolation(StackChoices.DatabaseHost, provider.ToWire(), "FR-152", reason)]));
        }

        var problems = new List<string>();
        if (mode is null)
        {
            problems.Add("create: Provision a database, link one or enter a connection string (one of them).");
        }
        else if (provider == ManagedDatabaseProvider.External && mode != DatabaseSetupMode.External)
        {
            problems.Add("external: An External database takes a connection string.");
        }
        else if (provider != ManagedDatabaseProvider.External && mode == DatabaseSetupMode.External)
        {
            problems.Add($"external: {DisplayName(provider)} databases are provisioned or linked, not entered.");
        }

        if (ConnectionProvider(provider) is { } platform && !fakeMode && connectionId is null)
        {
            problems.Add($"connectionId: Choose one of your {PlatformConnectionRules.DisplayName(platform)} connections.");
        }

        if (mode == DatabaseSetupMode.Link && (string.IsNullOrWhiteSpace(linkResourceId) || linkResourceId.Length > ResourceIdMaxLength || linkResourceId.Any(char.IsControl)))
        {
            problems.Add($"link.resourceId: Choose a database (up to {ResourceIdMaxLength} characters).");
        }

        if (mode == DatabaseSetupMode.Create && provider == ManagedDatabaseProvider.Supabase && !fakeMode && !IsSupabaseRegion(region))
        {
            problems.Add("create.region: Choose a Supabase region or one of americas, emea, apac.");
        }

        if (mode == DatabaseSetupMode.External && !IsConnectionString(connectionString))
        {
            problems.Add("external.connectionString: Enter a postgres:// or postgresql:// URI, or an ADO.NET connection string.");
        }

        return problems.Count == 0 ? Result.Success() : Projects.ProjectRules.ToResult(problems, "The database settings are invalid.");
    }

    /// <summary>A Supabase region code or smart group.</summary>
    public static bool IsSupabaseRegion(string? region) =>
        region is not null && (PlatformRegions.Supabase.Contains(region, StringComparer.Ordinal) || SupabaseSmartGroups.Contains(region, StringComparer.Ordinal));

    /// <summary>FR-152: a <c>postgres://</c>/<c>postgresql://</c> URI with a host, or an ADO.NET string (e.g. SQL Server's <c>Server=…</c>).</summary>
    public static bool IsConnectionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > ConnectionStringMaxLength || value.Any(char.IsControl))
        {
            return false;
        }

        if (value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host);
        }

        return AdoHostPattern().IsMatch(value);
    }

    /// <summary>
    /// §4.21 Supabase create body: <c>{name, organization_slug, db_pass, region_selection}</c>, where the selection is
    /// <c>{type: "smartGroup", code}</c> for americas/emea/apac and <c>{type: "specific", code}</c> otherwise. The deprecated
    /// <c>plan</c>, <c>region</c> and <c>organization_id</c> are never sent.
    /// </summary>
    public static IReadOnlyDictionary<string, object> SupabaseCreateBody(string name, string organizationSlug, string dbPass, string region)
    {
        ArgumentNullException.ThrowIfNull(region);
        var type = SupabaseSmartGroups.Contains(region, StringComparer.Ordinal) ? "smartGroup" : "specific";
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["organization_slug"] = organizationSlug,
            ["db_pass"] = dbPass,
            ["region_selection"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = type, ["code"] = region },
        };
    }

    /// <summary>
    /// §4.21 Supabase project status → AIDE's: <c>ACTIVE_HEALTHY</c> available (the health check still decides),
    /// <c>INACTIVE</c>/<c>PAUSING</c>/<c>PAUSE_FAILED</c> paused, <c>INIT_FAILED</c>/<c>RESTORE_FAILED</c> error,
    /// <c>REMOVED</c>/<c>GOING_DOWN</c> removed, anything else (<c>COMING_UP</c>, <c>RESTORING</c>, <c>UPGRADING</c>, …) provisioning.
    /// </summary>
    public static DatabaseStateMapping SupabaseState(string? status) => status?.ToUpperInvariant() switch
    {
        "ACTIVE_HEALTHY" => new(ManagedDatabaseStatus.Available, true),
        "INACTIVE" or "PAUSING" or "PAUSE_FAILED" => new(ManagedDatabaseStatus.Paused, true),
        "INIT_FAILED" or "RESTORE_FAILED" => new(ManagedDatabaseStatus.Error, true),
        "REMOVED" or "GOING_DOWN" => new(ManagedDatabaseStatus.Removed, true),
        _ => new(ManagedDatabaseStatus.Provisioning, false),
    };

    /// <summary>§4.21 Supabase health: every service (db, pooler, auth, rest) reported <c>ACTIVE_HEALTHY</c> (or <c>healthy: true</c>).</summary>
    public static bool SupabaseHealthy(IReadOnlyList<(string Name, string? Status, bool? Healthy)> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        string[] required = ["db", "pooler", "auth", "rest"];
        return required.All(name => services.Any(s => s.Name == name && (s.Healthy == true || s.Status == "ACTIVE_HEALTHY")));
    }

    /// <summary>
    /// §4.21 Render Postgres status → AIDE's: <c>available</c> (and <c>maintenance_*</c>) available, or <c>expiring</c>
    /// within 7 days of <paramref name="expiresAt"/>; <c>suspended</c> paused; <c>recovery_failed</c> error; anything else
    /// (<c>creating</c>, …) provisioning.
    /// </summary>
    public static DatabaseStateMapping RenderState(string? status, DateTimeOffset? expiresAt, DateTimeOffset now) => status?.ToLowerInvariant() switch
    {
        "available" or "maintenance_scheduled" or "maintenance_in_progress" or "config_restart" =>
            new(IsExpiring(expiresAt, now) ? ManagedDatabaseStatus.Expiring : ManagedDatabaseStatus.Available, true),
        "suspended" => new(ManagedDatabaseStatus.Paused, true),
        "recovery_failed" => new(ManagedDatabaseStatus.Error, true),
        _ => new(ManagedDatabaseStatus.Provisioning, false),
    };

    /// <summary>A database that expires within 7 days (Render free: 30 days plus a 14-day grace period).</summary>
    public static bool IsExpiring(DateTimeOffset? expiresAt, DateTimeOffset now) => expiresAt is { } at && at - now <= ExpiryWarning;

    /// <summary>The status a stored available database shows now (<c>expiring</c> once within 7 days of its expiry).</summary>
    public static ManagedDatabaseStatus Effective(ManagedDatabaseStatus status, DateTimeOffset? expiresAt, DateTimeOffset now) =>
        status == ManagedDatabaseStatus.Available && IsExpiring(expiresAt, now) ? ManagedDatabaseStatus.Expiring : status;

    /// <summary><c>DATABASE_EXPIRING.reason</c>: the database's <c>expiresAt</c> is within 7 days.</summary>
    public const string ExpiringReasonExpiry = "expiry";

    /// <summary><c>DATABASE_EXPIRING.reason</c>: the Supabase project is paused.</summary>
    public const string ExpiringReasonPaused = "paused";

    /// <summary>
    /// FR-155 expiry sweep (<c>null</c>: nothing changes). The stored status makes the warning once-only: <c>available →
    /// expiring</c> warns (within 7 days of the expiry) and <c>available → paused</c> warns (Supabase <c>INACTIVE</c>), while a
    /// database already there stays quiet unless its date moved within the window, which announces the new date. A renewed
    /// expiry or a restored project goes back to <c>available</c> silently, so a later expiry or pause warns again; a date
    /// the provider moved is always stored. <paramref name="live"/> is what the provider just said (<c>null</c>: it couldn't
    /// be asked or has nothing to say, so the stored expiry stands).
    /// </summary>
    public static DatabaseSweepChange? ExpirySweep(
        ManagedDatabaseProvider provider, ManagedDatabaseStatus status, DateTimeOffset? expiresAt, DatabaseLiveState? live, DateTimeOffset now)
    {
        if (provider == ManagedDatabaseProvider.Supabase)
        {
            return (status, live?.Status) switch
            {
                (ManagedDatabaseStatus.Available or ManagedDatabaseStatus.Expiring, ManagedDatabaseStatus.Paused) => new(ManagedDatabaseStatus.Paused, expiresAt, ExpiringReasonPaused),
                (ManagedDatabaseStatus.Paused, ManagedDatabaseStatus.Available) => new(ManagedDatabaseStatus.Available, expiresAt, null),
                _ => null,
            };
        }

        var known = live is null ? expiresAt : live.ExpiresAt;
        return status switch
        {
            ManagedDatabaseStatus.Available when IsExpiring(known, now) => new(ManagedDatabaseStatus.Expiring, known, ExpiringReasonExpiry),
            ManagedDatabaseStatus.Available when known != expiresAt => new(ManagedDatabaseStatus.Available, known, null),
            ManagedDatabaseStatus.Expiring when !IsExpiring(known, now) => new(ManagedDatabaseStatus.Available, known, null),
            ManagedDatabaseStatus.Expiring when known != expiresAt => new(ManagedDatabaseStatus.Expiring, known, ExpiringReasonExpiry),
            _ => null,
        };
    }

    /// <summary>
    /// FR-153, §4.21: available and expiring databases take migrations. A paused, failed or still provisioning one that has
    /// a real resource or connection is <see cref="DatabaseMigrationTarget.Unavailable"/>: its migrations must fail visibly
    /// and block the merge, never count as "nothing to migrate". Removed, or never set up: absent.
    /// </summary>
    public static DatabaseMigrationTarget MigrationTarget(ManagedDatabaseStatus status, bool hasDatabase) => status switch
    {
        ManagedDatabaseStatus.Available or ManagedDatabaseStatus.Expiring => DatabaseMigrationTarget.Usable,
        ManagedDatabaseStatus.Paused or ManagedDatabaseStatus.Error or ManagedDatabaseStatus.Provisioning when hasDatabase => DatabaseMigrationTarget.Unavailable,
        _ => DatabaseMigrationTarget.Absent,
    };

    /// <summary>§4.21: the provisioning poll gives up 10 minutes after it started.</summary>
    public static bool TimedOut(DateTimeOffset startedAt, DateTimeOffset now) => now - startedAt >= ProvisioningCeiling;

    /// <summary>The Supabase project URL, <c>https://&lt;ref&gt;.supabase.co</c>.</summary>
    public static string SupabaseProjectUrl(string projectRef) => $"https://{projectRef}.supabase.co";

    /// <summary>
    /// §4.21: a Supabase pooler URI, <c>postgresql://&lt;user&gt;:&lt;password&gt;@&lt;host&gt;:&lt;port&gt;/&lt;db&gt;</c>
    /// (session <c>:5432</c>, transaction <c>:6543</c>), user and password percent-encoded. The direct host
    /// <c>db.&lt;ref&gt;.supabase.co</c> is never used (IPv6-only).
    /// </summary>
    public static string PoolerUrl(string host, string user, string password, int port, string? database = null) =>
        $"postgresql://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@{host}:{port}/{Uri.EscapeDataString(database ?? DefaultDatabaseName)}";

    /// <summary>
    /// §4.22: the URI with <c>pgbouncer=true</c> (Prisma on the transaction pooler: prepared statements off) added to its query.
    /// </summary>
    public static string WithPgbouncer(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.Contains("pgbouncer=", StringComparison.Ordinal) ? url : url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "pgbouncer=true";
    }

    /// <summary>§4.21: the Railway reference apps use, <c>${{Postgres.DATABASE_URL}}</c> (private network).</summary>
    public static string RailwayReference(string? serviceName) => $"${{{{{(string.IsNullOrWhiteSpace(serviceName) ? RailwayServiceName : serviceName)}.DATABASE_URL}}}}";

    /// <summary>§4.21: the preview Render Postgres is a second instance named <c>&lt;name&gt;-preview</c>.</summary>
    public static string RenderPreviewName(string productionName)
    {
        ArgumentNullException.ThrowIfNull(productionName);
        return productionName.EndsWith("-preview", StringComparison.Ordinal) ? productionName : $"{productionName}-preview";
    }

    /// <summary>
    /// A database resource name from the project's name: lowercase letters, digits and '-', at most 40 characters, starting
    /// with a letter (<c>aide-db</c> when nothing is left); the preview adds <c>-preview</c>.
    /// </summary>
    public static string ResourceName(string? projectName, DeploymentEnvironment environment)
    {
        var slug = NonSlug().Replace((projectName ?? string.Empty).ToLowerInvariant(), "-").Trim('-');
        slug = slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
        if (slug.Length == 0 || !char.IsAsciiLetterLower(slug[0]))
        {
            slug = slug.Length == 0 ? "aide-db" : $"db-{slug}";
        }

        return environment == DeploymentEnvironment.Preview ? RenderPreviewName(slug) : slug;
    }

    /// <summary>Render's <c>databaseName</c>: the resource name with '-' as '_' (letters, digits and underscores).</summary>
    public static string RenderDatabaseName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var db = NonIdentifier().Replace(name.ToLowerInvariant().Replace('-', '_'), string.Empty);
        return db.Length == 0 || !char.IsAsciiLetterLower(db[0]) ? $"db_{db}" : db;
    }

    /// <summary>
    /// The card's connection summary: <c>host:port/database</c> of a URI (never the user or password), the pooler host for
    /// Supabase, the reference for Railway; <c>null</c> when nothing is known.
    /// </summary>
    public static string? Summary(ManagedDatabaseProvider provider, ManagedDatabaseConnection? connection)
    {
        if (connection is null)
        {
            return null;
        }

        if (provider == ManagedDatabaseProvider.Supabase && connection.PoolerHost is { } pooler)
        {
            return $"{pooler}:{SupabaseSessionPort}/{connection.DatabaseName ?? DefaultDatabaseName} (session pooler)";
        }

        if (provider == ManagedDatabaseProvider.Railway)
        {
            return RailwayReference(connection.RailwayService);
        }

        if (connection.Url is not { } url)
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            var port = uri.IsDefaultPort || uri.Port < 0 ? string.Empty : $":{uri.Port}";
            return $"{uri.Host}{port}{uri.AbsolutePath}";
        }

        var match = AdoHostPattern().Match(url);
        return match.Success ? match.Groups["host"].Value.Trim() : "connection string";
    }

    [GeneratedRegex(@"(?:^|;)\s*(?:Server|Data Source|Host|Address|Addr)\s*=\s*(?<host>[^;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AdoHostPattern();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonSlug();

    [GeneratedRegex("[^a-z0-9_]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonIdentifier();
}

/// <summary>
/// FR-153: the Supabase Auth URLs from the frontend target: <c>site_url</c> is the frontend production URL (the preview
/// project: the preview URL, else production), and <c>uri_allow_list</c> (comma-separated) holds the production URL, the
/// preview URL, the frontend host's preview URL pattern and <c>http://localhost:&lt;dev port&gt;/**</c>. Pure.
/// </summary>
public static class SupabaseAuthRules
{
    /// <summary>The frontend pack's dev server port: Next.js 3000, Angular 4200, Vite-based packs 5173.</summary>
    public static int DevPort(string? frontendPack) => frontendPack switch
    {
        StackIds.NextJs => 3000,
        "fe-angular" => 4200,
        _ => 5173,
    };

    /// <summary>The Auth URLs, or <c>null</c> while the frontend has no URL.</summary>
    public static (string SiteUrl, string AllowList)? AuthUrls(Deployments.DerivedEnvTarget? frontend, DeploymentEnvironment environment, string? frontendPack = null)
    {
        var production = Deployments.DerivedEnvRules.Origin(frontend?.ProductionUrl);
        var preview = Deployments.DerivedEnvRules.Origin(frontend?.PreviewUrl);
        var site = environment == DeploymentEnvironment.Preview ? preview ?? production : production ?? preview;
        if (site is null || frontend is null)
        {
            return null;
        }

        var allow = new[]
        {
            production is null ? null : $"{production}/**",
            preview is null ? null : $"{preview}/**",
            Deployments.DerivedEnvRules.PreviewPattern(frontend.Provider, frontend.Name) is { } pattern ? $"{pattern}/**" : null,
            $"http://localhost:{DevPort(frontendPack)}/**",
        };
        return (site, string.Join(',', allow.OfType<string>().Distinct(StringComparer.Ordinal)));
    }
}
