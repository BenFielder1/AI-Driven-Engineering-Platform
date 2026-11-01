namespace Aide.Domain.Databases;

/// <summary>
/// FR-152, §4.21, §5: what AIDE knows about how to reach a managed database, stored as JSON in
/// <c>managed_databases.connection_encrypted</c> (Data Protection ciphertext; never returned or logged). The database
/// password is separate (<c>db_password_encrypted</c>). Which fields are set depends on the provider:
/// <list type="bullet">
/// <item>Supabase: <see cref="SupabaseRef"/>, the pooler (<see cref="PoolerHost"/>, <see cref="PoolerUser"/>,
/// <see cref="DatabaseName"/>), the publishable and secret keys, and the Auth URLs last applied.</item>
/// <item>Render: <see cref="Url"/> (<c>internalConnectionString</c>) and <see cref="ExternalUrl"/>.</item>
/// <item>Railway: <see cref="RailwayService"/> (apps reference <c>${{&lt;service&gt;.DATABASE_URL}}</c>).</item>
/// <item>External and Fake: <see cref="Url"/>.</item>
/// </list>
/// </summary>
public sealed record ManagedDatabaseConnection
{
    /// <summary>The connection URI apps use (Render internal, External, Fake); <c>null</c> for Supabase and Railway.</summary>
    public string? Url { get; init; }

    /// <summary>Render's <c>externalConnectionString</c> (for consumers outside Render).</summary>
    public string? ExternalUrl { get; init; }

    /// <summary>The Railway Postgres service name (the reference variable's prefix).</summary>
    public string? RailwayService { get; init; }

    public string? SupabaseRef { get; init; }

    /// <summary>The Supavisor pooler host (<c>aws-&lt;n&gt;-&lt;region&gt;.pooler.supabase.com</c>, which can't be derived).</summary>
    public string? PoolerHost { get; init; }

    /// <summary>The pooler user (<c>postgres.&lt;ref&gt;</c>).</summary>
    public string? PoolerUser { get; init; }

    public string? DatabaseName { get; init; }

    /// <summary>Supabase <c>sb_publishable_…</c> (or a migrated v2 project's legacy <c>anon</c> key).</summary>
    public string? PublishableKey { get; init; }

    /// <summary>Supabase <c>sb_secret_…</c> (or a migrated v2 project's legacy <c>service_role</c> key).</summary>
    public string? SecretKey { get; init; }

    /// <summary>The Auth <c>site_url</c> AIDE last applied (re-applied when the frontend URLs change).</summary>
    public string? AuthSiteUrl { get; init; }

    /// <summary>The Auth <c>uri_allow_list</c> AIDE last applied.</summary>
    public string? AuthAllowList { get; init; }

    /// <summary>The resource's display name on the platform.</summary>
    public string? Name { get; init; }

    /// <summary>A link to the resource in the platform's dashboard.</summary>
    public string? DashboardUrl { get; init; }

    /// <summary>The provisioning job's last step or the failure code (for the card while <c>provisioning</c> or <c>error</c>).</summary>
    public string? Step { get; init; }

    /// <summary>The failure message (no secrets) when the database is <c>error</c>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The Owner who provisioned or linked the database ("connected by" on the card).</summary>
    public Guid? ConnectedByUserId { get; init; }

    /// <summary>The newest <c>database_provisioning</c> job; only it may apply its result.</summary>
    public Guid? JobId { get; init; }

    /// <summary>When provisioning started (for the 10-minute ceiling).</summary>
    public DateTimeOffset? ProvisioningStartedAt { get; init; }

    /// <summary>The Supabase project URL, <c>https://&lt;ref&gt;.supabase.co</c>.</summary>
    public string? SupabaseUrl => SupabaseRef is null ? null : ManagedDatabaseRules.SupabaseProjectUrl(SupabaseRef);

    /// <summary>Never prints the connection URLs or the keys (they hold passwords and the secret key).</summary>
    public override string ToString() =>
        $"ManagedDatabaseConnection {{ Name = {Name}, SupabaseRef = {SupabaseRef}, RailwayService = {RailwayService}, Step = {Step}, JobId = {JobId} }}";
}

/// <summary>A Supabase security advisor finding (FR-153 <c>GET /projects/{ref}/advisors/security</c>), without SQL or secrets.</summary>
public sealed record AdvisorFinding(string Name, string Level, string Title, string? Detail, string? Remediation);
