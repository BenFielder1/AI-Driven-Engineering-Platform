using Aide.Domain.Trackers;

namespace Aide.Domain.Entities;

/// <summary>
/// §5, FR-170: one import of tracker items into a release version: a <c>tracker_import</c> job fetches them once, stores them
/// here (provider-shaped payload, so <c>jsonb</c>) and writes a locked <c>source_items</c> version of the version's active
/// imports. Never holds a token or an email. Agents read only the artifact.
/// </summary>
public sealed class TrackerImport
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public Guid ReleaseVersionId { get; set; }

    public TrackerProvider Provider { get; set; }

    /// <summary>The importer's Jira or Linear connection; cleared if it is deleted.</summary>
    public Guid? PlatformConnectionId { get; set; }

    /// <summary>The importer's GitLab connection; cleared if it is deleted.</summary>
    public Guid? GitConnectionId { get; set; }

    /// <summary>The importer's GitHub account when the import was asked for (GitHub imports).</summary>
    public long? GithubUserId { get; set; }

    public TrackerSelection Query { get; set; } = new();

    public TrackerImportOptions Options { get; set; } = new();

    public TrackerImportStatus Status { get; set; } = TrackerImportStatus.Queued;

    /// <summary>The failure, or <c>TRACKER_RATE_LIMITED</c> while the job waits for a rate limit.</summary>
    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public IReadOnlyList<SourceItem> Items { get; set; } = [];

    public int ItemCount { get; set; }

    public int RenderedBytes { get; set; }

    /// <summary>FR-173: the latest drift check. Never touches <see cref="Items"/>.</summary>
    public IReadOnlyList<SourceItemDriftResult>? Drift { get; set; }

    public DateTimeOffset? DriftCheckedAt { get; set; }

    /// <summary>The <c>source_items</c> version this import's completion wrote.</summary>
    public Guid? SourceArtifactId { get; set; }

    /// <summary>FR-173: the import this re-import replaces when it completes; at most one replacement per import. Not exposed in the API.</summary>
    public Guid? SupersedesImportId { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public uint RowVersion { get; set; }
}
