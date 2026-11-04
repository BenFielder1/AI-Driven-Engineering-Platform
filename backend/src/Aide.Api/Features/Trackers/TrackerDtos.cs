using System.ComponentModel.DataAnnotations;
using Aide.Api.Features.Activity;
using Aide.Domain.Trackers;

namespace Aide.Api.Features.Trackers;

/// <summary>FR-169: structured filters: title text, status categories (done is hidden unless asked for), labels, and parent (epic key, Linear sub-issue key or project name, GitLab milestone).</summary>
public sealed record TrackerFilterDto
{
    [MaxLength(TrackerRules.MaxFilterTextLength)]
    public string? Text { get; init; }

    public IReadOnlyList<SourceItemStatusCategory>? StatusCategories { get; init; }

    public IReadOnlyList<string>? Labels { get; init; }

    [MaxLength(TrackerRules.MaxFilterTextLength)]
    public string? Parent { get; init; }
}

/// <summary>
/// FR-169, FR-171: what an import reads: ticked <c>keys</c> (at most 200), or everything matching <c>filter</c> or the native
/// <c>query</c> (JQL for Jira, search qualifiers for GitHub; GitLab and Linear offer filters only), in <c>containerId</c> (a Jira
/// project key, Linear <c>team:KEY</c> or <c>project:ID</c>, GitHub <c>owner/repo</c>, GitLab <c>group/project</c>).
/// </summary>
public sealed record TrackerSelectionDto
{
    public IReadOnlyList<string>? Keys { get; init; }

    [MaxLength(TrackerRules.MaxQueryLength)]
    public string? Query { get; init; }

    [MaxLength(TrackerRules.MaxContainerIdLength)]
    public string? ContainerId { get; init; }

    public TrackerFilterDto? Filter { get; init; }
}

/// <summary>FR-169: include children (default on), comments and assignees (default off).</summary>
public sealed record TrackerImportOptionsDto
{
    public bool IncludeChildren { get; init; } = true;

    public bool IncludeComments { get; init; }

    public bool IncludeAssignees { get; init; }
}

/// <summary>
/// FR-171: one import: a tracker, the connection (one of the caller's own; omitted: their newest usable one; ignored for GitHub
/// beyond being the caller's), the selection and options. A <c>sourceImports[]</c> entry on project and version creation, and
/// the body of <c>POST /sources</c>.
/// </summary>
public sealed record SourceImportRequest
{
    public required TrackerProvider Provider { get; init; }

    public Guid? ConnectionId { get; init; }

    public required TrackerSelectionDto Selection { get; init; }

    public TrackerImportOptionsDto? Options { get; init; }
}

/// <summary>A connection in the Issue trackers section. <c>id</c> is what to send as <c>connectionId</c>; <c>status</c> is <c>valid</c>, <c>invalid</c> or <c>insufficient_scope</c>; <c>detail</c> the Jira site, Linear organisation, GitHub login or GitLab instance.</summary>
public sealed record TrackerConnectionSummaryDto(Guid Id, string Label, string Status, string? AccountName, string? Detail);

/// <summary>
/// FR-165: one tracker for the Issue trackers section: whether it's <c>ready</c> to browse and, if not, why (<c>reason</c>:
/// <c>not_connected</c>, <c>app_not_registered</c> or <c>issues_permission_missing</c>, with the App settings link in <c>settingsUrl</c>).
/// </summary>
public sealed record TrackerStatusDto(
    TrackerProvider Provider,
    string Name,
    bool Ready,
    string? Reason,
    string? SettingsUrl,
    IReadOnlyList<TrackerConnectionSummaryDto> Connections);

/// <summary>A project, team or repository to browse; send <c>id</c> as <c>containerId</c>.</summary>
public sealed record TrackerContainerDto(string Id, string Name, string? Detail, string? Url);

public sealed record TrackerContainerPageDto(IReadOnlyList<TrackerContainerDto> Items, string? NextCursor);

/// <summary>FR-169: browse results. Send <c>query</c> or <c>filter</c>, and <c>cursor</c> from the previous page.</summary>
public sealed record TrackerSearchRequest
{
    public Guid? ConnectionId { get; init; }

    [MaxLength(TrackerRules.MaxContainerIdLength)]
    public string? ContainerId { get; init; }

    public TrackerFilterDto? Filter { get; init; }

    [MaxLength(TrackerRules.MaxQueryLength)]
    public string? Query { get; init; }

    [MaxLength(4000)]
    public string? Cursor { get; init; }
}

/// <summary>An item without its body: for the browse dialog, chips and the sources list.</summary>
public sealed record TrackerItemSummaryDto(
    string Key,
    string Title,
    string Url,
    SourceItemType Type,
    string Status,
    SourceItemStatusCategory StatusCategory,
    IReadOnlyList<string> Labels,
    string? ParentKey,
    DateTimeOffset UpdatedAt);

