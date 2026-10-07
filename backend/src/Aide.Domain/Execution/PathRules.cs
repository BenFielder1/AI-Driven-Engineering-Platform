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
    /// internals, so such paths are never accepted, even when declared.
    /// </summary>
    public static bool IsUnsafe(string normalisedPath)
    {
        ArgumentNullException.ThrowIfNull(normalisedPath);
        return normalisedPath.Length == 0
            || normalisedPath.StartsWith('/')
            || (normalisedPath.Length >= 2 && normalisedPath[1] == ':')
            || normalisedPath.Split('/').Any(segment => segment is ".." or "." or "" || segment.Equals(".git", StringComparison.OrdinalIgnoreCase));
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
        if (segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
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
