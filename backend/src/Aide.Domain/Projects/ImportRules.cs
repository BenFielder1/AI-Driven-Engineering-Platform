using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Domain.Releases;

namespace Aide.Domain.Projects;

/// <summary>FR-73: where a new project's code comes from (wizard step "Source").</summary>
public enum ProjectSource
{
    /// <summary>Start from scratch (v1 flow plus FR-66's initial version).</summary>
    Scratch = 0,

    /// <summary>Import an existing repository with its released version (or "Never released") and a version to develop.</summary>
    Import = 1,
}

/// <summary>
/// FR-73 versions of an import: the released tag (<c>null</c> = "Never released") with its semver, and the version to
/// develop.
/// </summary>
public sealed record ImportVersions(string? ReleasedTag, SemVer? ReleasedVersion, SemVer DevelopmentVersion);

/// <summary>
/// FR-73: rules for importing an existing repository.
/// <list type="bullet">
/// <item><see cref="ParseVersions"/> (no repository access): <c>developmentVersion</c> is semver (<c>VERSION_INVALID</c>);
/// the released tag, when sent, names a semver version after the project's tag prefix (default <c>v</c>) or a leading
/// <c>v</c> (<c>VERSION_INVALID</c> on <c>releasedTag</c>); the version to develop is strictly greater
/// (<c>VERSION_NOT_GREATER</c>).</item>
/// <item><see cref="CheckAgainstRepository"/> (with the repository's tag names, e.g. from the refs endpoint): the released
/// tag exists (<c>TAG_NOT_FOUND</c>, 400) and the development version's tag doesn't yet (<c>TAG_EXISTS</c>, 409 — the
/// release would fail on it, FR-64).</item>
/// <item><see cref="ResolveLocalSource"/>: the Development/E2E-only Local import (§8 Testing) copies a directory that must
/// resolve under the configured root <c>Git:Local:ImportRoot</c>.</item>
/// </list>
/// </summary>
public static class ImportRules
{
    /// <summary>The longest tag name accepted (Git allows more; no real release tag is this long).</summary>
    public const int TagMaxLength = 200;

    /// <summary>
    /// FR-73 request shape (<c>VALIDATION</c>, every problem): <c>import</c> is required with <c>source: import</c> and not
    /// allowed with <c>scratch</c>; <c>initialVersion</c> is for <c>scratch</c> only; <c>git.localSourcePath</c> only with
    /// <c>import</c> and <c>git.mode: local</c>.
    /// </summary>
    public static Result ValidateSource(ProjectSource source, bool importSent, bool initialVersionSent, bool localSourceSent, GitProvider mode)
    {
        var problems = new List<string>();
        if (source == ProjectSource.Import)
        {
            if (!importSent)
            {
                problems.Add("import: Required when importing; send the released tag (or null) and the version to develop.");
            }

            if (initialVersionSent)
            {
                problems.Add("initialVersion: Not used when importing; send import.developmentVersion.");
            }

            if (localSourceSent && mode != GitProvider.Local)
            {
                problems.Add("git.localSourcePath: Only used with git.mode local.");
            }
        }
        else
        {
            if (importSent)
            {
                problems.Add("import: Only used with source import.");
            }

            if (localSourceSent)
            {
                problems.Add("git.localSourcePath: Only used with source import.");
            }
        }

        return ProjectRules.ToResult(problems, "The project source is invalid.");
    }

