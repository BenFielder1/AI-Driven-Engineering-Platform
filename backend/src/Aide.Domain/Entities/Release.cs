namespace Aide.Domain.Entities;

/// <summary>A release attempt (FR-42, FR-43). At most one succeeded release per project version.</summary>
public sealed class Release
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public int ProjectVersion { get; set; }

    public string Tag { get; set; } = string.Empty;

    public string? MergeCommitSha { get; set; }

    public string? PrUrl { get; set; }

    public ReleaseStatus Status { get; set; }

    public string? Error { get; set; }

    /// <summary>Files in conflict when the merge failed (FR-43).</summary>
    public IReadOnlyList<string> ConflictingFiles { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}
