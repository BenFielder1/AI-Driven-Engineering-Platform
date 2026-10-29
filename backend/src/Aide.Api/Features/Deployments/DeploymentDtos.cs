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
/// A failed deployment carries <c>errorCode</c> (<c>VERCEL_ERROR</c>, <c>DEPLOYMENT_TIMEOUT</c>), <c>errorMessage</c> and
/// <c>buildLogTail</c> (the end of the build log). <c>trigger</c> is <c>auto</c> (AIDE, after execution or the release
/// merge), <c>manual</c>, <c>promote</c> or <c>redeploy</c>; <c>triggeredBy</c> is <c>null</c> for <c>auto</c> (or a
/// deleted user). <c>promotedFromDeploymentId</c> is the earlier production deployment a <c>promote</c> row promoted.
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
    DateTimeOffset? ReadyAt);

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

/// <summary>The project's Vercel binding (FR-88): which connection, "connected by &lt;name&gt;", and the connection's status.
/// Never the token.</summary>
public sealed record DeploymentBindingDto(
    Guid ConnectionId,
    string Label,
    string Last4,
    string? TeamSlug,
    DeploymentUserDto BoundBy,
    PlatformConnectionStatus Status);

/// <summary>The linked Vercel project (FR-88): id (<c>prj_…</c>), name, its Vercel URL and the production URL once known.</summary>
public sealed record VercelProjectDto(string Id, string Name, string? Url, string? ProductionUrl);

/// <summary>
/// An environment variable the project's stack requires (FR-88, §4.22: the resolved composition's env): its key, targets, whether it is
/// sensitive, what it is for, and whether the project has it (<c>present</c>; missing ones are flagged).
/// </summary>
public sealed record RequiredEnvVarDto(
    string Key,
    IReadOnlyList<DeploymentEnvironment> Targets,
    bool Sensitive,
    string Description,
    bool Present);

/// <summary>
/// Settings → Deployment (FR-88), for any member. <c>provider</c> is <c>vercel</c> (or <c>fake</c> in test environments)
/// once deployment is set, else <c>null</c>. <c>supported</c>: the stack deploys to Vercel, or an Owner opted in by setting
/// a provider. <c>binding</c> is the bound connection ("connected by &lt;name&gt;"; <c>null</c>: none) and <c>canBind</c>
/// whether the caller may bind one of their own (Owners). <c>vercelProject</c> is the linked Vercel project (<c>null</c>:
/// none yet). <c>rootDirectory</c> and <c>framework</c> are what the Vercel project builds. <c>requiredEnv</c> lists the
/// stack's required env vars with <c>present</c>. <c>gitConnected</c> is whether Vercel's GitHub App can access the repo
/// (<c>null</c>: not checked), with Vercel's <c>installUrl</c> when it can't.
/// </summary>
public sealed record DeploymentSettingsDto(
    DeploymentProvider? Provider,
    bool Supported,
    DeploymentBindingDto? Binding,
    bool CanBind,
    VercelProjectDto? VercelProject,
    string RootDirectory,
    string Framework,
    IReadOnlyList<RequiredEnvVarDto> RequiredEnv,
    bool? GitConnected,
    string? InstallUrl);

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
/// Settings → Deployment (FR-88, Owner). <c>provider</c> (required) is <c>vercel</c>, or <c>null</c> to turn deployment
/// off (other fields not allowed then; history is kept). <c>connectionId</c> binds one of the caller's own Vercel
/// connections (omitted: keep the bound one). Send <c>vercelProjectId</c> to link an existing Vercel project or
/// <c>create</c> to create one, not both (omitted: keep the linked one).
/// </summary>
public sealed record UpdateDeploymentSettingsRequest
{
    public required DeploymentProvider? Provider { get; init; }

    public Guid? ConnectionId { get; init; }

    [MaxLength(DeploymentSettingsRules.VercelProjectIdMaxLength)]
    public string? VercelProjectId { get; init; }

    public CreateVercelProjectRequest? Create { get; init; }
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

/// <summary>
/// A project environment variable (FR-88, Owner only). The value is never returned: <c>hasValue</c> is always <c>true</c>.
/// <c>syncedAt</c> is when the current value was pushed to Vercel (<c>null</c>: not yet); <c>syncError</c> is the last push
/// failure (<c>null</c> after a successful push).
/// </summary>
public sealed record DeploymentEnvVarDto(
    string Key,
    IReadOnlyList<DeploymentEnvironment> Targets,
    bool Sensitive,
    bool HasValue,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SyncedAt,
    string? SyncError);

/// <summary>
/// Create or replace an environment variable (FR-88): the value (up to 64 KB; encrypted in AIDE and never returned), its
/// targets (<c>preview</c>, <c>production</c>, at least one) and whether Vercel stores it as sensitive.
/// </summary>
public sealed record PutDeploymentEnvRequest
{
    [Required]
    public required string Value { get; init; }

    [Required]
    public required IReadOnlyList<DeploymentEnvironment> Targets { get; init; }

    public bool Sensitive { get; init; }
}
