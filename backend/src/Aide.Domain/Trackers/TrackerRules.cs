using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Lifecycle;

namespace Aide.Domain.Trackers;

/// <summary>How a tracker request failed (FR-165–FR-173), as adapters classify it.</summary>
public enum TrackerFailure
{
    /// <summary>The tracker rejected the token (401).</summary>
    Rejected = 0,

    /// <summary>The token lacks read access (403).</summary>
    MissingPermissions = 1,

    /// <summary>429 or the tracker's rate-limit answer.</summary>
    RateLimited = 2,

    NotFound = 3,

    /// <summary>The tracker refused the native query.</summary>
    QueryInvalid = 4,

    /// <summary>The AIDE GitHub App doesn't have Issues: read on the installation (FR-167).</summary>
    AppPermissionMissing = 5,

    /// <summary>No usable connection reaches this (e.g. the App isn't installed where the repository lives).</summary>
    NotConnected = 6,

    /// <summary>Any other failure, or the tracker couldn't be reached.</summary>
    Upstream = 7,
}

/// <summary>Pure rules of tracker connections and imports (FR-165–FR-173, D-V5-6, D-V5-10).</summary>
public static partial class TrackerRules
{
    public const int MaxQueryLength = 2_000;

    public const int MaxContainerIdLength = 300;

    public const int MaxFilterTextLength = 300;

    public const int MaxImportsPerRequest = 10;

    public const int MaxEmailLength = 254;

    public const int MaxFieldIdLength = 100;

    /// <summary>The phases where items can be imported or removed (D-V5-10, FR-171): <c>analysis</c> and <c>refinement</c>.</summary>
    public static IReadOnlyList<Phase> ImportPhases { get; } = [Phase.Analysis, Phase.Refinement];

    /// <summary>FR-165: AIDE warns this long before a Jira or Linear connection's user-entered expiry.</summary>
    public static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(14);

    /// <summary>The platform connection providers that are issue trackers (FR-165, FR-166), whose expiry the user enters.</summary>
    public static IReadOnlyList<Entities.PlatformProvider> ConnectionProviders { get; } = [Entities.PlatformProvider.Jira, Entities.PlatformProvider.Linear];

    /// <summary>
    /// FR-165: whether the expiry sweep warns the owner of a connection now: a Jira or Linear connection that isn't <c>invalid</c>
    /// (a rejected token already told them), whose expiry is still ahead and at most 14 days away, and whose owner wasn't warned of
    /// that same date before. A changed expiry date warns again; a date further away than 14 days, or none, never does.
    /// </summary>
    public static bool WarnsOfExpiry(
        Entities.PlatformProvider provider,
        Entities.PlatformConnectionStatus status,
        DateTimeOffset? expiresAt,
        DateTimeOffset now,
        bool warnedOfThisDate) =>
        ConnectionProviders.Contains(provider)
        && status != Entities.PlatformConnectionStatus.Invalid
        && expiresAt is { } expires
        && expires > now
        && expires - now <= ExpiryWarningWindow
        && !warnedOfThisDate;

    /// <summary>The tracker's name in messages.</summary>
    public static string DisplayName(TrackerProvider provider) => provider switch
    {
        TrackerProvider.Jira => "Jira",
        TrackerProvider.Linear => "Linear",
        TrackerProvider.Github => "GitHub Issues",
        TrackerProvider.Gitlab => "GitLab Issues",
        _ => "The tracker",
    };

    /// <summary>
    /// FR-165, §8 SSRF: the site as a bare host matching <c>^[a-z0-9-]+\.atlassian\.net$</c>, or <c>TRACKER_SITE_NOT_ALLOWED</c>. Only
    /// a leading <c>https://</c> and one trailing <c>/</c> are forgiven, so user info, ports, paths or another host always fail.
    /// Called before any network request.
    /// </summary>
    public static Result<string> NormalizeJiraSite(string? site)
    {
        var text = site?.Trim().ToLowerInvariant() ?? string.Empty;
        if (text.StartsWith("https://", StringComparison.Ordinal))
        {
            text = text["https://".Length..];
        }

        if (text.EndsWith('/'))
        {
            text = text[..^1];
        }

        return JiraSite().IsMatch(text)
            ? Result.Success(text)
            : Result.Failure<string>(new DomainError(ErrorCodes.TrackerSiteNotAllowed, "Enter your Jira Cloud site, like acme.atlassian.net.") { Field = "site" });
    }

