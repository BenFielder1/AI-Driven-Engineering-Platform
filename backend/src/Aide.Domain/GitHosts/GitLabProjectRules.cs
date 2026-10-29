using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Releases;

namespace Aide.Domain.GitHosts;

/// <summary>A release or tag offered by the import's released-version picker (FR-73, FR-141).</summary>
/// <param name="Tag">The tag name.</param>
/// <param name="IsRelease">A host release (GitLab Release) rather than a bare tag.</param>
/// <param name="IsLatest">The host's latest release.</param>
public sealed record ReleaseRefCandidate(string Tag, bool IsRelease, bool IsLatest);

/// <summary>FR-141: the picker's preselected tag (<c>null</c>: "Never released") and the version to develop it prefills.</summary>
public sealed record ReleaseRefSuggestion(string? PreselectedTag, SemVer DevelopmentVersion);

/// <summary>
/// FR-132 (project binding), FR-136, FR-137, FR-141: the pure rules for a project on a GitLab repository: the create
/// request's repository fields, the repository's full path, Developer access, draft merge request titles, the web URL,
/// and the import's released-version suggestion.
/// </summary>
public static partial class GitLabProjectRules
{
    /// <summary>FR-137, §4.19: GitLab's draft marker; a merge request is ready once it is removed from the title.</summary>
    public const string DraftPrefix = "Draft: ";

    /// <summary>The longest <c>group/sub/project</c> path accepted (GitLab allows 20 levels of 255 characters; AIDE stores 255).</summary>
    public const int MaxFullPathLength = 255;

    /// <summary>The longest repo id accepted (GitLab project ids are integers).</summary>
    public const int MaxRepoIdLength = 20;

