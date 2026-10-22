namespace Aide.Domain.Entities;

/// <summary>Deployment settings of a project (FR-88); table <c>project_deployment_settings</c>, at most one per project.
/// Owners can edit them concurrently, so the row carries an <c>xmin</c> concurrency token.</summary>
public sealed class ProjectDeploymentSettings
{
    public const string DefaultRootDirectory = ".";

    public const string DefaultFramework = "nextjs";

    public Guid ProjectId { get; set; }

    public DeploymentProvider Provider { get; set; } = DeploymentProvider.Vercel;

    /// <summary>The bound Owner's <see cref="VercelConnection"/>; cleared (ON DELETE SET NULL) when it is deleted.</summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>The Owner who bound <see cref="ConnectionId"/> (the connection's owner).</summary>
    public Guid? BoundByUserId { get; set; }

    /// <summary>Vercel's project id (<c>prj_…</c>), set once the Vercel project is created or linked.</summary>
    public string? VercelProjectId { get; set; }

    public string? VercelProjectName { get; set; }

    public string RootDirectory { get; set; } = DefaultRootDirectory;

    public string Framework { get; set; } = DefaultFramework;

    public string? ProductionUrl { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint RowVersion { get; set; }
}

/// <summary>A project environment variable pushed to Vercel (FR-88, §4.15 env sync); table <c>deployment_env_vars</c>,
/// unique per (project, key). The value is Data Protection ciphertext (<c>SecretPurposes.DeploymentEnvValue</c>) and is
/// never returned or logged.</summary>
public sealed class DeploymentEnvVar
{
    /// <summary>The longest key accepted.</summary>
    public const int KeyMaxLength = 256;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>Wire values of <see cref="DeploymentTarget"/> (<c>preview</c>, <c>production</c>); at least one (a check
    /// constraint enforces both).</summary>
    public IReadOnlyList<string> Targets { get; set; } = [];

    public string ValueEncrypted { get; set; } = string.Empty;

    public bool Sensitive { get; set; }

    /// <summary>Vercel's env var id per target wire value, for upsert/delete by <c>(key, target)</c>; null until synced.</summary>
    public IReadOnlyDictionary<string, string>? VercelEnvIds { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the current value was last pushed to Vercel; null if never or out of date.</summary>
    public DateTimeOffset? SyncedAt { get; set; }

    /// <summary>The last sync failure (never the value); null after a successful sync.</summary>
    public string? SyncError { get; set; }
}

/// <summary>A preview or production deployment (FR-90, FR-91, §4.15); table <c>deployments</c>. The automatic production
/// deployment of a release is unique per <c>(release_id, target)</c>; redeploy and promote rows are additional rows.
/// Polls, cancellation and redeploys can race, so the row carries an <c>xmin</c> concurrency token.</summary>
public sealed class Deployment
{
    /// <summary>The longest stored build-log tail (characters); <see cref="TailOf"/> keeps the end of a longer log.</summary>
    public const int BuildLogTailMaxLength = 16_000;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public Guid ReleaseVersionId { get; set; }

    /// <summary>The release attempt a production deployment belongs to; null for previews (and if the release row goes).</summary>
    public Guid? ReleaseId { get; set; }

    public DeploymentProvider Provider { get; set; }

    public DeploymentTarget Target { get; set; }

    /// <summary>The provider's deployment id (<c>dpl_…</c>). Not unique: a promote row can name an existing deployment.</summary>
    public string ProviderDeploymentId { get; set; } = string.Empty;

    public string? Url { get; set; }

    public string? InspectorUrl { get; set; }

    public string GitRef { get; set; } = string.Empty;

    public string GitSha { get; set; } = string.Empty;

    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;

    /// <summary>§7 error code, e.g. <c>DEPLOYMENT_TIMEOUT</c>; null unless <see cref="DeploymentStatus.Error"/>.</summary>
    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>The end of the build log (at most <see cref="BuildLogTailMaxLength"/> characters).</summary>
    public string? BuildLogTail { get; set; }

    /// <summary>The user who triggered it; null when AIDE triggered it (auto) or the user was deleted.</summary>
    public Guid? TriggeredBy { get; set; }

    public DeploymentTrigger Trigger { get; set; }

    /// <summary>For <see cref="DeploymentTrigger.Promote"/>: the earlier production deployment that was promoted.</summary>
    public Guid? PromotedFromDeploymentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReadyAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint RowVersion { get; set; }

    /// <summary>The last <see cref="BuildLogTailMaxLength"/> characters of <paramref name="log"/>.</summary>
    public static string TailOf(string log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return log.Length <= BuildLogTailMaxLength ? log : log[^BuildLogTailMaxLength..];
    }
}

/// <summary>Deployment provider (§4.15). Wire values: <c>vercel</c>, <c>fake</c> (deterministic, tests and E2E).</summary>
public enum DeploymentProvider
{
    Vercel = 0,
    Fake = 1,
}

/// <summary>Deployment target, also a Vercel env target (FR-88). Wire values: <c>preview</c>, <c>production</c>.</summary>
public enum DeploymentTarget
{
    Preview = 0,
    Production = 1,
}

/// <summary>§4.15 deployment states: <c>queued → building → ready | error | canceled</c>.</summary>
public enum DeploymentStatus
{
    Queued = 0,
    Building = 1,
    Ready = 2,
    Error = 3,
    Canceled = 4,
}

/// <summary>Why a deployment row exists (FR-90, FR-91). Wire values: <c>auto</c> (AIDE after execution or release),
/// <c>manual</c>, <c>promote</c>, <c>redeploy</c>.</summary>
public enum DeploymentTrigger
{
    Auto = 0,
    Manual = 1,
    Promote = 2,
    Redeploy = 3,
}
