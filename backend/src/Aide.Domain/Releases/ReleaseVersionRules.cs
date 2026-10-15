using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Domain.Projects;

namespace Aide.Domain.Releases;

/// <summary>A release version as the rules see it: its version text and status (§4.10).</summary>
public sealed record ReleaseVersionRow(string Version, ReleaseVersionStatus Status);

/// <summary>
/// §4.10, FR-60–FR-62, FR-66, D-V2-2: release version statuses, the released and development versions of a project,
/// starting a new version, version validation, release tags and per-version branches. Pure.
/// </summary>
public static partial class ReleaseVersionRules
{
    /// <summary>FR-66: the initial version of a new project when none is given.</summary>
    public const string DefaultInitialVersion = "0.1.0";

    /// <summary>The longest tag prefix (§4.10).</summary>
    public const int TagPrefixMaxLength = 20;

    /// <summary>
    /// Allowed status changes (§4.10): <c>in_development → awaiting_merge → released</c> and <c>in_development → released</c>.
    /// <c>imported</c> is only ever a starting status (a baseline); <c>released</c> and <c>imported</c> are final.
    /// </summary>
    public static bool CanTransition(ReleaseVersionStatus from, ReleaseVersionStatus to) => (from, to) switch
    {
        (ReleaseVersionStatus.InDevelopment, ReleaseVersionStatus.AwaitingMerge) => true,
        (ReleaseVersionStatus.InDevelopment, ReleaseVersionStatus.Released) => true,
        (ReleaseVersionStatus.AwaitingMerge, ReleaseVersionStatus.Released) => true,
        _ => false,
    };

    /// <summary>A version in development: <c>in_development</c> or <c>awaiting_merge</c> (at most one per project).</summary>
    public static bool IsDevelopment(ReleaseVersionStatus status) =>
        status is ReleaseVersionStatus.InDevelopment or ReleaseVersionStatus.AwaitingMerge;

    /// <summary>A version that counts as released: <c>released</c> or <c>imported</c>.</summary>
    public static bool IsReleased(ReleaseVersionStatus status) =>
        status is ReleaseVersionStatus.Released or ReleaseVersionStatus.Imported;

    /// <summary>
    /// The project's released version: the highest <c>released</c> or <c>imported</c> version by semver precedence (not by
    /// creation order); <c>null</c> when none. Rows whose text isn't semver are ignored.
    /// </summary>
    public static SemVer? ReleasedVersion(IEnumerable<ReleaseVersionRow> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        return versions
            .Where(v => IsReleased(v.Status))
            .Select(v => SemVer.TryParse(v.Version, out var parsed) ? parsed : null)
            .OfType<SemVer>()
            .Max();
    }

    /// <summary>The project's development version: the one <c>in_development</c> or <c>awaiting_merge</c>; <c>null</c> when none.</summary>
    public static ReleaseVersionRow? DevelopmentVersion(IEnumerable<ReleaseVersionRow> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        return versions.FirstOrDefault(v => IsDevelopment(v.Status));
    }

    /// <summary>FR-61: parses a version (a leading <c>v</c> is stripped); invalid text is <c>VERSION_INVALID</c>.</summary>
    public static Result<SemVer> Parse(string? text, string field = "version") =>
        SemVer.TryParse(text, out var version)
            ? Result.Success(version)
            : Result.Failure<SemVer>(new DomainError(
                ErrorCodes.VersionInvalid,
                $"'{Shorten(text)}' is not a valid version. Use MAJOR.MINOR.PATCH with an optional -prerelease, e.g. 1.2.0 or 2.0.0-beta.1.")
            {
                Details = [$"{field}: Use MAJOR.MINOR.PATCH[-prerelease], without leading zeros or build metadata."],
            });

