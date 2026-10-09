using System.Globalization;
using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;

namespace Aide.Domain.Releases;

/// <summary>A release tag of the version being released that already exists, and whether its commit holds the release.</summary>
public sealed record ExistingReleaseTag(string Tag, bool ContainsRelease);

/// <summary>
/// The tag a release uses (FR-42): <see cref="Tag"/> to create (after merging), or, when <see cref="AlreadyReleased"/>, an
/// existing tag that already holds the release (nothing to merge or tag).
/// </summary>
public sealed record ReleaseTagChoice(string Tag, bool AlreadyReleased);

/// <summary>FR-42, FR-43, §4.1, D4: release tags, attempts and when a release may run or be retried. Pure.</summary>
public static partial class ReleaseRules
{
    /// <summary>The annotated tag of a release: <c>v&lt;project version&gt;-aide</c> (FR-42).</summary>
    public static string Tag(int projectVersion)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(projectVersion, 1);
        return string.Create(CultureInfo.InvariantCulture, $"v{projectVersion}-aide");
    }

    /// <summary>
    /// The <paramref name="suffix"/>-th tag of a version: <c>v&lt;N&gt;-aide</c> for 1, else <c>v&lt;N&gt;-aide.&lt;suffix&gt;</c>.
    /// Re-releasing a version number after a rollback (§4.8) uses the next free suffix: tags are never moved.
    /// </summary>
    public static string Tag(int projectVersion, int suffix)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(suffix, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(suffix, MaxTagSuffix);
        return suffix == 1 ? Tag(projectVersion) : string.Create(CultureInfo.InvariantCulture, $"{Tag(projectVersion)}.{suffix}");
    }

    /// <summary>The highest tag suffix used for one version (far beyond any real number of re-releases).</summary>
    public const int MaxTagSuffix = 9999;

    /// <summary>A name <see cref="Tag(int, int)"/> can produce (used to refuse anything else before it reaches git).</summary>
    public static bool IsReleaseTag(string? tag) => tag is not null && TagPattern().IsMatch(tag);

    /// <summary>Whether <paramref name="tag"/> is one of <paramref name="projectVersion"/>'s release tags (base or suffixed).</summary>
    public static bool IsReleaseTagOf(string? tag, int projectVersion) =>
        TryParseTag(tag, out var version, out _) && version == projectVersion;

    /// <summary>Reads a release tag's version and suffix (1 for the base tag).</summary>
    public static bool TryParseTag(string? tag, out int projectVersion, out int suffix)
    {
        projectVersion = 0;
        suffix = 0;
        var match = tag is null ? null : TagPattern().Match(tag);
        if (match is null || !match.Success)
        {
            return false;
        }

        projectVersion = int.Parse(match.Groups["version"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        suffix = match.Groups["suffix"].Success ? int.Parse(match.Groups["suffix"].Value, NumberStyles.None, CultureInfo.InvariantCulture) : 1;
        return true;
    }

    /// <summary>
    /// FR-42 idempotency and the re-release decision: when an existing tag of the version (base or suffixed, lowest suffix
    /// first) already holds the release, it is reused (no merge, no new tag); otherwise the first free tag of the version
    /// (<c>v&lt;N&gt;-aide</c>, then <c>.2</c>, <c>.3</c>…) is created. Tags that aren't the version's release tags are ignored.
    /// </summary>
    public static ReleaseTagChoice ChooseTag(int projectVersion, IEnumerable<ExistingReleaseTag> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var tags = existing
            .Where(t => TryParseTag(t.Tag, out var version, out _) && version == projectVersion)
            .Select(t => (t.Tag, Suffix: Suffix(t.Tag), t.ContainsRelease))
            .OrderBy(t => t.Suffix)
            .ToList();
        if (tags.FirstOrDefault(t => t.ContainsRelease) is { Tag: not null } held)
        {
            return new ReleaseTagChoice(held.Tag, AlreadyReleased: true);
        }

        var taken = tags.Select(t => t.Suffix).ToHashSet();
        for (var suffix = 1; suffix <= MaxTagSuffix; suffix++)
        {
            if (!taken.Contains(suffix))
            {
                return new ReleaseTagChoice(Tag(projectVersion, suffix), AlreadyReleased: false);
            }
        }

        throw new InvalidOperationException($"Every release tag of version {projectVersion} is taken.");
    }

    private static int Suffix(string tag) => TryParseTag(tag, out _, out var suffix) ? suffix : 0;

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

    /// <summary>The annotated tag's message (<paramref name="tag"/> is the tag being created; the base tag by default).</summary>
    public static string TagMessage(string projectName, int projectVersion, string? tag = null) =>
        string.Create(CultureInfo.InvariantCulture, $"AIDE release {ReleaseTagOrBase(tag, projectVersion)}: {Single(projectName)} (project version {projectVersion})");

    /// <summary>The merge commit's subject (Local merge, GitHub merge commit title).</summary>
    public static string MergeTitle(string projectName, int projectVersion, string? tag = null) =>
        string.Create(CultureInfo.InvariantCulture, $"[AIDE] Release {ReleaseTagOrBase(tag, projectVersion)}: {Single(projectName)}");

    private static string ReleaseTagOrBase(string? tag, int projectVersion) =>
        tag is null ? Tag(projectVersion)
        : IsReleaseTagOf(tag, projectVersion) ? tag
        : throw new ArgumentException($"'{tag}' is not a release tag of version {projectVersion}.", nameof(tag));

    private static string Single(string text)
    {
        var line = string.Join(' ', (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length > 100 ? line[..100] : line;
    }

    // v<N>-aide or v<N>-aide.<2..9999>: digits, dots and letters only (safe as a git ref and an argument; no leading '-').
    [GeneratedRegex(@"\Av(?<version>[1-9][0-9]{0,8})-aide(?:\.(?<suffix>[2-9]|[1-9][0-9]{1,3}))?\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex TagPattern();
}