    /// <summary>
    /// FR-132, FR-136: the <c>git</c> fields of <c>POST /api/projects</c> with <c>mode: gitlab</c>: the instance, the
    /// caller's connection, GitLab's numeric project id and its <c>group/sub/project</c> path are required; the GitHub and
    /// Local fields don't apply. Field names get <paramref name="prefix"/>.
    /// </summary>
    public static Result ValidateCreate(
        Guid? instanceId,
        Guid? connectionId,
        string? repoId,
        string? repoFullName,
        string? repoUrl,
        string? token,
        long? installationId,
        string prefix = "")
    {
        var problems = new List<string>();
        if (instanceId is null || instanceId == Guid.Empty)
        {
            problems.Add($"{prefix}instanceId: Pick the GitLab instance of the repository.");
        }

        if (connectionId is null || connectionId == Guid.Empty)
        {
            problems.Add($"{prefix}connectionId: Pick one of your GitLab connections.");
        }

        if (!IsValidRepoId(repoId))
        {
            problems.Add($"{prefix}repoId: Pick the repository from the GitLab repo picker.");
        }

        if (!IsValidFullPath(repoFullName))
        {
            problems.Add($"{prefix}repoFullName: Use the project's path, e.g. acme/web/todo-app.");
        }

        if (!string.IsNullOrEmpty(repoUrl))
        {
            problems.Add($"{prefix}repoUrl: Not used with gitlab; send repoId and repoFullName.");
        }

        if (!string.IsNullOrEmpty(token))
        {
            problems.Add($"{prefix}token: Not used with gitlab: AIDE uses your GitLab connection.");
        }

        if (installationId is not null)
        {
            problems.Add($"{prefix}installationId: Only used with github_app.");
        }

        return problems.Count == 0
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Validation, "The Git settings are invalid.") { Details = problems });
    }

    /// <summary>The GitHub-only and GitLab-only fields aren't sent for another mode (<c>VALIDATION</c>).</summary>
    public static Result ValidateNotSent(Guid? instanceId, Guid? connectionId, string? repoId, string prefix = "")
    {
        var problems = new List<string>();
        if (instanceId is not null)
        {
            problems.Add($"{prefix}instanceId: Only used with gitlab.");
        }

        if (connectionId is not null)
        {
            problems.Add($"{prefix}connectionId: Only used with gitlab.");
        }

        if (repoId is not null)
        {
            problems.Add($"{prefix}repoId: Only used with gitlab.");
        }

        return problems.Count == 0
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Validation, "The Git settings are invalid.") { Details = problems });
    }

    /// <summary>A GitLab project id: 1–20 ASCII digits, not zero.</summary>
    public static bool IsValidRepoId(string? repoId) =>
        repoId is { Length: > 0 and <= MaxRepoIdLength } && repoId.All(char.IsAsciiDigit) && repoId.TrimStart('0').Length > 0;

    /// <summary>
    /// A GitLab project's <c>path_with_namespace</c>: two or more segments of letters, digits, <c>_</c>, <c>.</c> and
    /// <c>-</c> (not starting with <c>.</c> or <c>-</c>), at most 255 characters, not ending in <c>.git</c>.
    /// </summary>
    public static bool IsValidFullPath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || fullPath.Length > MaxFullPathLength || fullPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = fullPath.Split('/');
        return segments.Length >= 2 && segments.All(s => PathSegment().IsMatch(s));
    }

    /// <summary>
    /// FR-136, §4.19: the caller's effective access level on a project is the higher of the project membership and the
    /// (inherited) group membership levels GitLab reports in <c>permissions</c>; <c>null</c> when neither is reported.
    /// </summary>
    public static int? EffectiveAccessLevel(int? projectAccess, int? groupAccess) =>
        projectAccess is null && groupAccess is null ? null : Math.Max(projectAccess ?? 0, groupAccess ?? 0);

    /// <summary>FR-136: Developer (30) or higher may push, open merge requests and tag: the binding check.</summary>
    public static bool HasDeveloperAccess(int? projectAccess, int? groupAccess) =>
        GitBindingRules.GitLabHasDeveloperAccess(EffectiveAccessLevel(projectAccess, groupAccess));

    /// <summary>
    /// FR-137, §4.19: GitLab treats a title starting with <c>Draft:</c>, <c>[Draft]</c> or <c>(Draft)</c> (any case) as a draft.
    /// </summary>
    public static bool IsDraftTitle(string? title) => title is not null && DraftMarker().IsMatch(title);

    /// <summary>FR-137: the merge request's title as opened: <c>Draft: </c> + <paramref name="title"/> (never doubled).</summary>
    public static string DraftTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var ready = ReadyTitle(title);
        return DraftPrefix + ready;
    }

    /// <summary>FR-137: the title with every leading draft marker removed (marking the merge request ready).</summary>
    public static string ReadyTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var text = title;
        while (DraftMarker().Match(text) is { Success: true } match)
        {
            text = text[match.Length..];
        }

        return text.Trim();
    }

    /// <summary>
    /// FR-137: the title a reused merge request gets: the new title, kept a draft while the existing one is a draft (AIDE
    /// never marks it ready by updating its description).
    /// </summary>
    public static string ReusedTitle(string existingTitle, string newTitle) =>
        IsDraftTitle(existingTitle) ? DraftTitle(newTitle) : ReadyTitle(newTitle);

    /// <summary>FR-136: the repository's web URL on the instance: <c>{base}/{group/sub/project}</c>.</summary>
    public static string ProjectWebUrl(Uri instanceBaseUrl, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(instanceBaseUrl);
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        return $"{instanceBaseUrl.AbsoluteUri.TrimEnd('/')}/{fullPath.Trim('/')}";
    }

    /// <summary>
    /// FR-141 (as FR-73): the released version the import picker preselects — the host's latest release, else the highest
    /// release, else the highest semver tag, else "Never released" — and the version to develop it prefills: the next
    /// minor of that version, else the initial version (<c>0.1.0</c>). Refs that aren't semantic versions (after
    /// <paramref name="tagPrefix"/> or a leading <c>v</c>) are ignored.
    /// </summary>
    public static ReleaseRefSuggestion SuggestRelease(IEnumerable<ReleaseRefCandidate> refs, string tagPrefix = "v")
    {
        ArgumentNullException.ThrowIfNull(refs);
        var versioned = refs
            .Select(r => (Ref: r, Version: GitHub.GitHubRules.TagSemver(r.Tag, tagPrefix) is { } text ? SemVer.Parse(text) : null))
            .Where(x => x.Version is not null)
            .ToList();
        var pick = versioned.FirstOrDefault(x => x.Ref.IsRelease && x.Ref.IsLatest);
        if (pick.Ref is null)
        {
            pick = versioned.Where(x => x.Ref.IsRelease).OrderByDescending(x => x.Version).FirstOrDefault();
        }

        if (pick.Ref is null)
        {
            pick = versioned.OrderByDescending(x => x.Version).FirstOrDefault();
        }

        return pick.Ref is null
            ? new ReleaseRefSuggestion(null, SemVer.Parse(ReleaseVersionRules.DefaultInitialVersion))
            : new ReleaseRefSuggestion(pick.Ref.Tag, pick.Version!.NextMinor());
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PathSegment();

    [GeneratedRegex(@"^\s*(draft:|\[draft\]|\(draft\))\s*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DraftMarker();
}
