namespace Aide.Domain.Entities;

/// <summary>A release attempt (FR-42, FR-43, FR-64); attempts are numbered per (release version, checkpoint).
/// <see cref="Tag"/> is the base tag until the release succeeds, then the tag it was recorded with.</summary>
public sealed class Release
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    /// <summary>The release version this attempt releases (§4.10).</summary>
    public Guid ReleaseVersionId { get; set; }

    /// <summary>The checkpoint the release was approved at.</summary>
    public int Checkpoint { get; set; }

    public string Tag { get; set; } = string.Empty;

    public string? MergeCommitSha { get; set; }

    public string? PrUrl { get; set; }

    /// <summary>The GitHub Release created for the tag (FR-64); null for Local or before it exists.</summary>
    public string? GithubReleaseUrl { get; set; }

    public ReleaseStatus Status { get; set; }

    /// <summary>1 for the first attempt at this checkpoint; retries after a failure increment it (FR-43).</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>Machine-readable failure code (§7 error table), e.g. <c>MERGE_CONFLICT</c>; null unless failed.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Human-readable failure message; null unless failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Files in conflict when the merge failed (FR-43).</summary>
    public IReadOnlyList<string> ConflictingFiles { get; set; } = [];

    /// <summary>FR-65: when the attempt became <c>awaiting_merge</c>; null otherwise.</summary>
    public DateTimeOffset? AwaitingMergeSince { get; set; }

    /// <summary>FR-65/FR-84: merge-readiness reasons the merge was refused, as raw JSON; null when not refused.</summary>
    public string? MergeReasons { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the attempt reached <c>succeeded</c> or <c>failed</c>; null while <c>in_progress</c> or <c>awaiting_merge</c>.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}
