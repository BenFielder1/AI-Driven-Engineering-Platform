using Aide.Domain.Execution;

namespace Aide.Domain.Stack;

/// <summary>
/// FR-125, §4.18 (the composition equivalent of v2's preset path rules): the Guardian's protected paths are the
/// v1 rules plus the packs' <see cref="StackResolution.ProtectedPaths"/>, with the v2 "owner may declare" exception. Which
/// protected paths a <b>generated</b> plan step may declare:
/// <list type="bullet">
/// <item>a v1-protected CI workflow or <c>.env.example</c> (<see cref="IsOwnableV1Protected"/>), when the
/// step's specialist owns it (the ci-engineer, the deploy-engineer);</item>
/// <item>a path under a pack's own non-<c>existingOnBase</c> entry, when the step's specialist owns it;</item>
/// <item>never: other v1-protected paths (lockfiles, <c>.git</c>, <c>.aide/</c>, <c>.env</c> files), and a file that exists on
/// the base commit under an <c>existingOnBase</c> entry. Migrations on the base commit are immutable for every data pack.</item>
/// </list>
/// </summary>
public static class StackPathRules
{
    /// <summary>
    /// Why a generated plan step assigned <paramref name="agentKey"/> may not declare <paramref name="normalisedPath"/>, or
    /// <c>null</c> when it may. <paramref name="existsOnBase"/>: the file exists on the base commit; <c>null</c> when the
    /// base couldn't be listed, which refuses every path under an <c>existingOnBase</c> entry (it can't be proven new).
    /// </summary>
    public static string? GeneratedPlanRefusal(StackResolution resolution, string? agentKey, string normalisedPath, bool? existsOnBase)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(normalisedPath);
        if (existsOnBase is null && resolution.ProtectedPaths.FirstOrDefault(p => p.ExistingOnBase && p.Protects(normalisedPath, _ => true)) is { } unverifiable)
        {
            return $"declares '{normalisedPath}' under {unverifiable.Glob.Pattern}, but the base commit couldn't be read to prove it is a new file; retry when the repository is available.";
        }

        var exists = existsOnBase ?? false;
        if (ImmutableRefusal(resolution, normalisedPath, exists) is { } immutable)
        {
            return immutable;
        }

        var v1 = PathRules.IsProtected(normalisedPath);
        var packProtected = resolution.IsProtected(normalisedPath, _ => exists);
        if (!v1 && !packProtected)
        {
            return null;
        }

        if (v1 && !IsOwnableV1Protected(normalisedPath))
        {
            return $"declares the protected path '{normalisedPath}'; no plan step may declare .git/, .aide/, lockfiles or .env files other than .env.example.";
        }

        var owners = resolution.Specialists.Where(s => s.Owns.IsMatch(normalisedPath)).Select(s => s.Key).ToList();
        return owners.Contains(agentKey ?? string.Empty, StringComparer.Ordinal)
            ? null
            : $"declares the protected path '{normalisedPath}', which only a specialist that owns it may declare ({(owners.Count == 0 ? "no specialist owns it" : string.Join(", ", owners))}).";
    }

    /// <summary>
    /// The v1-protected paths a specialist that owns them may declare: v2's (GitHub workflows under
    /// <c>.github/workflows/</c>, <c>.env.example</c> in any folder) plus the root CI files of the other VCS packs (<c>.gitlab-ci.yml</c>,
    /// <c>bitbucket-pipelines.yml</c>), which the ci-engineer owns (§4.18).
    /// </summary>
    public static bool IsOwnableV1Protected(string normalisedPath)
    {
        ArgumentNullException.ThrowIfNull(normalisedPath);
        return normalisedPath.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
            || normalisedPath.Split('/')[^1].Equals(".env.example", StringComparison.OrdinalIgnoreCase)
            || normalisedPath.Equals(".gitlab-ci.yml", StringComparison.OrdinalIgnoreCase)
            || normalisedPath.Equals("bitbucket-pipelines.yml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// FR-125 (any plan, Owner edits included, and the Guardian): why <paramref name="normalisedPath"/> may not be written
    /// because it is a file under an <c>existingOnBase</c> entry that exists on the base commit, or <c>null</c>.
    /// </summary>
    public static string? ImmutableRefusal(StackResolution resolution, string normalisedPath, bool existsOnBase)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(normalisedPath);
        return resolution.ProtectedPaths.FirstOrDefault(p => p.ExistingOnBase && p.Protects(normalisedPath, _ => existsOnBase)) is { } immutable
            ? $"declares the protected path '{normalisedPath}', which already exists on the base commit ({immutable.Reason}); add a new file instead."
            : null;
    }

    /// <summary>
    /// §4.11, FR-124 scaffold-first: whether a repository holding only <paramref name="paths"/> counts as empty — no files, or
    /// only what a new repository starts with (README, LICENSE, <c>.gitignore</c>, <c>.gitattributes</c> at the root).
    /// </summary>
    public static bool IsStarterOnly(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.Select(PathRules.Normalize).All(p =>
        {
            if (p.Contains('/', StringComparison.Ordinal))
            {
                return false;
            }

            var name = p.ToLowerInvariant();
            return name is ".gitignore" or ".gitattributes"
                || name.StartsWith("readme", StringComparison.Ordinal)
                || name.StartsWith("license", StringComparison.Ordinal)
                || name.StartsWith("licence", StringComparison.Ordinal);
        });
    }
}
