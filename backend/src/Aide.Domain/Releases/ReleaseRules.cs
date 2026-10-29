using System.Globalization;
using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;

namespace Aide.Domain.Releases;

/// <summary>The release tag found in the repository, and whether its commit already holds the release (contains the branch).</summary>
public sealed record ExistingReleaseTag(string Tag, bool ContainsRelease);

/// <summary>
/// FR-42 amended by FR-64, FR-43, §4.1, §4.10, D4, D-V2-2: release tags, attempts and when a release may run or be retried.
/// Pure.
/// </summary>
public static partial class ReleaseRules
{
    /// <summary>The longest tag AIDE creates (a 20-character prefix plus a 64-character version).</summary>
    public const int MaxTagLength = 100;

    /// <summary>
    /// FR-140: how long the release job keeps re-checking a merge the host accepted but is still performing (a Bitbucket
    /// merge task) before handing it to the merge poll (<c>awaiting_merge</c>, with its own 14-day ceiling).
    /// </summary>
    public static readonly TimeSpan SubmittedMergeCeiling = TimeSpan.FromHours(1);

    /// <summary>
    /// FR-140: whether the release job keeps waiting (deferring, no attempt counted) for a merge submitted at
    /// <paramref name="submittedAt"/>: until it is <see cref="SubmittedMergeCeiling"/> old. An unknown submission time waits
    /// (the first deferral right after submitting).
    /// </summary>
    public static bool KeepWaitingForSubmittedMerge(DateTimeOffset? submittedAt, DateTimeOffset now) =>
        submittedAt is not { } since || now - since < SubmittedMergeCeiling;

    /// <summary>
    /// A tag name AIDE may create or push: letters, digits and <c>. _ / + -</c>, a valid Git ref (no <c>..</c>, <c>//</c>,
    /// <c>/.</c>, <c>@{</c>, no trailing <c>.</c>, <c>/</c> or <c>.lock</c>) that can't be read as an option (no leading <c>-</c>).
    /// Used to refuse anything else before it reaches git.
    /// </summary>
    public static bool IsReleaseTag(string? tag) =>
        tag is { Length: > 0 and <= MaxTagLength }
        && TagPattern().IsMatch(tag)
        && tag[0] is not ('-' or '.' or '/')
        && !tag.Contains("..", StringComparison.Ordinal)
        && !tag.Contains("//", StringComparison.Ordinal)
        && !tag.Contains("/.", StringComparison.Ordinal)
        && !tag.EndsWith('.')
        && !tag.EndsWith('/')
        && !tag.EndsWith(".lock", StringComparison.Ordinal);

    /// <summary>
    /// FR-64 (amends FR-42): the release tag <c>&lt;tagPrefix&gt;&lt;version&gt;</c> is created on the merge commit when it doesn't
    /// exist; an existing tag that already holds the release is reused (idempotent, nothing merged or tagged:
    /// <c>true</c>); a tag on another commit fails the release with <c>TAG_EXISTS</c> before anything is merged. Tags are
    /// never moved, and v1's <c>v&lt;N&gt;-aide.&lt;k&gt;</c> suffixes are gone.
    /// </summary>
    public static Result<bool> DecideTag(string tag, ExistingReleaseTag? existing)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        if (existing is null)
        {
            return Result.Success(false);
        }

        return existing.ContainsRelease
            ? Result.Success(true)
            : Result.Failure<bool>(new DomainError(
                ErrorCodes.TagExists,
                $"The tag {tag} already exists on another commit; tags are never moved. Delete it in the repository or start a different version."));
    }

    /// <summary>The attempt number of the next release of a project version: one more than the attempts so far (FR-43).</summary>
    public static int NextAttempt(IEnumerable<int> attemptsAtVersion)
    {
        ArgumentNullException.ThrowIfNull(attemptsAtVersion);
        return attemptsAtVersion.DefaultIfEmpty(0).Max() + 1;
    }

    /// <summary>
    /// The release job runs only while the project is in <c>release</c> with status <c>releasing</c> at the version it was
    /// queued for; otherwise (released already, rolled back, superseded) it does nothing.
    /// </summary>
    public static bool ShouldRun(LifecycleState state, int queuedAtVersion)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Phase == Phase.Release && state.Status == ProjectStatus.Releasing && state.Version == queuedAtVersion;
    }

    /// <summary>
    /// FR-43 <c>POST /release</c>: a retry is allowed only in <c>release</c>/<c>releasing</c> (<c>NOT_RELEASING</c>), with no
    /// release job queued or running and no attempt in progress (<c>RELEASE_IN_PROGRESS</c>), after the latest attempt at
    /// this version failed. With no attempt recorded at all (the job was lost) a retry starts the first one.
    /// </summary>
    public static Result CanRetry(LifecycleState state, ReleaseStatus? latestAttempt, bool releaseJobActive)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Phase != Phase.Release || state.Status != ProjectStatus.Releasing)
        {
            return Result.Failure(new DomainError(
                ErrorCodes.NotReleasing,
                state.Status == ProjectStatus.Released
                    ? "The project is already released."
                    : $"The project is in {state.Phase.ToWire()} with status {state.Status.ToWire()}, not releasing."));
        }

        if (releaseJobActive || latestAttempt == ReleaseStatus.InProgress)
        {
            return Result.Failure(new DomainError(ErrorCodes.ReleaseInProgress, "A release is already queued or running."));
        }

        if (latestAttempt == ReleaseStatus.Succeeded)
        {
            return Result.Failure(new DomainError(ErrorCodes.NotReleasing, "The latest release succeeded; there is nothing to retry."));
        }

        return Result.Success();
    }

    /// <summary>The annotated tag's message, e.g. <c>AIDE release v1.2.0: Todo API (version 1.2.0)</c>.</summary>
    public static string TagMessage(string projectName, string tag, string version) =>
        string.Create(CultureInfo.InvariantCulture, $"AIDE release {RequireTag(tag)}: {Single(projectName)} (version {version})");

    /// <summary>The merge commit's subject (Local merge, GitHub merge commit title).</summary>
    public static string MergeTitle(string projectName, string tag) =>
        string.Create(CultureInfo.InvariantCulture, $"[AIDE] Release {RequireTag(tag)}: {Single(projectName)}");

    private static string RequireTag(string tag) =>
        IsReleaseTag(tag) ? tag : throw new ArgumentException($"'{tag}' is not a valid release tag.", nameof(tag));

    private static string Single(string text)
    {
        var line = string.Join(' ', (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length > 100 ? line[..100] : line;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._/+-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
}
