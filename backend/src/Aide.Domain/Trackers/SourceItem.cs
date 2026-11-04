namespace Aide.Domain.Trackers;

/// <summary>An issue tracker AIDE imports from (FR-165–FR-168). Wire values: <c>jira</c>, <c>linear</c>, <c>github</c>, <c>gitlab</c>.</summary>
public enum TrackerProvider
{
    Jira = 0,
    Linear = 1,
    Github = 2,
    Gitlab = 3,
}

/// <summary>
/// Where an import is (§5). <c>completed</c> imports are the version's <b>active</b> imports; <c>superseded</c> (re-imported)
/// and <c>removed</c> ones stay as history. Wire values: <c>queued</c>, <c>running</c>, <c>completed</c>, <c>failed</c>,
/// <c>superseded</c>, <c>removed</c>.
/// </summary>
public enum TrackerImportStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Superseded = 4,
    Removed = 5,
}

/// <summary>§4.26: the normalised status of an item. Wire values: <c>todo</c>, <c>in_progress</c>, <c>done</c>.</summary>
public enum SourceItemStatusCategory
{
    Todo = 0,
    InProgress = 1,
    Done = 2,
}

/// <summary>§4.26: the normalised type of an item. Wire values: <c>epic</c>, <c>story</c>, <c>task</c>, <c>bug</c>, <c>subtask</c>, <c>other</c>.</summary>
public enum SourceItemType
{
    Epic = 0,
    Story = 1,
    Task = 2,
    Bug = 3,
    Subtask = 4,
    Other = 5,
}

/// <summary>FR-173: how an imported item compares with the tracker now. Wire values: <c>unchanged</c>, <c>changed</c>, <c>removed</c>, <c>unknown</c>.</summary>
public enum SourceItemDrift
{
    Unchanged = 0,
    Changed = 1,
    Removed = 2,
    Unknown = 3,
}

/// <summary>A link between items (<c>blocks</c>, <c>relates to</c>), by the other item's key.</summary>
public sealed record SourceItemLink(string Relation, string Key);

/// <summary>A comment (opt-in, §4.26): the author's display name, never an email.</summary>
public sealed record SourceItemComment(string? Author, DateTimeOffset CreatedAt, string BodyMarkdown);

/// <summary>§4.26: a tracker item, the same shape from every provider. Holds no email and no token.</summary>
public sealed record SourceItem
{
    public required TrackerProvider Provider { get; init; }

    public required string ExternalId { get; init; }

    /// <summary><c>PROJ-123</c>, <c>ENG-42</c>, <c>owner/repo#7</c>, <c>group/project#7</c>.</summary>
    public required string Key { get; init; }

    public required string Url { get; init; }

    public required SourceItemType Type { get; init; }

    public required string NativeType { get; init; }

    public required string Title { get; init; }

    public string DescriptionMarkdown { get; init; } = string.Empty;

    public required string Status { get; init; }

    public required SourceItemStatusCategory StatusCategory { get; init; }

    public string? Priority { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Only when the import opted in (§4.26).</summary>
    public string? Assignee { get; init; }

    public string? Estimate { get; init; }

    public string? ParentKey { get; init; }

    public IReadOnlyList<string> ChildKeys { get; init; } = [];

    public IReadOnlyList<SourceItemLink> Links { get; init; } = [];

    public string? AcceptanceCriteria { get; init; }

    /// <summary>Only when the import opted in (§4.26); newest first after normalising.</summary>
    public IReadOnlyList<SourceItemComment>? Comments { get; init; }

    /// <summary>§4.26: how many older comments were dropped.</summary>
    public int CommentsDropped { get; init; }

    /// <summary>FR-169: attachments are never imported, only their names.</summary>
    public IReadOnlyList<string> AttachmentNames { get; init; } = [];

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>FR-169: structured filters (every provider): title text, status categories (done is hidden unless asked for), labels and parent (epic, Linear project, GitLab milestone).</summary>
public sealed record TrackerFilter
{
    public string? Text { get; init; }

    public IReadOnlyList<SourceItemStatusCategory>? StatusCategories { get; init; }

    public IReadOnlyList<string>? Labels { get; init; }

    public string? Parent { get; init; }
}

/// <summary>
/// §5 <c>tracker_import.query</c>: what an import reads: ticked <see cref="Keys"/>, or everything matching
/// <see cref="Filter"/> or the native <see cref="Query"/> ("Select all matching", up to the cap), in <see cref="ContainerId"/>.
/// </summary>
public sealed record TrackerSelection
{
    public IReadOnlyList<string>? Keys { get; init; }

    public string? Query { get; init; }

    public string? ContainerId { get; init; }

    public TrackerFilter? Filter { get; init; }
}

/// <summary>§5 <c>tracker_import.options</c> (FR-169): include children (default on), comments and assignees (default off).</summary>
public sealed record TrackerImportOptions(bool IncludeChildren = true, bool IncludeComments = false, bool IncludeAssignees = false);

/// <summary>FR-173: one item's drift, by key.</summary>
public sealed record SourceItemDriftResult(string Key, SourceItemDrift Drift);