    public static Result<ImportVersions> ParseVersions(string? releasedTag, string? developmentVersion, string tagPrefix = "v")
    {
        ArgumentNullException.ThrowIfNull(tagPrefix);
        var development = ReleaseVersionRules.Parse(developmentVersion, "import.developmentVersion");
        if (development.IsFailure)
        {
            return Result.Failure<ImportVersions>(development.Error!);
        }

        if (releasedTag is null)
        {
            return Result.Success(new ImportVersions(null, null, development.Value));
        }

        var tag = releasedTag.Trim();
        var releasedText = tag.Length is > 0 and <= TagMaxLength ? GitHubRules.TagSemver(tag, tagPrefix) : null;
        if (releasedText is null)
        {
            return Result.Failure<ImportVersions>(new DomainError(
                ErrorCodes.VersionInvalid,
                $"The released tag '{Shorten(tag)}' doesn't name a version (MAJOR.MINOR.PATCH[-prerelease], optionally after '{tagPrefix}'). Pick a semver tag or \"Never released\".")
            {
                Details = ["import.releasedTag: Pick a tag whose name is a semantic version, e.g. v2.3.0."],
            });
        }

        var released = SemVer.Parse(releasedText);
        if (development.Value <= released)
        {
            return Result.Failure<ImportVersions>(new DomainError(
                ErrorCodes.VersionNotGreater,
                $"Version {development.Value} must be greater than the released version {released} ({tag}).")
            {
                Details = [$"import.developmentVersion: Use a version greater than {released}, e.g. {released.NextMinor()}."],
            });
        }

        return Result.Success(new ImportVersions(tag, released, development.Value));
    }

    /// <summary>
    /// The repository checks of an import, given its tag names: <c>TAG_NOT_FOUND</c> when the released tag isn't one of
    /// them (ordinal), <c>TAG_EXISTS</c> when the development version's tag (<paramref name="tagPrefix"/> + version) already is.
    /// </summary>
    public static Result CheckAgainstRepository(ImportVersions versions, IReadOnlyCollection<string> repositoryTags, string tagPrefix = "v")
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(repositoryTags);
        var tags = repositoryTags.ToHashSet(StringComparer.Ordinal);
        if (versions.ReleasedTag is { } released && !tags.Contains(released))
        {
            return Result.Failure(new DomainError(
                ErrorCodes.TagNotFound,
                $"The tag '{Shorten(released)}' doesn't exist in the repository. Pick one of its releases or tags, or \"Never released\".")
            {
                Details = ["import.releasedTag: The tag doesn't exist in the repository."],
            });
        }

        var developmentTag = ReleaseVersionRules.Tag(tagPrefix, versions.DevelopmentVersion);
        return tags.Contains(developmentTag)
            ? Result.Failure(new DomainError(
                ErrorCodes.TagExists,
                $"The tag {developmentTag} already exists in the repository; pick a version greater than every released one.")
            {
                Details = ["import.developmentVersion: Its tag already exists in the repository."],
            })
            : Result.Success();
    }

    /// <summary>
    /// The full path of a Local import source: <paramref name="sourcePath"/> (absolute, or relative to
    /// <paramref name="importRoot"/>) must resolve to <paramref name="importRoot"/> or a directory under it. No root
    /// configured (outside Development/E2E) or a path outside it is <c>VALIDATION</c> on <c>git.localSourcePath</c>.
    /// Whether the directory exists and is a Git repository is checked by the caller.
    /// </summary>
    public static Result<string> ResolveLocalSource(string? importRoot, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(importRoot))
        {
            return LocalSourceInvalid("Importing a local directory isn't enabled on this server (Git:Local:ImportRoot is not set).");
        }

        if (string.IsNullOrWhiteSpace(sourcePath) || sourcePath.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            return LocalSourceInvalid("Send the directory to import.");
        }

        // Symbolic links and junctions are followed on both sides: the real directory must be under the real root.
        var root = RealPath(Path.GetFullPath(importRoot));
        var full = RealPath(Path.GetFullPath(sourcePath.Trim(), root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var inside = string.Equals(full, root, comparison)
            || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || full.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
        return inside ? Result.Success(full) : LocalSourceInvalid("The directory must be inside the configured import root.");
    }

    /// <summary>
    /// <paramref name="fullPath"/> with every existing symbolic link or junction along it replaced by its final target
    /// (like <c>realpath</c>), without a trailing separator. Parts that don't exist are kept as they are.
    /// </summary>
    internal static string RealPath(string fullPath)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var current = root;
        foreach (var part in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (entry.Exists && entry.LinkTarget is not null && entry.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A link that can't be resolved is left as it is; the caller's existence and repository checks fail on it.
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static Result<string> LocalSourceInvalid(string message) =>
        Result.Failure<string>(new DomainError(ErrorCodes.Validation, message) { Details = [$"git.localSourcePath: {message}"] });

    private static string Shorten(string text) => text.Length > 64 ? text[..64] + "…" : text;
}
