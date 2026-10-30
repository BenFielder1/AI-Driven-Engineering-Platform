namespace Aide.Domain.Entities;

/// <summary>
/// A project's deployment target for one component (FR-144, §5); table <c>deployment_targets</c>, unique per
/// <c>(project_id, component)</c>. Replaces v2's <c>project_deployment_settings</c>: v2 rows became <c>frontend</c> targets
/// with provider <c>vercel</c>. Owners can edit them concurrently, so the row carries an <c>xmin</c> concurrency token.
/// </summary>
public sealed class DeploymentTarget
{
    public const string DefaultRootDirectory = ".";

    public const string DefaultFramework = "nextjs";

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public DeploymentComponent Component { get; set; } = DeploymentComponent.Frontend;

    public DeploymentProvider Provider { get; set; } = DeploymentProvider.Vercel;

    /// <summary>The bound Owner's <see cref="PlatformConnection"/>; cleared (ON DELETE SET NULL) when it is deleted.</summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>The Owner who bound <see cref="ConnectionId"/> (the connection's owner).</summary>
    public Guid? BoundByUserId { get; set; }

    /// <summary>
    /// The platform resource (Vercel project id <c>prj_…</c>, Netlify site, Worker, Pages project, Render service, Railway
    /// project and service); <c>null</c> until it is created or linked.
    /// </summary>
    public string? ResourceId { get; set; }

    /// <summary>The preview resource (Render preview service, Railway environment, Workers preview trigger); <c>null</c> when none.</summary>
    public string? PreviewResourceId { get; set; }

    /// <summary>The resource's name (e.g. the Vercel project name); <c>null</c> until it is created or linked.</summary>
    public string? Name { get; set; }

    public string RootDirectory { get; set; } = DefaultRootDirectory;

    /// <summary>How the platform builds the component (§4.20); provider-specific settings such as Vercel's framework.</summary>
    public DeploymentBuildConfig BuildConfig { get; set; } = new();

    public string? ProductionUrl { get; set; }

    public string? PreviewUrl { get; set; }

    public DeploymentTargetStatus Status { get; set; } = DeploymentTargetStatus.NotConfigured;

    /// <summary>Why the target needs attention or failed (a short machine reason such as <c>connection_missing</c>).</summary>
    public string? StatusReason { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint RowVersion { get; set; }
}

/// <summary>
/// The build configuration of a deployment target (§4.20, <c>deployment_targets.build_config</c>): what the platform runs.
/// Every field is optional; providers use the ones they support (Vercel: <see cref="Framework"/>).
/// </summary>
public sealed record DeploymentBuildConfig
{
    public string? Framework { get; init; }

    public string? BuildCommand { get; init; }

    public string? OutputDirectory { get; init; }

    public string? StartCommand { get; init; }

    public string? DockerfilePath { get; init; }

    public string? HealthCheckPath { get; init; }

    public string? PreDeployCommand { get; init; }
}

/// <summary>A deployable component of a project (FR-144). Wire values: <c>frontend</c>, <c>backend</c>.</summary>
public enum DeploymentComponent
{
    Frontend = 0,
    Backend = 1,
}

/// <summary>State of a deployment target (FR-144). Wire values: <c>not_configured</c>, <c>configuring</c>, <c>ready</c>,
/// <c>attention</c>, <c>error</c>.</summary>
public enum DeploymentTargetStatus
{
    NotConfigured = 0,
    Configuring = 1,
    Ready = 2,
    Attention = 3,
    Error = 4,
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

    /// <summary>Wire values of <see cref="DeploymentEnvironment"/> (<c>preview</c>, <c>production</c>); at least one (a check
    /// constraint enforces both).</summary>
    public IReadOnlyList<string> Targets { get; set; } = [];

    public string ValueEncrypted { get; set; } = string.Empty;

    public bool Sensitive { get; set; }