public sealed record TrackerSearchResultDto(IReadOnlyList<TrackerItemSummaryDto> Items, string? NextCursor);

/// <summary>
/// One import (§5). <c>status</c>: <c>queued</c>, <c>running</c>, <c>completed</c> (active), <c>failed</c> (see <c>errorCode</c>;
/// re-import to retry), <c>superseded</c>, <c>removed</c>. <c>errorCode</c> is <c>TRACKER_RATE_LIMITED</c> while a queued job waits.
/// <c>sourceArtifactId</c> is the <c>source_items</c> version its completion wrote.
/// </summary>
public sealed record SourceImportDto(
    Guid Id,
    Guid ReleaseVersionId,
    TrackerProvider Provider,
    Guid? ConnectionId,
    TrackerImportStatus Status,
    string? ErrorCode,
    string? ErrorMessage,
    TrackerSelectionDto Selection,
    TrackerImportOptionsDto Options,
    int ItemCount,
    int RenderedBytes,
    IReadOnlyList<TrackerItemSummaryDto> Items,
    Guid? SourceArtifactId,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>The current <c>source_items</c> version (read it through the artifact routes).</summary>
public sealed record SourceSnapshotDto(Guid ArtifactId, int Version, string ContentHash, DateTimeOffset CreatedAt);

/// <summary>
/// FR-173: the latest project request draft against the snapshot. <c>builtFromSnapshotVersion</c> is what its run listed (<c>null</c>:
/// a person's edit, or none). <c>behindSnapshot</c> true drives "Sources changed since this draft: run another round" and the
/// confirmation on approving it.
/// </summary>
public sealed record SourceDraftStateDto(Guid ArtifactId, int Version, int? BuiltFromSnapshotVersion, bool BehindSnapshot);

/// <summary>
/// §6 <c>GET /sources</c>: a release version's imports, newest first, its current snapshot and draft state, and whether items can be
/// changed now (<c>canChange</c>: the project is in an import phase and this is the current version).
/// </summary>
public sealed record ProjectSourcesDto(
    Guid? ReleaseVersionId,
    IReadOnlyList<SourceImportDto> Imports,
    SourceSnapshotDto? CurrentSnapshot,
    SourceDraftStateDto? Draft,
    bool CanChange);

/// <summary>One imported item's drift.</summary>
public sealed record SourceDriftItemDto(Guid ImportId, TrackerProvider Provider, string Key, string Title, string Url, SourceItemDrift Drift);

/// <summary>§6 <c>POST /sources/check</c>: per-item drift, with the snapshot's hash (a check never changes it).</summary>
public sealed record SourcesCheckDto(IReadOnlyList<SourceDriftItemDto> Items, string? SnapshotContentHash, DateTimeOffset CheckedAt);

internal static class TrackerDtoMapping
{
    public static TrackerFilter? ToDomain(this TrackerFilterDto? filter) => filter is null
        ? null
        : new TrackerFilter
        {
            Text = string.IsNullOrWhiteSpace(filter.Text) ? null : filter.Text.Trim(),
            StatusCategories = filter.StatusCategories,
            Labels = filter.Labels,
            Parent = string.IsNullOrWhiteSpace(filter.Parent) ? null : filter.Parent.Trim(),
        };

    public static TrackerSelection ToDomain(this TrackerSelectionDto? selection) => selection is null
        ? new TrackerSelection()
        : new TrackerSelection
        {
            Keys = selection.Keys,
            Query = string.IsNullOrWhiteSpace(selection.Query) ? null : selection.Query.Trim(),
            ContainerId = string.IsNullOrWhiteSpace(selection.ContainerId) ? null : selection.ContainerId.Trim(),
            Filter = selection.Filter.ToDomain(),
        };

    public static TrackerImportOptions ToDomain(this TrackerImportOptionsDto? options) =>
        options is null ? new TrackerImportOptions() : new TrackerImportOptions(options.IncludeChildren, options.IncludeComments, options.IncludeAssignees);

    public static TrackerSelectionDto ToDto(this TrackerSelection selection) => new()
    {
        Keys = selection.Keys,
        Query = selection.Query,
        ContainerId = selection.ContainerId,
        Filter = selection.Filter is { } filter
            ? new TrackerFilterDto { Text = filter.Text, StatusCategories = filter.StatusCategories, Labels = filter.Labels, Parent = filter.Parent }
            : null,
    };

    public static TrackerImportOptionsDto ToDto(this TrackerImportOptions options) => new()
    {
        IncludeChildren = options.IncludeChildren,
        IncludeComments = options.IncludeComments,
        IncludeAssignees = options.IncludeAssignees,
    };

    public static TrackerItemSummaryDto ToSummary(this SourceItem item) =>
        new(item.Key, item.Title, item.Url, item.Type, item.Status, item.StatusCategory, item.Labels, item.ParentKey, item.UpdatedAt);
}
