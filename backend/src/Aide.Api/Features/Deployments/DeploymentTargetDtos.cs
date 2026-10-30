using System.ComponentModel.DataAnnotations;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// A target's bound connection (FR-144): which of the binder's platform connections, "connected by &lt;name&gt;", the
/// account it reaches and its status. Never the token.
/// </summary>
public sealed record DeploymentTargetBindingDto(
    Guid ConnectionId,
    string Label,
    string Last4,
    string? AccountName,
    DeploymentUserDto BoundBy,
    PlatformConnectionStatus Status);

/// <summary>The build configuration AIDE derived from the composition (§4.20); each field is <c>null</c> when it doesn't apply.</summary>
public sealed record DeploymentBuildConfigDto(
    string? Framework,
    string? BuildCommand,
    string? OutputDirectory,
    string? StartCommand,
    string? DockerfilePath,
    string? HealthCheckPath,
    string? PreDeployCommand);

/// <summary>§4.20 capabilities of the target's provider.</summary>
public sealed record DeploymentCapabilitiesDto(bool ShaPinned, bool Previews, bool Rollback, bool PerEnvironmentVariables, bool RuntimeSecrets);

/// <summary>
/// The <c>deployment_setup</c> job of a target (FR-144): <c>state</c> <c>queued</c>, <c>running</c>, <c>succeeded</c> or
/// <c>failed</c>; <c>step</c> while it runs (<c>queued</c>, <c>creating</c>, <c>linking</c>, <c>syncing_env</c>); when it
/// failed, <c>errorCode</c> (<c>PLATFORM_GIT_NOT_CONNECTED</c>, <c>PLATFORM_PLAN_LIMIT</c>, <c>PLATFORM_PERMISSION_MISSING</c>,
/// <c>CREDENTIAL_INVALID</c>, <c>NOT_FOUND</c>, <c>VALIDATION</c>, <c>PLATFORM_ERROR</c>, <c>UPSTREAM_ERROR</c>), the
/// <c>message</c> and a <c>fixUrl</c> (the platform's install or connect link, or the GitHub App settings).
/// </summary>
public sealed record DeploymentSetupDto(
    Guid? JobId,
    string State,
    string? Step,
    string? ErrorCode,
    string? Message,
    string? FixUrl,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>One readiness checklist item (FR-144): a stable <c>id</c>, <c>state</c> (<c>ok</c>, <c>warning</c>, <c>error</c>, <c>na</c>), a message and an optional fix link.</summary>
public sealed record ReadinessItemDto(string Id, ReadinessState State, string Message, string? FixUrl);

/// <summary>
/// One target card of Settings → Deployment (FR-144): one per component whose host the composition names (plus a target
/// set up before the composition dropped its host, <c>inComposition: false</c>). <c>provider</c> is the composition's host
/// (<c>hostOption</c> is its catalog id). <c>binding</c> is the bound connection (<c>null</c> for GitHub Pages, which uses
/// the project's GitHub App or PAT) and <c>needsConnection</c> whether one is needed; <c>canBind</c> whether the caller may
/// bind one of their own. <c>resourceId</c>/<c>name</c> the platform resource (<c>null</c> until set up),
/// <c>rootDirectory</c> and <c>buildConfig</c> what it builds, the production and preview URLs once known, <c>status</c>
/// (<c>not_configured</c>, <c>configuring</c>, <c>ready</c>, <c>attention</c>, <c>error</c>) with <c>statusReason</c>, the
/// setup job (<c>setup</c>, <c>null</c> before the first), the provider's capabilities and the readiness checklist
/// (platform items say "not checked yet" until <c>verify</c>). <c>fakeMode</c>: deployments run on the Fake provider.
/// </summary>
public sealed record DeploymentTargetDto(
    DeploymentComponent Component,
    DeploymentProvider Provider,
    string? HostOption,
    bool InComposition,
    bool NeedsConnection,
    DeploymentTargetBindingDto? Binding,
    bool CanBind,
    string? ResourceId,
    string? Name,
    string RootDirectory,
    DeploymentBuildConfigDto BuildConfig,
    string? ProductionUrl,
    string? PreviewUrl,
    DeploymentTargetStatus Status,
    string? StatusReason,
    DeploymentSetupDto? Setup,
    DeploymentCapabilitiesDto Capabilities,
    IReadOnlyList<ReadinessItemDto> Readiness,
    bool ImplementedForReal,
    bool FakeMode);

/// <summary>
/// A database card (FR-144, FR-152): per environment when the composition has a database host. Until M30 every card is
/// <c>status: not_configured</c> with the composition's <c>provider</c> (<c>supabase</c>, <c>render-postgres</c>,
/// <c>railway-postgres</c>, <c>external</c>).
/// </summary>
public sealed record DeploymentDatabaseDto(DeploymentEnvironment Environment, string Provider, string Status);

/// <summary>
/// One row of the environment inventory (FR-145): <c>key</c>, the components it applies to, its environments, whether it is
/// sensitive or public, its <c>source</c> (<c>user</c>, <c>derived</c>, <c>required</c>), what a derived one comes from
/// (<c>derivedFrom</c>), a required one's <c>description</c>, <c>hasValue</c> (required rows and derived rows without a
/// source URL yet: <c>false</c>; values are never returned), and the sync state (<c>syncedAt</c>, <c>syncError</c>).
/// </summary>
public sealed record DeploymentEnvVarDto(
    string Key,
    IReadOnlyList<DeploymentComponent> AppliesTo,
    IReadOnlyList<DeploymentEnvironment> Environments,
    bool Sensitive,
    bool Public,
    DeploymentEnvSource Source,
    string? DerivedFrom,
    string? Description,
    bool HasValue,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? SyncedAt,
    string? SyncError);

/// <summary>The environment inventory (FR-145): the frontend pack's public prefix (<c>NEXT_PUBLIC_</c>, <c>VITE_</c>, or <c>null</c>) and the rows.</summary>
public sealed record DeploymentEnvInventoryDto(string? PublicPrefix, IReadOnlyList<DeploymentEnvVarDto> Variables);

/// <summary>
/// FR-146: the composition's origin model (<c>same-origin</c>, <c>proxy</c>, <c>cross-origin</c>; <c>null</c> API only), the
/// stack-packs vocabulary's (<c>packModel</c>: <c>same-origin</c> or <c>cross-origin</c>), the models the Owner may switch
/// between (Settings → Stack) and the one-line explanation.
/// </summary>
public sealed record DeploymentOriginModelDto(string? Model, string? PackModel, IReadOnlyList<string> Options, string? Explanation);

/// <summary>
/// Settings → Deployment (FR-144, any member; D-V4-4 replaced v2's single Vercel shape): <c>targets</c>, <c>databases</c>,
/// the environment inventory (<c>env</c>; keys only) and the <c>originModel</c>. <c>canManage</c>: the caller is an Owner.
/// <c>fakeMode</c>: <c>Deployments:Provider=Fake</c>.
/// </summary>
public sealed record DeploymentSettingsDto(
    IReadOnlyList<DeploymentTargetDto> Targets,
    IReadOnlyList<DeploymentDatabaseDto> Databases,
    DeploymentEnvInventoryDto Env,
    DeploymentOriginModelDto OriginModel,
    bool CanManage,
    bool FakeMode);

/// <summary>FR-144: create a platform resource: its name (letters, digits, <c>.</c>, <c>_</c>, <c>-</c>), and region and plan where the platform has them.</summary>
public sealed record CreateDeploymentResourceRequest
{
    [Required]
    [MaxLength(DeploymentTargetSetupRules.NameMaxLength)]
    public required string Name { get; init; }

    [MaxLength(64)]
    public string? Region { get; init; }

    [MaxLength(64)]
    public string? Plan { get; init; }
}

/// <summary>FR-144: link an existing platform resource (from <c>GET /api/me/connections/platforms/:id/resources</c>).</summary>
public sealed record LinkDeploymentResourceRequest
{
    [Required]
    [MaxLength(DeploymentTargetSetupRules.ResourceIdMaxLength)]
    public required string ResourceId { get; init; }
}

/// <summary>
/// FR-144 (Owner): set up a target. <c>provider</c> must be the composition's host for the component (<c>fake</c> also in
/// Fake mode); <c>connectionId</c> one of the caller's own connections of that platform (not used by GitHub Pages);
/// <c>create</c> or <c>link</c>, not both (GitHub Pages: neither; setup enables Pages on the repository).
/// </summary>
public sealed record PutDeploymentTargetRequest
{
    public required DeploymentProvider Provider { get; init; }

    public Guid? ConnectionId { get; init; }

    public CreateDeploymentResourceRequest? Create { get; init; }

    public LinkDeploymentResourceRequest? Link { get; init; }
}

/// <summary>FR-144: the readiness checklist of <c>POST …/targets/:component/verify</c>, with the target after the check.</summary>
public sealed record DeploymentVerifyDto(DeploymentTargetDto Target, IReadOnlyList<ReadinessItemDto> Items);

/// <summary>
/// Create or replace a user environment variable (FR-88, FR-145): the value (up to 64 KB; encrypted, never returned), its
/// environments (<c>targets</c>: <c>preview</c>, <c>production</c>), whether it is sensitive, the components it applies to
/// (<c>appliesTo</c>, default <c>["frontend"]</c>) and whether it is public (default: the key has the frontend pack's public
/// prefix; a prefixed key is always public). A public variable that looks secret, or a key AIDE derives, is
/// <c>VALIDATION</c>.
/// </summary>
public sealed record PutDeploymentEnvRequest
{
    [Required]
    public required string Value { get; init; }

    [Required]
    public required IReadOnlyList<DeploymentEnvironment> Targets { get; init; }

    public bool Sensitive { get; init; }

    public IReadOnlyList<DeploymentComponent>? AppliesTo { get; init; }

    public bool? Public { get; init; }
}
