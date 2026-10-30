using System.ComponentModel.DataAnnotations;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.Api.Features.Deployments;

/// <summary>A user named on a deployment or binding: "connected by &lt;name&gt;", "triggered by &lt;name&gt;".</summary>
public sealed record DeploymentUserDto(Guid Id, string Name);

/// <summary>
/// A preview or production deployment (FR-90, FR-91, §4.15). <c>status</c> follows <c>queued → building → ready | error |
/// canceled</c>. <c>url</c> is the deployment URL once known, <c>inspectorUrl</c> Vercel's inspector page. <c>gitRef</c>
/// and <c>gitSha</c> are what was deployed; <c>releaseVersion</c> is the version (e.g. <c>1.2.0</c>) of
/// <c>releaseVersionId</c>; <c>releaseId</c> is the release attempt of a production deployment (<c>null</c> for previews).
/// A failed deployment carries <c>errorCode</c> (<c>PLATFORM_ERROR</c>, formerly <c>VERCEL_ERROR</c>; <c>DEPLOYMENT_TIMEOUT</c>), <c>errorMessage</c> and
/// <c>buildLogTail</c> (the end of the build log). <c>trigger</c> is <c>auto</c> (AIDE, after execution or the release
/// merge), <c>manual</c>, <c>promote</c> or <c>redeploy</c>; <c>triggeredBy</c> is <c>null</c> for <c>auto</c> (or a
/// deleted user). <c>promotedFromDeploymentId</c> is the earlier production deployment a <c>promote</c> (or
/// <c>rollback</c>) row made current again.
/// FR-150, FR-151: <c>component</c> (<c>frontend</c>, <c>backend</c>), <c>kind</c> (<c>deploy</c>, <c>rollback</c>,
/// <c>promote</c>), <c>platform</c> (the target's host; <c>provider</c> is <c>fake</c> in Fake mode), <c>current</c> (the
/// deployment production serves now, per component), <c>canRollback</c> with <c>rollbackBlockedReason</c> when false
/// (<c>unsupported</c>, <c>not_production</c>, <c>not_ready</c>, <c>unknown_at_platform</c>, <c>current</c>), and
/// <c>olderThanLatestMigration</c> (the rollback dialog's database warning; <c>null</c> = unknown, until M30 databases).
/// </summary>
public sealed record DeploymentDto(
    Guid Id,
    DeploymentEnvironment Target,
    DeploymentProvider Provider,
    DeploymentStatus Status,
    string? Url,
    string? InspectorUrl,
    string GitRef,
    string GitSha,
    Guid ReleaseVersionId,
    string ReleaseVersion,
    Guid? ReleaseId,
    string? ErrorCode,
    string? ErrorMessage,
    string? BuildLogTail,
    DeploymentTrigger Trigger,
    DeploymentUserDto? TriggeredBy,
    Guid? PromotedFromDeploymentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadyAt,
    DeploymentComponent Component,
    DeploymentKind Kind,
    DeploymentProvider Platform,
    bool Current,
    bool CanRollback,
    string? RollbackBlockedReason,
    bool? OlderThanLatestMigration);

/// <summary>
/// FR-151: the production URL of one component on the project overview: its platform, the URL (<c>null</c> until known)
/// and the deployment production serves now (<c>null</c> before the first ready production deployment).
/// </summary>
public sealed record ComponentProductionUrlDto(DeploymentComponent Component, DeploymentProvider Provider, string? Url, Guid? DeploymentId);

/// <summary>
/// FR-90: the preview deployment a review cycle waited for (<c>compliance_report.external_checks.preview</c>): its id, final
/// (or last seen) <c>status</c>, <c>url</c> and <c>buildLogTail</c>, how long review waited (<c>waitedSeconds</c>, up to 20
/// minutes; <c>null</c> when it didn't wait) and whether it gave up waiting (<c>timedOut</c>).
/// </summary>
public sealed record PreviewCheckDto(
    Guid DeploymentId,
    DeploymentStatus Status,
    string? Url,
    string? BuildLogTail,
    int? WaitedSeconds,
    bool TimedOut);

/// <summary>FR-88: create a new Vercel project linked to the GitHub repo (framework <c>nextjs</c>).</summary>
public sealed record CreateVercelProjectRequest
{
    /// <summary>Lowercase letters, digits, <c>.</c>, <c>_</c>, <c>-</c>, up to 100, without <c>---</c>.</summary>
    [Required]
    [MaxLength(DeploymentSettingsRules.VercelProjectNameMaxLength)]
    public required string Name { get; init; }

    /// <summary>The app's directory in the repository (default <c>.</c>).</summary>
    [MaxLength(DeploymentSettingsRules.RootDirectoryMaxLength)]
    public string? RootDirectory { get; init; }
}

/// <summary>
/// Deployment step of the new-project wizard (FR-88): <c>provider</c> <c>vercel</c>, one of the caller's own Vercel
/// connections, and an existing Vercel project (<c>vercelProjectId</c>) or a new one (<c>create</c>), not both. Needs a
/// GitHub-backed project (<c>git.mode</c> <c>local</c>: 400 <c>VALIDATION</c>).
/// </summary>
public sealed record CreateProjectDeploymentRequest
{
    public required DeploymentProvider Provider { get; init; }

    public required Guid ConnectionId { get; init; }

    [MaxLength(DeploymentSettingsRules.VercelProjectIdMaxLength)]
    public string? VercelProjectId { get; init; }

    public CreateVercelProjectRequest? Create { get; init; }
}

