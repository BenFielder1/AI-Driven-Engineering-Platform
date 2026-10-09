namespace Aide.Domain.Entities;

/// <summary>A release attempt (FR-42, FR-43); attempts are numbered per project version. <see cref="Tag"/> is the base tag until the release succeeds, then the tag it was recorded with (<c>v/// <summary>A release attempt (FR-42, FR-43). At most one succeeded release per project version.</summary>lt;N/// <summary>A release attempt (FR-42, FR-43). At most one succeeded release per project version.</summary>gt;-aide</c> or <c>v/// <summary>A release attempt (FR-42, FR-43). At most one succeeded release per project version.</summary>lt;N/// <summary>A release attempt (FR-42, FR-43). At most one succeeded release per project version.</summary>gt;-aide./// <summary>A release attempt (FR-42, FR-43). At most one succeeded release per project version.</summary>lt;k/// <summary>A release attempt (FR-42, FR-43). At most one succeeded release per project version.</summary>gt;</c>).</summary>
public sealed class Release
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public int ProjectVersion { get; set; }

    public string Tag { get; set; } = string.Empty;

    public string? MergeCommitSha { get; set; }

    public string? PrUrl { get; set; }

    public ReleaseStatus Status { get; set; }

    /// <summary>1 for the first attempt at this project version; retries after a failure increment it (FR-43).</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>Machine-readable failure code (§7 error table), e.g. <c>MERGE_CONFLICT</c>; null unless failed.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Human-readable failure message; null unless failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Files in conflict when the merge failed (FR-43).</summary>
    public IReadOnlyList<string> ConflictingFiles { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the attempt reached <c>succeeded</c> or <c>failed</c>; null while <c>in_progress</c>.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}
