using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Releases;

namespace Aide.Domain.GitHosts;

/// <summary>What a Bitbucket <c>merge/task-status</c> answer says about an asynchronous merge (FR-140).</summary>
public enum BitbucketMergeTaskState
{
    /// <summary>Still merging (<c>PENDING</c>, or anything Bitbucket hasn't documented): poll again.</summary>
    Pending = 0,

    /// <summary>Merged (<c>SUCCESS</c>): the answer's <c>merge_result</c> is the merged pull request.</summary>
    Succeeded = 1,

    /// <summary>The merge failed (<c>FAILED</c>, <c>ERROR</c>).</summary>
    Failed = 2,
}

/// <summary>
/// FR-135–FR-141, §4.19: the pure rules for a project on Bitbucket Cloud: the <c>git</c> fields of create and import, the
/// repository UUID and <c>workspace/slug</c> forms, the repository permission query, the merge strategy sent to
/// <c>POST …/merge</c>, pull request states for the merge poll, merge task states, commit hash comparison (Bitbucket often
/// answers 12-character short hashes) and the tag's web page.
/// </summary>
public static partial class BitbucketProjectRules
{
    /// <summary>The longest repository UUID accepted (<c>{…}</c>).</summary>
    public const int MaxRepoIdLength = 66;

    /// <summary>The longest <c>workspace/slug</c> accepted.</summary>
    public const int MaxFullNameLength = BitbucketRules.MaxWorkspaceLength + 1 + BitbucketRules.MaxSlugLength;

    /// <summary>The shortest hash prefix compared with a full commit hash (Bitbucket's short hashes are 12).</summary>
    public const int MinHashPrefix = 7;

    /// <summary>
    /// FR-135, FR-136: the <c>git</c> fields of <c>POST /api/projects</c> with <c>mode: bitbucket</c>: the caller's
    /// connection, the repository UUID and its <c>workspace/slug</c> are required; the instance (Bitbucket Cloud is fixed),
    /// a repository URL, a token and a GitHub installation don't apply. Field names get <paramref name="prefix"/>.
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
        if (instanceId is not null)
        {
            problems.Add($"{prefix}instanceId: Not used with bitbucket: Bitbucket Cloud is the only instance.");
        }

        if (connectionId is null || connectionId == Guid.Empty)
        {
            problems.Add($"{prefix}connectionId: Pick one of your Bitbucket connections.");
        }

        if (!IsValidRepoId(repoId))
        {
            problems.Add($"{prefix}repoId: Pick the repository from the Bitbucket repo picker (its UUID).");
        }

        if (!IsValidFullName(repoFullName))
        {
            problems.Add($"{prefix}repoFullName: Use the repository's workspace/slug, e.g. acme/todo-app.");
        }

        if (!string.IsNullOrEmpty(repoUrl))
        {
            problems.Add($"{prefix}repoUrl: Not used with bitbucket; send repoId and repoFullName.");
        }

        if (!string.IsNullOrEmpty(token))
        {
            problems.Add($"{prefix}token: Not used with bitbucket: AIDE uses your Bitbucket connection.");
        }

        if (installationId is not null)
        {
            problems.Add($"{prefix}installationId: Only used with github_app.");
        }