    /// <summary>
    /// FR-60, FR-61: may <paramref name="requested"/> be started now? Checks, in order: <c>VERSION_INVALID</c>;
    /// <c>VERSION_IN_PROGRESS</c> (a version is <c>in_development</c>); <c>RELEASE_NOT_MERGED</c> (the latest release is
    /// <c>awaiting_merge</c>); <c>NOT_RELEASED</c> (the project isn't <c>released</c>); <c>VERSION_NOT_GREATER</c> (not strictly
    /// greater than the released version, or the version already exists). Whether the tag exists in the repository
    /// (<c>TAG_EXISTS</c>) is checked by the caller afterwards, through the Git provider.
    /// </summary>
    public static Result<SemVer> CanStart(ProjectStatus status, IReadOnlyCollection<ReleaseVersionRow> versions, string? requested)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var parsed = Parse(requested);
        if (parsed.IsFailure)
        {
            return parsed;
        }

        var version = parsed.Value;
        if (DevelopmentVersion(versions) is { } development)
        {
            return development.Status == ReleaseVersionStatus.AwaitingMerge
                ? Fail(ErrorCodes.ReleaseNotMerged, $"Version {development.Version} is waiting for its pull request to be merged. Merge it first, then start a new version.")
                : Fail(ErrorCodes.VersionInProgress, $"Version {development.Version} is already in development.");
        }

        if (status != ProjectStatus.Released)
        {
            return Fail(ErrorCodes.NotReleased, $"The project is {status.ToWire()}, not released. Release the version in development first.");
        }

        if (ReleasedVersion(versions) is { } released && version <= released)
        {
            return Fail(ErrorCodes.VersionNotGreater, $"Version {version} must be greater than the released version {released}.");
        }

        if (versions.Any(v => SemVer.TryParse(v.Version, out var existing) && existing == version))
        {
            return Fail(ErrorCodes.VersionNotGreater, $"Version {version} already exists in this project.");
        }

        return Result.Success(version);
    }

    /// <summary>The prefill of "Start new version" (FR-60): the next minor of the released version, or the initial version.</summary>
    public static SemVer SuggestNext(IEnumerable<ReleaseVersionRow> versions) =>
        ReleasedVersion(versions)?.NextMinor() ?? SemVer.Parse(DefaultInitialVersion);

    /// <summary>§4.10: a tag prefix is <c>^[A-Za-z0-9._/-]{0,20}$</c> and keeps the tag a safe Git ref name (no leading <c>-</c>, <c>.</c> or <c>/</c>, no <c>..</c>, <c>//</c>, <c>/.</c> or <c>.lock/</c>).</summary>
    public static bool IsValidTagPrefix(string? prefix) =>
        prefix is not null
        && PrefixPattern().IsMatch(prefix)
        && (prefix.Length == 0 || prefix[0] is not ('-' or '.' or '/'))
        && !prefix.Contains("..", StringComparison.Ordinal)
        && !prefix.Contains("//", StringComparison.Ordinal)
        && !prefix.Contains("/.", StringComparison.Ordinal)
        && !prefix.Contains(".lock/", StringComparison.Ordinal);

    /// <summary>§4.10, FR-64: the release tag <c>&lt;tagPrefix&gt;&lt;version&gt;</c>, e.g. <c>v1.2.0</c>.</summary>
    public static string Tag(string tagPrefix, SemVer version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (!IsValidTagPrefix(tagPrefix))
        {
            throw new ArgumentException($"'{tagPrefix}' is not a valid tag prefix.", nameof(tagPrefix));
        }

        return tagPrefix + version;
    }

    /// <summary>
    /// The working branch of a release version: <c>aide/&lt;slug&gt;/v&lt;version&gt;</c> (FR-60); a slug that doesn't make a valid
    /// branch name falls back to <c>aide/project-&lt;short-id&gt;/v&lt;version&gt;</c>.
    /// </summary>
    public static string BranchName(string slug, Guid projectId, SemVer version)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(version);
        var candidate = $"aide/{slug.Trim()}/v{version}";
        return slug.Trim().Length > 0 && ProjectSettingsRules.IsValidBranchName(candidate)
            ? candidate
            : $"aide/{ProjectRules.SlugFallback}-{projectId.ToString("N")[..8]}/v{version}";
    }

    private static Result<SemVer> Fail(string code, string message) => Result.Failure<SemVer>(new DomainError(code, message));

    private static string Shorten(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length > 40 ? value[..40] + "…" : value;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._/-]{0,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();
}
