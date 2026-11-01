using System.ComponentModel.DataAnnotations;
using Aide.Api.Features.Deployments;
using Aide.Domain.Databases;
using Aide.Domain.Entities;

namespace Aide.Api.Features.Databases;

/// <summary>
/// FR-152: the <c>database_provisioning</c> job of a database card: <c>state</c> (<c>queued</c>, <c>running</c>,
/// <c>succeeded</c>, <c>failed</c>), the current <c>step</c> (<c>queued</c>, <c>creating</c>, <c>linking</c>,
/// <c>waiting</c>, <c>syncing_env</c>), and on failure the error code (<c>DATABASE_PROVISIONING_FAILED</c>,
/// <c>DATABASE_PROVISIONING_TIMEOUT</c>, <c>PLATFORM_PLAN_LIMIT</c>, <c>CREDENTIAL_INVALID</c>,
/// <c>CONNECTION_SCOPE_INSUFFICIENT</c>, <c>UPSTREAM_ERROR</c>, <c>VALIDATION</c>) and message.
/// </summary>
public sealed record DatabaseProvisioningDto(
    Guid? JobId,
    string State,
    string? Step,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// FR-152 database card (Settings → Deployment <c>databases[]</c>, one per environment when the composition has a
/// database host or a database is set): <c>provider</c> (<c>supabase</c>, <c>render</c>, <c>railway</c>, <c>external</c>,
/// <c>fake</c>), <c>status</c> (<c>not_configured</c>, <c>provisioning</c>, <c>available</c>, <c>error</c>, <c>paused</c>,
/// <c>expiring</c>, <c>removed</c>), the composition's <c>hostOption</c>, the binding ("connected by"), the resource, region,
/// plan, expiry, the dashboard link (<c>resourceUrl</c>), a connection summary without secrets (host, port, database), the
/// Supabase permissions the connection lacks (e.g. "API Key Secrets: Read"), the provisioning job, and <c>warnings</c>
/// (<c>render_free_expiry</c>, <c>supabase_free_pausing</c>, <c>supabase_free_project_limit</c>,
/// <c>preview_uses_production_database</c>). Secrets are never returned. FR-148: on the preview card,
/// <c>resetRecommended</c> is true when the preview database has migrations the version branch no longer has (e.g. after a
/// rollback that removed migrations): offer "Reset preview database" (<c>POST …/databases/preview/reset</c>).
/// </summary>
public sealed record DeploymentDatabaseDto(
    DeploymentEnvironment Environment,
    string Provider,
    string Status,
    string? HostOption,
    bool InComposition,
    bool NeedsConnection,
    DeploymentTargetBindingDto? Binding,
    bool CanBind,
    string? ResourceId,
    string? Name,
    string? Region,
    string? Plan,
    DateTimeOffset? ExpiresAt,
    bool Paused,
    bool CanRestore,
    string? ResourceUrl,
    string? ConnectionSummary,
    IReadOnlyList<string> MissingPermissions,
    DatabaseProvisioningDto? Provisioning,
    IReadOnlyList<string> Warnings,
    DateTimeOffset? UpdatedAt,
    bool ResetRecommended = false);

/// <summary>FR-152: provision a database: region (Supabase: a region code or americas/emea/apac; Render: defaults to the backend's), plan, and an optional name.</summary>
public sealed record CreateManagedDatabaseRequest
{
    [MaxLength(64)]
    public string? Region { get; init; }

    [MaxLength(64)]
    public string? Plan { get; init; }

    /// <summary>The resource name; defaults to the project's name (Render preview: <c>&lt;name&gt;-preview</c>).</summary>
    [MaxLength(60)]
    public string? Name { get; init; }
}

/// <summary>FR-152: an External database's connection string (<c>postgres://</c>, <c>postgresql://</c> or ADO.NET), stored encrypted and never returned.</summary>
public sealed record ExternalDatabaseRequest
{
    [Required]
    [MaxLength(ManagedDatabaseRules.ConnectionStringMaxLength)]
    public required string ConnectionString { get; init; }

    public override string ToString() => "ExternalDatabaseRequest { ConnectionString = *** }";
}

/// <summary>
/// FR-152 (Owner): set the environment's database. <c>provider</c> must be the composition's database host (<c>fake</c>
/// also in Fake mode); exactly one of <c>create</c>, <c>link</c> (a resource id from the connection's picker) or
/// <c>external</c> (External only). <c>connectionId</c> is one of the caller's own platform connections (not for External).
/// </summary>
public sealed record PutManagedDatabaseRequest
{
    public required ManagedDatabaseProvider Provider { get; init; }

    public Guid? ConnectionId { get; init; }

    public CreateManagedDatabaseRequest? Create { get; init; }

    public LinkDeploymentResourceRequest? Link { get; init; }

    public ExternalDatabaseRequest? External { get; init; }
}

/// <summary>One file a migration run applied: version, name and duration (ms).</summary>
public sealed record MigrationFileDto(string Version, string Name, long Ms);

/// <summary>A Supabase security advisor finding recorded on a run (FR-153).</summary>
public sealed record AdvisorFindingDto(string Name, string Level, string Title, string? Detail, string? Remediation);

/// <summary>
/// FR-153, §7 Migrations page: one Supabase BaaS migration run: environment, database, release and version, commit, status
/// (<c>running</c>, <c>succeeded</c>, <c>failed</c>), the files applied with durations, the total duration, the error
/// (<c>MIGRATION_FAILED</c>, <c>MIGRATION_HISTORY_DIVERGED</c>) and the advisor findings.
/// </summary>
public sealed record MigrationRunDto(
    Guid Id,
    DeploymentEnvironment Environment,
    Guid ManagedDatabaseId,
    ManagedDatabaseProvider? Provider,
    Guid? ReleaseId,
    Guid ReleaseVersionId,
    string GitSha,
    MigrationRunStatus Status,
    IReadOnlyList<MigrationFileDto> Files,
    long TotalMs,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<AdvisorFindingDto> AdvisorFindings,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);
