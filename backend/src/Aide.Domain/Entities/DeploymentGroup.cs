namespace Aide.Domain.Entities;

/// <summary>
/// §4.28, §5: a project's deployment group on one platform (table <c>deployment_groups</c>, unique per (project, provider)):
/// one connection, one name, one create or link choice. <see cref="SharedResourceId"/> is the platform project (Railway,
/// Render, Fake), stored as soon as it exists; <c>null</c> on Vercel, which shares a setup and has no shared resource
/// (D-V5-8). Parts point here with <c>group_id</c>. Edited concurrently (job, Owner), so <see cref="RowVersion"/> maps <c>xmin</c>.
/// </summary>
public sealed class DeploymentGroup
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public DeploymentProvider Provider { get; set; } = DeploymentProvider.Railway;

    public string Name { get; set; } = string.Empty;

    /// <summary>The Owner's connection for every part; cleared (ON DELETE SET NULL) when it is deleted.</summary>
    public Guid? ConnectionId { get; set; }

    public Guid? BoundByUserId { get; set; }

    public string? SharedResourceId { get; set; }

    public string? Region { get; set; }

    public string? Plan { get; set; }

    public DeploymentGroupStatus Status { get; set; } = DeploymentGroupStatus.Configuring;

    /// <summary>The failure code, or a fact such as <see cref="Deployments.DeploymentGroupRules.PlacementUnavailable"/>.</summary>
    public string? StatusReason { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint RowVersion { get; set; }
}

/// <summary>A group's state (§5). Wire values: <c>configuring</c>, <c>ready</c>, <c>attention</c>, <c>error</c>.</summary>
public enum DeploymentGroupStatus
{
    Configuring = 0,
    Ready = 1,
    Attention = 2,
    Error = 3,
}
