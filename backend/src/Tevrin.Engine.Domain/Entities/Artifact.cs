using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Domain.Entities;

/// <summary>
/// A versioned artifact (FR-16, §4.3). Identity and content (<see cref="Content"/>, <see cref="ContentHash"/>,
/// type, version, parent, author) are init-only: the only mutable columns are status and the approval/rejection
/// fields. New content means a new version (CLAUDE.md rule 10).
/// </summary>
public sealed class Artifact
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid ProjectId { get; init; }

    public string Type { get; init; } = string.Empty;

    public int Version { get; init; }

    public int? ParentVersion { get; init; }

    public string Content { get; init; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the normalised content (<c>ContentHash.Compute</c>).</summary>
    public string ContentHash { get; init; } = string.Empty;

    public ArtifactStatus Status { get; set; } = ArtifactStatus.Draft;

    /// <summary>The lifecycle checkpoint the artifact was created at (§4.10).</summary>
    public int CheckpointAtCreation { get; init; }

    /// <summary>The release version the artifact belongs to (§4.10); FR-334: <c>null</c> for an artifact of a run without a
    /// subject, which belongs to the run alone (<see cref="PipelineRunId"/>).</summary>
    public Guid? ReleaseVersionId { get; init; }

    /// <summary>FR-294: the pipeline run the artifact belongs to (nullable until the contract step, D-0.7.0-28).</summary>
    public Guid? PipelineRunId { get; init; }

    /// <summary>
    /// FR-352: <see cref="ArtifactScope.Project"/> for a source-of-truth artifact: one version history per project (no run,
    /// no release version), unique by (project, type, version). Runs read it through their pinned version.
    /// </summary>
    public ArtifactScope Scope { get; init; } = ArtifactScope.Run;

    /// <summary>FR-58: the version whose content a restored draft copies; null otherwise.</summary>
    public int? RestoredFromVersion { get; init; }

    /// <summary>FR-343–FR-349, §5: the variant label when a stage produces variants of one version; null otherwise.</summary>
    public string? Variant { get; init; }

    public Guid? CreatedByUserId { get; init; }

    public Guid? AgentRunId { get; init; }

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public string? RejectionComment { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public uint RowVersion { get; set; }
}
