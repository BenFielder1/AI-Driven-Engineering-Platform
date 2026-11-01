using System.ComponentModel.DataAnnotations;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// One part's progress on a group card (FR-190): <c>part</c>, the database's <c>environment</c>, <c>status</c> (a target's
/// or database's status, <c>not_configured</c> before it has a row), <c>statusReason</c> (the failure code or step) and the
/// platform resource. The part's full card is in <c>targets[]</c> or <c>databases[]</c>.
/// </summary>
public sealed record DeploymentGroupPartDto(DeploymentPart Part, DeploymentEnvironment? Environment, string Status, string? StatusReason, string? ResourceId);

/// <summary>
/// A group card of Settings → Deployment (FR-191): every group the composition forms, plus any configured group it no
/// longer does. <c>id</c> is <c>null</c> and <c>status</c> <c>not_configured</c> until it is set up; then <c>configuring</c>,
/// <c>ready</c>, <c>attention</c> or <c>error</c>. <c>capability</c>: <c>project</c> (one real project,
/// <c>sharedResourceId</c> and its <c>sharedResourceUrl</c>) or <c>shared_setup</c> (Vercel: same team and name, no shared
/// resource). <c>statusReason</c>: the failure code, or <c>project_placement_unavailable</c> (Render project grouping isn't
/// available: the group still works). <c>canRetry</c>: a part failed or is missing and no setup is running.
/// </summary>
public sealed record DeploymentGroupDto(
    Guid? Id,
    DeploymentProvider Provider,
    SharedProjectCapability Capability,
    IReadOnlyList<DeploymentPart> Parts,
    string? Name,
    DeploymentTargetBindingDto? Binding,
    bool CanBind,
    string? SharedResourceId,
    string? SharedResourceUrl,
    string? Region,
    string? Plan,
    string Status,
    string? StatusReason,
    IReadOnlyList<DeploymentGroupPartDto> PartStatuses,
    bool CanRetry,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// FR-190 Link: per part, an existing resource from the connection's picker (<c>GET …/resources</c>: Railway
/// <c>type=services</c>, Render <c>services</c> and <c>databases</c>, Vercel <c>projects</c>); left out means
/// <b>Create in this project</b> (Vercel: every part must name one).
/// </summary>
public sealed record DeploymentGroupLinkParts
{
    [MaxLength(DeploymentTargetSetupRules.ResourceIdMaxLength)]
    public string? Frontend { get; init; }

    [MaxLength(DeploymentTargetSetupRules.ResourceIdMaxLength)]
    public string? Backend { get; init; }

    [MaxLength(DeploymentTargetSetupRules.ResourceIdMaxLength)]
    public string? Database { get; init; }
}

/// <summary>FR-190 Link: the platform project (<c>type=projects</c> in the picker; Vercel has none, so leave it out) and the parts.</summary>
public sealed record LinkDeploymentGroupRequest
{
    [MaxLength(DeploymentTargetSetupRules.ResourceIdMaxLength)]
    public string? ResourceId { get; init; }

    public DeploymentGroupLinkParts? Parts { get; init; }
}

/// <summary>
/// FR-190, FR-191 (Owner): set up a group. <c>connectionId</c> is one of the caller's own connections of the platform;
/// <c>create {name, region?, plan?}</c> a new project, <c>link</c> an existing one, neither a <b>rebind</b> of an existing
/// group to the new connection. <c>plan</c> applies to services; a group's database uses the platform's default plan.
/// </summary>
public sealed record PutDeploymentGroupRequest
{
    public Guid? ConnectionId { get; init; }

    public CreateDeploymentResourceRequest? Create { get; init; }

    public LinkDeploymentGroupRequest? Link { get; init; }
}
