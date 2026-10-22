namespace Aide.Domain.Entities;

/// <summary>
/// A release version of a project (§4.10, FR-60–FR-67): <c>in_development → (awaiting_merge →) released</c>, or
/// <c>imported</c> for a baseline never developed in AIDE. At most one version per project is <c>in_development</c> or
/// <c>awaiting_merge</c> (partial unique index). The project's Git branch and pull request belong to the version.
/// </summary>
public sealed class ReleaseVersion
{
    /// <summary>Default release tag prefix (§4.10).</summary>
    public const string DefaultTagPrefix = "v";

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    /// <summary>Normalised semver without a leading <c>v</c>, e.g. <c>1.2.0</c>.</summary>
    public string Version { get; set; } = string.Empty;

    public ReleaseVersionStatus Status { get; set; } = ReleaseVersionStatus.InDevelopment;

    /// <summary>The tag the version was released (or imported) with; v1 migrated versions keep their <c>v&lt;N&gt;-aide</c> tag.</summary>
    public string? Tag { get; set; }

    public string TagPrefix { get; set; } = DefaultTagPrefix;

    /// <summary>The released tag the version started from (FR-60); null for a first version.</summary>
    public string? BaseTag { get; set; }

    public string? BaseCommitSha { get; set; }

    public string? MergeCommitSha { get; set; }

    /// <summary>The working branch of the version, e.g. <c>aide/&lt;slug&gt;-&lt;short-id&gt;</c> (v1 name kept on migration).</summary>
    public string? BranchName { get; set; }

    /// <summary>The version's pull request (FR-36), merged by the release job (FR-64, FR-65).</summary>
    public string? PullRequestUrl { get; set; }

    public int? PullRequestNumber { get; set; }

    /// <summary>The request the version was started with (FR-60); the project idea for a first version.</summary>
    public string Request { get; set; } = string.Empty;

    public string? PresetId { get; set; }

    public string? PresetVersion { get; set; }

    public string? PresetHash { get; set; }

    public Guid? StartedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>): the release job and the merge poll both update the row.</summary>
    public uint RowVersion { get; set; }
}
