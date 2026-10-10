using Aide.Domain.Artifacts;

namespace Aide.Domain.Entities;

/// <summary>
/// A versioned artifact (FR-16, §4.3). Identity and content (<see cref="Content"/>, <see cref="ContentHash"/>,
/// type, version, parent, author) are init-only: the only mutable columns are status and the approval/rejection
/// fields. New content means a new version (CLAUDE.md rule 10).
/// </summary>
public sealed class Artifact
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid ProjectId { get; init; }

    public ArtifactType Type { get; init; }

    public int Version { get; init; }

    public int? ParentVersion { get; init; }

    public string Content { get; init; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the normalised content (<c>ContentHash.Compute</c>).</summary>
    public string ContentHash { get; init; } = string.Empty;

    public ArtifactStatus Status { get; set; } = ArtifactStatus.Draft;

    public int ProjectVersionAtCreation { get; init; }

    public Guid? CreatedByUserId { get; init; }

    public Guid? AgentRunId { get; init; }

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public string? RejectionComment { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public uint RowVersion { get; set; }
}