    /// <summary>FR-165: a Jira connection's fields other than the site: a plausible email and, when given, a custom field id.</summary>
    public static Result ValidateJiraConnection(string? email, string? acceptanceCriteriaField)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(email) || email.Length > MaxEmailLength || !PlausibleEmail().IsMatch(email.Trim()))
        {
            problems.Add("email: Enter your Atlassian account email.");
        }

        if (!IsFieldId(acceptanceCriteriaField))
        {
            problems.Add("acceptanceCriteriaField: Pick a field from the list.");
        }

        return Projects.ProjectRules.ToResult(problems, "The connection is invalid.");
    }

    /// <summary>FR-165: an empty value (no field) or a Jira field id, e.g. <c>customfield_10042</c>.</summary>
    public static bool IsFieldId(string? fieldId) =>
        string.IsNullOrEmpty(fieldId) || (fieldId.Length <= MaxFieldIdLength && FieldId().IsMatch(fieldId));

    /// <summary>
    /// D-V5-10: items can be imported, re-imported or removed only in <see cref="ImportPhases"/>; otherwise <c>WRONG_PHASE</c>
    /// (new work belongs in a new version, or after a rollback).
    /// </summary>
    public static Result CanChangeImports(Phase phase) =>
        ImportPhases.Contains(phase)
            ? Result.Success()
            : Result.Failure(new DomainError(
                ErrorCodes.WrongPhase,
                $"Tracker items can't be changed in {phase.ToWire()}. Start a new version, or roll back, to add them."));

    /// <summary>
    /// FR-169, FR-171: a selection: ticked keys (at most 200, in the provider's key format) or a filter or native query. The
    /// native query is JQL for Jira and search qualifiers for GitHub; GitLab and Linear offer the structured filters only.
    /// GitHub and GitLab read from one repository or project, so they need a container with anything but keys.
    /// </summary>
    public static Result ValidateSelection(TrackerProvider provider, TrackerSelection? selection, string prefix = "selection.")
    {
        if (selection is null)
        {
            return Fail($"{prefix.TrimEnd('.')}: Choose items, a filter or a query.");
        }

        var problems = new List<string>();
        var keys = selection.Keys ?? [];
        var hasQuery = !string.IsNullOrWhiteSpace(selection.Query);
        if (keys.Count > 0 && (hasQuery || selection.Filter is not null))
        {
            problems.Add($"{prefix}keys: Send keys, or a query or filter, not both.");
        }

        if (keys.Count == 0 && !hasQuery && selection.Filter is null && string.IsNullOrWhiteSpace(selection.ContainerId))
        {
            problems.Add($"{prefix}keys: Choose items, a filter or a query.");
        }

        if (keys.Any(k => !IsKey(provider, k)))
        {
            problems.Add($"{prefix}keys: Every key must look like {KeyExample(provider)}.");
        }

        if (hasQuery && provider is TrackerProvider.Linear or TrackerProvider.Gitlab)
        {
            problems.Add($"{prefix}query: {DisplayName(provider)} offers the structured filters only.");
        }

        if (new[] { selection.Query, selection.ContainerId, selection.Filter?.Text, selection.Filter?.Parent }
            .Concat(selection.Filter?.Labels ?? [])
            .Any(t => t?.Contains('\0', StringComparison.Ordinal) == true))
        {
            problems.Add($"{prefix}query: The text can't contain NUL characters.");
        }

        if (selection.Query?.Length > MaxQueryLength)
        {
            problems.Add($"{prefix}query: The query can't be longer than {MaxQueryLength} characters.");
        }

        if (selection.ContainerId?.Length > MaxContainerIdLength)
        {
            problems.Add($"{prefix}containerId: The container id is too long.");
        }

        if (selection.Filter?.Text?.Length > MaxFilterTextLength)
        {
            problems.Add($"{prefix}filter.text: The text can't be longer than {MaxFilterTextLength} characters.");
        }

        if (provider is TrackerProvider.Github or TrackerProvider.Gitlab && keys.Count == 0 && string.IsNullOrWhiteSpace(selection.ContainerId))
        {
            problems.Add($"{prefix}containerId: Choose a {(provider == TrackerProvider.Github ? "repository" : "project")}.");
        }

        if (provider is TrackerProvider.Github or TrackerProvider.Gitlab && keys.Select(RepositoryOf).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            problems.Add($"{prefix}keys: One import reads one {(provider == TrackerProvider.Github ? "repository" : "project")}.");
        }

        return problems.Count == 0 ? SourceItemLimits.CheckItemCount(keys.Count) : Projects.ProjectRules.ToResult(problems, "The selection is invalid.");
    }

    /// <summary>Whether <paramref name="key"/> has the provider's format (§4.26).</summary>
    public static bool IsKey(TrackerProvider provider, string? key) => key is { Length: > 0 and <= 300 } && provider switch
    {
        TrackerProvider.Jira or TrackerProvider.Linear => IssueKey().IsMatch(key),
        TrackerProvider.Github => RepositoryKey().IsMatch(key),
        TrackerProvider.Gitlab => NamespacedKey().IsMatch(key),
        _ => false,
    };

    /// <summary>The repository or project of an <c>owner/repo#7</c> key (the key itself when it has none).</summary>
    public static string RepositoryOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var hash = key.LastIndexOf('#');
        return hash < 0 ? key : key[..hash];
    }

    /// <summary>The issue number of an <c>owner/repo#7</c> key.</summary>
    public static string IssueNumberOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key[(key.LastIndexOf('#') + 1)..];
    }

    /// <summary>A failed tracker request as the error a person gets (§6, the error table).</summary>
    public static DomainError ToError(TrackerProvider provider, TrackerFailure failure, string? detail = null, TimeSpan? retryAfter = null, string? link = null)
    {
        var name = DisplayName(provider);
        return failure switch
        {
            TrackerFailure.Rejected or TrackerFailure.NotConnected => new DomainError(ErrorCodes.TrackerNotConnected, $"Connect {name} to do this."),
            TrackerFailure.MissingPermissions => new DomainError(ErrorCodes.ConnectionScopeInsufficient, $"The {name} token doesn't have read access to this."),
            TrackerFailure.RateLimited => new DomainError(ErrorCodes.UpstreamRateLimited, $"{name} is rate limiting requests. Try again in a few minutes.") { RetryAfter = retryAfter },
            TrackerFailure.NotFound => new DomainError(ErrorCodes.NotFound, $"{name} couldn't find that, or the connection can't see it."),
            TrackerFailure.QueryInvalid => new DomainError(ErrorCodes.TrackerQueryInvalid, $"{name} couldn't run that query: {detail ?? "the query is invalid"}"),
            TrackerFailure.AppPermissionMissing => new DomainError(
                ErrorCodes.TrackerPermissionMissing,
                "Issues permission missing: update the App's permissions in GitHub, then accept them on each installation.")
            { Reason = link },
            _ => new DomainError(ErrorCodes.UpstreamError, $"{name} returned an error or couldn't be reached. Try again later."),
        };
    }

    /// <summary>FR-172: the budget a run's rendered snapshot must fit (§4.26), or the error that fails the run.</summary>
    public static Result CheckRunBudget(int renderedBytes, int? contextTokens) =>
        SourceItemLimits.CheckRenderedSize(renderedBytes, SourceItemLimits.MaxRenderedBytes(contextTokens));

    /// <summary>§4.26: Jira's issue type.</summary>
    public static SourceItemType JiraType(string? issueType) => issueType?.Trim().ToLowerInvariant() switch
    {
        "epic" => SourceItemType.Epic,
        "story" => SourceItemType.Story,
        "task" => SourceItemType.Task,
        "bug" => SourceItemType.Bug,
        "sub-task" or "subtask" => SourceItemType.Subtask,
        _ => SourceItemType.Other,
    };

    /// <summary>§4.26: Jira's status category key (<c>new</c>, <c>indeterminate</c>, <c>done</c>).</summary>
    public static SourceItemStatusCategory JiraStatusCategory(string? key) => key switch
    {
        "done" => SourceItemStatusCategory.Done,
        "indeterminate" => SourceItemStatusCategory.InProgress,
        _ => SourceItemStatusCategory.Todo,
    };

    /// <summary>§4.26: a Linear sub-issue is a <c>subtask</c>, the label <c>bug</c> a <c>bug</c>, anything else a <c>task</c>.</summary>
    public static SourceItemType LinearType(bool isSubIssue, IEnumerable<string> labels) =>
        isSubIssue ? SourceItemType.Subtask : HasBugLabel(labels) ? SourceItemType.Bug : SourceItemType.Task;

    /// <summary>§4.26: Linear's state type (<c>backlog</c>, <c>unstarted</c> → todo; <c>started</c> → in progress; <c>completed</c>, <c>canceled</c> → done).</summary>
    public static SourceItemStatusCategory LinearStatusCategory(string? stateType) => stateType switch
    {
        "started" => SourceItemStatusCategory.InProgress,
        "completed" or "canceled" => SourceItemStatusCategory.Done,
        _ => SourceItemStatusCategory.Todo,
    };

    /// <summary>§4.26: GitHub's issue type if set, else the label <c>bug</c> → <c>bug</c>, else <c>task</c>.</summary>
    public static SourceItemType GitHubType(string? issueType, IEnumerable<string> labels) => issueType?.Trim().ToLowerInvariant() switch
    {
        "epic" => SourceItemType.Epic,
        "story" or "feature" => SourceItemType.Story,
        "bug" => SourceItemType.Bug,
        "task" => SourceItemType.Task,
        "sub-task" or "subtask" => SourceItemType.Subtask,
        { Length: > 0 } => SourceItemType.Other,
        _ => HasBugLabel(labels) ? SourceItemType.Bug : SourceItemType.Task,
    };

    /// <summary>§4.26: GitLab's <c>issue</c> → <c>task</c>, <c>incident</c> → <c>bug</c>, <c>task</c> → <c>subtask</c>; the label <c>bug</c> → <c>bug</c>.</summary>
    public static SourceItemType GitLabType(string? issueType, IEnumerable<string> labels)
    {
        if (HasBugLabel(labels))
        {
            return SourceItemType.Bug;
        }

        return issueType?.Trim().ToLowerInvariant() switch
        {
            "incident" => SourceItemType.Bug,
            "task" => SourceItemType.Subtask,
            "issue" or null or "" => SourceItemType.Task,
            _ => SourceItemType.Other,
        };
    }

    /// <summary>§4.26: GitHub and GitLab: open → todo, closed → done.</summary>
    public static SourceItemStatusCategory OpenClosedCategory(string? state) =>
        string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase) ? SourceItemStatusCategory.Done : SourceItemStatusCategory.Todo;

    /// <summary>Whether a filter asks for done items (FR-169: hidden otherwise).</summary>
    public static bool ShowsDone(TrackerFilter? filter) => filter?.StatusCategories?.Contains(SourceItemStatusCategory.Done) == true;

    private static bool HasBugLabel(IEnumerable<string> labels) => labels.Any(l => string.Equals(l, "bug", StringComparison.OrdinalIgnoreCase));

    private static string KeyExample(TrackerProvider provider) => provider switch
    {
        TrackerProvider.Github => "owner/repo#7",
        TrackerProvider.Gitlab => "group/project#7",
        _ => "PROJ-123",
    };

    private static Result Fail(string detail) =>
        Result.Failure(new DomainError(ErrorCodes.Validation, "The selection is invalid.") { Details = [detail] });

    [GeneratedRegex(@"^[a-z0-9-]+\.atlassian\.net\z", RegexOptions.CultureInvariant)]
    private static partial Regex JiraSite();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlausibleEmail();

    [GeneratedRegex(@"^[A-Za-z0-9_.:-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex FieldId();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]*-[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex IssueKey();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]*)/[A-Za-z0-9._-]+#[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryKey();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)+#[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamespacedKey();
}
