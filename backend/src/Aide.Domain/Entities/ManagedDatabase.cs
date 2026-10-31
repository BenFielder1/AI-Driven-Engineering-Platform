namespace Aide.Domain.Entities;

/// <summary>
/// FR-152, §4.21, §5: a project's database for one environment (table <c>managed_databases</c>, unique per
/// (project, environment)): a Supabase project, a Render or Railway Postgres, an External connection string, or the Fake.
/// <see cref="ConnectionEncrypted"/> is Data Protection ciphertext of the connection JSON (URIs, keys, pooler config) and
/// <see cref="DbPasswordEncrypted"/> of the AIDE-generated password; neither is ever returned or logged. Concurrently
/// edited (provision poll, Owner actions), so <see cref="RowVersion"/> maps <c>xmin</c>.
/// </summary>
public sealed class ManagedDatabase
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public DeploymentEnvironment Environment { get; set; } = DeploymentEnvironment.Production;

    public ManagedDatabaseProvider Provider { get; set; } = ManagedDatabaseProvider.External;

    /// <summary>The Owner's platform connection the database was provisioned or linked with; cleared when it is deleted.</summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>The platform resource (Supabase project ref, Render Postgres id, Railway service id); <c>null</c> for External.</summary>
    public string? ResourceId { get; set; }

    public string? Region { get; set; }

    public string? Plan { get; set; }

    public ManagedDatabaseStatus Status { get; set; } = ManagedDatabaseStatus.Provisioning;

    /// <summary>Ciphertext of the connection JSON (URIs, keys, pooler config); never returned or logged.</summary>
    public string ConnectionEncrypted { get; set; } = string.Empty;

    /// <summary>Ciphertext of the database password AIDE generated or reset; <c>null</c> when AIDE doesn't hold one.</summary>
    public string? DbPasswordEncrypted { get; set; }

    /// <summary>When the database expires (Render free Postgres, 30 days); <c>null</c> when it doesn't.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint RowVersion { get; set; }
}

/// <summary>Where a managed database lives (§5). Wire values: <c>supabase</c>, <c>render</c>, <c>railway</c>, <c>external</c>, <c>fake</c>.</summary>
public enum ManagedDatabaseProvider
{
    Supabase = 0,
    Render = 1,
    Railway = 2,
    External = 3,
    Fake = 4,
}

/// <summary>
/// §4.21 database states: <c>provisioning → available | error</c>, plus <c>paused</c> (Supabase <c>INACTIVE</c>),
/// <c>expiring</c> (Render free, within 7 days of <c>expiresAt</c>) and <c>removed</c>.
/// </summary>
public enum ManagedDatabaseStatus
{
    Provisioning = 0,
    Available = 1,
    Error = 2,
    Paused = 3,
    Expiring = 4,
    Removed = 5,
}

/// <summary>
/// §4.21, §5: one run of AIDE's Supabase BaaS migration runner (table <c>database_migration_runs</c>) against a managed
/// database for a commit: the migrations it applied (<see cref="Applied"/>, jsonb <c>[{version, name, ms}]</c>), and the
/// error when it failed.
/// </summary>
public sealed class DatabaseMigrationRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public Guid ManagedDatabaseId { get; set; }

    /// <summary>The release whose production rollout ran it; <c>null</c> for a preview rollout.</summary>
    public Guid? ReleaseId { get; set; }

    public Guid ReleaseVersionId { get; set; }

    public string GitSha { get; set; } = string.Empty;

    public DeploymentEnvironment Environment { get; set; } = DeploymentEnvironment.Preview;

    public MigrationRunStatus Status { get; set; } = MigrationRunStatus.Running;

    public IReadOnlyList<AppliedMigration> Applied { get; set; } = [];

    /// <summary>FR-153: the Supabase security advisor findings read after the run (jsonb, <c>[]</c> when none or not read).</summary>
    public IReadOnlyList<Databases.AdvisorFinding> AdvisorFindings { get; set; } = [];

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>One migration file a run applied: its version, name and how long it took (milliseconds).</summary>
public sealed record AppliedMigration(string Version, string Name, long Ms);

/// <summary>A migration run's state (§5). Wire values: <c>running</c>, <c>succeeded</c>, <c>failed</c>.</summary>
public enum MigrationRunStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
}
