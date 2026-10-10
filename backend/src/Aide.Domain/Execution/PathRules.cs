using System.Buffers;

namespace Aide.Domain.Execution;

/// <summary>Repository-relative path handling for plans and the Guardian (§4.6, D18: exact matching).</summary>
public static class PathRules
{
    private static readonly HashSet<string> LockFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb", "bun.lock",
        "packages.lock.json", "paket.lock", "Cargo.lock", "poetry.lock", "Pipfile.lock", "uv.lock", "pdm.lock",
        "go.sum", "Gemfile.lock", "composer.lock", "mix.lock", "pubspec.lock", "Podfile.lock", "flake.lock",
        "gradle.lockfile", "conan.lock",
    };

    private static readonly HashSet<string> RootCiFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gitlab-ci.yml", "azure-pipelines.yml", "Jenkinsfile", ".travis.yml", "bitbucket-pipelines.yml", "appveyor.yml",
    };

    /// <summary>Agents never write here, even when a step declares it (§4.6, Decisions: only humans edit project rules).</summary>
    public const string AideDirectory = ".aide/";

    private static readonly HashSet<string> ReservedDeviceNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            .. Enumerable.Range(0, 10).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" }),
            "COM\u00b9", "COM\u00b2", "COM\u00b3", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly SearchValues<char> WindowsForbiddenCharacters = SearchValues.Create(":<>\"|?*");

    private static readonly string[] ProtectedPrefixes =
        [".git/", ".aide/", ".github/workflows/", ".circleci/", ".azure-pipelines/", ".gitea/workflows/", ".forgejo/workflows/"];

    /// <summary>Forward slashes, no leading <c>./</c>, no repeated or trailing slashes, trimmed.</summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var p = path.Trim().Replace('\\', '/');
        while (p.Contains("//", StringComparison.Ordinal))
        {
            p = p.Replace("//", "/", StringComparison.Ordinal);
        }

        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        return p.TrimEnd('/');
    }

    /// <summary>
    /// A normalised path that is empty, absolute, drive-rooted or climbs out with <c>..</c> could escape the
    /// working copy, and any <c>.git</c> segment (any depth, any case, file or folder) could rewrite repository
    /// internals, so such paths are never accepted, even when declared. Control characters (line breaks included) and
    /// backticks (<see cref="HasUnsafeCharacters"/>) are refused too: a path is written into Markdown and prompts.
    /// <para>
    /// Windows aliases are refused as well, because the working copy may live on NTFS: a segment ending in a dot or a
    /// space (Windows drops them, so <c>.git.</c> is <c>.git</c>), an 8.3 short name (<c>GIT~1</c>), an alternate data
    /// stream or drive (<c>:</c> anywhere), a reserved device name (<c>CON</c>, <c>NUL</c>, <c>COM1</c>, ... with or
    /// without an extension) and the other characters Windows forbids in names (<c>&lt; &gt; " | ? *</c>).
    /// </para>
    /// </summary>
    public static bool IsUnsafe(string normalisedPath)
    {
        ArgumentNullException.ThrowIfNull(normalisedPath);
        return normalisedPath.Length == 0
            || HasUnsafeCharacters(normalisedPath)
            || normalisedPath.StartsWith('/')
            || normalisedPath.AsSpan().ContainsAny(WindowsForbiddenCharacters)
            || normalisedPath.Split('/').Any(IsUnsafeSegment);
    }

    /// <summary>
    /// Is <paramref name="segment"/> (one path component) the repository's <c>.git</c> under any name a Windows or
    /// case-insensitive file system resolves to it (any case, trailing dots or spaces)?
    /// </summary>
    public static bool IsGitDirectoryName(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return segment.TrimEnd('.', ' ').Equals(".git", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A path under <c>.aide/</c> (any case): the project's own rules for AIDE, which only humans edit. The Guardian rejects
    /// agent output there even when the step declares it, and plans may not declare it (§4.6, Decisions).
    /// </summary>
    public static bool IsAgentForbidden(string normalisedPath)
    {
        ArgumentNullException.ThrowIfNull(normalisedPath);
        return (normalisedPath + "/").StartsWith(AideDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnsafeSegment(string segment) =>
        segment is ".." or "." or ""
        || IsGitDirectoryName(segment)
        || segment.EndsWith('.')
        || segment.EndsWith(' ')
        || IsShortName(segment)
        || IsReservedDeviceName(segment);

    /// <summary>An 8.3 short-name alias such as <c>GIT~1</c> or <c>GITHUB~2</c>: a <c>~</c> followed by a digit.</summary>
    private static bool IsShortName(string segment)
    {
        for (var i = segment.IndexOf('~', StringComparison.Ordinal); i >= 0 && i < segment.Length - 1; i = segment.IndexOf('~', i + 1))
        {
            if (char.IsAsciiDigit(segment[i + 1]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A Windows device name (<c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM0-9</c>, <c>LPT0-9</c>), with or without an extension.</summary>
    private static bool IsReservedDeviceName(string segment)
    {
        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ');
        return ReservedDeviceNames.Contains(stem);
    }

    /// <summary>
    /// A control character (line breaks, tabs, NUL, …) or a backtick: either could break out of the Markdown a path is
    /// rendered into (a plan's file list, a fenced block) or forge lines in a prompt.
    /// </summary>
    public static bool HasUnsafeCharacters(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Any(c => char.IsControl(c) || c == '`');
    }

    /// <summary>
    /// Protected paths (§4.6 scope-creep): <c>.git/</c>, <c>.aide/</c>, CI workflow files, lockfiles and env files.
    /// Matching ignores case so a case-insensitive checkout can't sneak past.
    /// </summary>
    public static bool IsProtected(string normalisedPath)
    {
        ArgumentNullException.ThrowIfNull(normalisedPath);
        var withSlash = normalisedPath + "/";
        if (ProtectedPrefixes.Any(prefix => withSlash.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var segments = normalisedPath.Split('/');
        if (segments.Any(IsGitDirectoryName))
        {
            return true;
        }

        var fileName = segments[^1];
        if (segments.Length == 1 && RootCiFiles.Contains(fileName))
        {
            return true;
        }

        return LockFileNames.Contains(fileName)
            || fileName.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase);
    }
}