    /// <summary>Wire values of <see cref="DeploymentComponent"/> the variable applies to (FR-145); at least one (a check
    /// constraint enforces both). v2 variables apply to <c>frontend</c>.</summary>
    public IReadOnlyList<string> AppliesTo { get; set; } = ["frontend"];

    /// <summary>Whether the variable is exposed to the browser (FR-145: a public prefix such as <c>NEXT_PUBLIC_</c>).</summary>
    public bool Public { get; set; }

    /// <summary>Who wrote it (FR-145): an Owner (<c>user</c>) or AIDE (<c>derived</c>, read-only).</summary>
    public DeploymentEnvSource Source { get; set; } = DeploymentEnvSource.User;

    /// <summary>For a derived variable: what it is derived from (e.g. "Render Postgres (production)"); <c>null</c> for user ones.</summary>
    public string? DerivedFrom { get; set; }

    /// <summary>Vercel's env var id per target wire value, for upsert/delete by <c>(key, target)</c>; null until synced.</summary>
    public IReadOnlyDictionary<string, string>? VercelEnvIds { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the current value was last pushed to Vercel; null if never or out of date.</summary>
    public DateTimeOffset? SyncedAt { get; set; }

    /// <summary>The last sync failure (never the value); null after a successful sync.</summary>
    public string? SyncError { get; set; }
}

/// <summary>A preview or production deployment (FR-90, FR-91, §4.15); table <c>deployments</c>. The automatic production
/// deployment of a release is unique per <c>(release_id, component)</c>; redeploy and promote rows are additional rows.
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

    /// <summary>The deployment target it deployed (FR-151); <c>null</c> when the target was removed (ON DELETE SET NULL) or
    /// for v2 rows of a project without settings.</summary>
    public Guid? TargetId { get; set; }

    public DeploymentComponent Component { get; set; } = DeploymentComponent.Frontend;

    /// <summary>Preview or production (column <c>environment</c>; v2 called it the target).</summary>
    public DeploymentEnvironment Environment { get; set; }

    public DeploymentKind Kind { get; set; } = DeploymentKind.Deploy;

    /// <summary>The preview or production rollout it belongs to (FR-147, FR-149); <c>null</c> for single deployments.</summary>
    public Guid? RolloutId { get; set; }

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

/// <summary>
/// Deployment provider (§4.15, §4.20): a target's platform, from the composition's host (<c>deploy-vercel</c> → <c>vercel</c>).
/// Wire values: <c>vercel</c>, <c>fake</c> (v2's explicit Fake provider; deployments made under
/// <c>Deployments:Provider=Fake</c> are recorded as <c>fake</c>), <c>netlify</c>, <c>cloudflare</c>, <c>render</c>,
/// <c>railway</c>, <c>github_pages</c>.
/// </summary>
public enum DeploymentProvider
{
    Vercel = 0,
    Fake = 1,
    Netlify = 2,
    Cloudflare = 3,
    Render = 4,
    Railway = 5,
    GithubPages = 6,
}

/// <summary>Deployment environment (FR-147, FR-149), also a Vercel env target (FR-88). Wire values: <c>preview</c>,
/// <c>production</c>.</summary>
public enum DeploymentEnvironment
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

    /// <summary>FR-150: an Owner rolled production back to an earlier deployment (Netlify restore, Cloudflare version, Pages rollback).</summary>
    Rollback = 4,
}

/// <summary>What a deployment row does (FR-150, §5). Wire values: <c>deploy</c>, <c>rollback</c>, <c>promote</c>.</summary>
public enum DeploymentKind
{
    Deploy = 0,
    Rollback = 1,
    Promote = 2,
}

/// <summary>Who wrote an environment variable (FR-145). Wire values: <c>user</c>, <c>derived</c>; <c>required</c> is an
/// inventory row only (a pack's required variable without a value), never stored.</summary>
public enum DeploymentEnvSource
{
    User = 0,
    Derived = 1,
    Required = 2,
}