        return problems.Count == 0
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Validation, "The Git settings are invalid.") { Details = problems });
    }

    /// <summary>
    /// The host-bound fields aren't sent for a mode that doesn't use them (<c>VALIDATION</c>): the instance only with
    /// gitlab; the connection and repo id only with gitlab or bitbucket.
    /// </summary>
    public static Result ValidateNotSent(GitProvider mode, Guid? instanceId, Guid? connectionId, string? repoId, string prefix = "")
    {
        if (mode == GitProvider.Gitlab || mode == GitProvider.Bitbucket)
        {
            return Result.Success();
        }

        var problems = new List<string>();
        if (instanceId is not null)
        {
            problems.Add($"{prefix}instanceId: Only used with gitlab.");
        }

        if (connectionId is not null)
        {
            problems.Add($"{prefix}connectionId: Only used with gitlab or bitbucket.");
        }

        if (repoId is not null)
        {
            problems.Add($"{prefix}repoId: Only used with gitlab or bitbucket.");
        }

        return problems.Count == 0
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Validation, "The Git settings are invalid.") { Details = problems });
    }

    /// <summary>A Bitbucket repository UUID as Bitbucket sends it: <c>{…}</c> with letters, digits and dashes inside.</summary>
    public static bool IsValidRepoId(string? repoId) =>
        repoId is { Length: > 2 and <= MaxRepoIdLength } && RepoUuid().IsMatch(repoId);

    /// <summary>A repository's <c>workspace/slug</c>: a workspace slug and a repository slug (<see cref="BitbucketRules.IsValidSlug"/>).</summary>
    public static bool IsValidFullName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > MaxFullNameLength)
        {
            return false;
        }

        var parts = fullName.Split('/');
        return parts.Length == 2 && Workspace().IsMatch(parts[0]) && parts[0].Length <= BitbucketRules.MaxWorkspaceLength && BitbucketRules.IsValidSlug(parts[1]);
    }

    /// <summary>The workspace and slug of a valid <c>workspace/slug</c>.</summary>
    public static (string Workspace, string Slug) SplitFullName(string fullName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullName);
        var slash = fullName.IndexOf('/', StringComparison.Ordinal);
        return slash <= 0 ? (fullName, string.Empty) : (fullName[..slash], fullName[(slash + 1)..]);
    }

    /// <summary>
    /// FR-136: the BBQL filter of <c>GET /user/permissions/repositories</c> for one repository:
    /// <c>repository.uuid="{…}"</c>, with <c>\</c> and <c>"</c> escaped.
    /// </summary>
    public static string PermissionQuery(string repoUuid)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoUuid);
        var escaped = repoUuid.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"repository.uuid=\"{escaped}\"";
    }

    /// <summary>FR-137: the BBQL filter for the open pull request from <paramref name="sourceBranch"/>.</summary>
    public static string SourceBranchQuery(string sourceBranch)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceBranch);
        var escaped = sourceBranch.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"source.branch.name=\"{escaped}\"";
    }

    /// <summary>
    /// FR-140, §4.19: Bitbucket's <c>merge_strategy</c> for the project's merge strategy: <c>squash</c> and
    /// <c>fast_forward</c> as chosen; <c>aide_merges</c> (and <c>manual</c>, which never merges) <c>merge_commit</c>.
    /// </summary>
    public static string MergeStrategyParameter(MergeStrategy strategy) => strategy switch
    {
        MergeStrategy.Squash => "squash",
        MergeStrategy.FastForward => "fast_forward",
        _ => "merge_commit",
    };

    /// <summary>
    /// FR-140: the merge strategies only Bitbucket offers (<c>fast_forward</c>, <c>squash</c>) are refused for another Git
    /// provider (<c>VALIDATION</c> on <c>mergeStrategy</c>): GitLab merges with its project's merge method and squash option,
    /// GitHub with the repository's allowed methods.
    /// </summary>
    public static Result ValidateMergeStrategy(GitProvider provider, MergeStrategy strategy)
    {
        if (strategy is not (MergeStrategy.FastForward or MergeStrategy.Squash) || provider == GitProvider.Bitbucket)
        {
            return Result.Success();
        }

        return Result.Failure(new DomainError(ErrorCodes.Validation, "The Git settings are invalid.")
        {
            Details = [$"mergeStrategy: {strategy.ToWire()} is only available for Bitbucket projects; use aide_merges or manual."],
        });
    }

    /// <summary>
    /// FR-140, FR-65: what a pull request's <c>state</c> means for the merge poll: <c>MERGED</c> → merged; <c>DECLINED</c>
    /// and <c>SUPERSEDED</c> → closed without merging (<c>PR_CLOSED</c>); <c>OPEN</c> or unknown → still open.
    /// </summary>
    public static MergePollObservation ObservationOf(string? state) => state switch
    {
        "MERGED" => MergePollObservation.Merged,
        "DECLINED" or "SUPERSEDED" => MergePollObservation.ClosedUnmerged,
        _ => MergePollObservation.Open,
    };

    /// <summary>FR-140: a <c>merge/task-status</c> answer's <c>task_status</c>.</summary>
    public static BitbucketMergeTaskState TaskStateOf(string? taskStatus) => taskStatus?.ToUpperInvariant() switch
    {
        "SUCCESS" or "SUCCESSFUL" or "COMPLETED" => BitbucketMergeTaskState.Succeeded,
        "FAILED" or "FAILURE" or "ERROR" => BitbucketMergeTaskState.Failed,
        _ => BitbucketMergeTaskState.Pending,
    };

    /// <summary>
    /// Whether two commit hashes name the same commit: equal, or one a prefix of the other when the shorter has at least
    /// <see cref="MinHashPrefix"/> characters (Bitbucket's <c>merge_commit.hash</c> and <c>source.commit.hash</c> are
    /// often 12-character short hashes). Case-insensitive; blank never matches.
    /// </summary>
    public static bool SameCommit(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var (shorter, longer) = left.Length <= right.Length ? (left.Trim(), right.Trim()) : (right.Trim(), left.Trim());
        return shorter.Length == longer.Length
            ? string.Equals(shorter, longer, StringComparison.OrdinalIgnoreCase)
            : shorter.Length >= MinHashPrefix && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A full (40 or 64 hex character) commit hash.</summary>
    public static bool IsFullHash(string? hash) =>
        hash is { Length: 40 or 64 } && hash.All(char.IsAsciiHexDigit);

    /// <summary>
    /// FR-140: the annotated tag's web page, used as the release URL (Bitbucket has no Releases, D-V4-7): the tag's
    /// <c>links.html.href</c> when it is an absolute http(s) URL of at most 500 characters, else
    /// <c>&lt;repository web URL&gt;/commits/tag/&lt;tag&gt;</c>; <c>null</c> when neither is known.
    /// </summary>
    public static string? TagUrl(string? htmlLink, string? repoWebUrl, string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        if (IsWebUrl(htmlLink))
        {
            return htmlLink;
        }

        return IsWebUrl(repoWebUrl) ? $"{repoWebUrl!.TrimEnd('/')}/commits/tag/{Uri.EscapeDataString(tag)}" : null;
    }

    /// <summary>FR-135: the web URL of <c>workspace/slug</c> on bitbucket.org.</summary>
    public static string RepoWebUrl(string fullName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullName);
        var (workspace, slug) = SplitFullName(fullName);
        return $"{BitbucketRules.WebBaseUrl}/{Uri.EscapeDataString(workspace)}/{Uri.EscapeDataString(slug)}";
    }

    private static bool IsWebUrl(string? url) =>
        url is { Length: <= 500 }
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    [GeneratedRegex(@"^\{[A-Za-z0-9\-]{1,64}\}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoUuid();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Workspace();
}
