using Aide.Domain.Access;

namespace Aide.Domain.Entities;

/// <summary>A user's role on a project (FR-8); table <c>project_members</c>. "At least one Owner" is enforced by
/// <c>MembershipRules</c>. (Named to avoid clashing with the <c>Access.ProjectMember</c> rule record.)</summary>
public sealed class ProjectMembership
{
    public Guid ProjectId { get; set; }

    public Guid UserId { get; set; }

    public ProjectRole Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
