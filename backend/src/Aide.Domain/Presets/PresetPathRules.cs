using Aide.Domain.Execution;

namespace Aide.Domain.Presets;

/// <summary>
/// §4.11, Decisions 2026-10-17 (protected-path ownership exception): which protected paths a <b>generated</b> plan step may
/// declare (an Owner-edited plan may declare any of them, v1 §4.6). Without a preset: none (v1). With a preset:
/// <list type="bullet">
/// <item>a v1-protected CI workflow (<c>.github/workflows/**</c>) or <c>.env.example</c>, when the step's agent owns the path
/// (e.g. <c>devops-engineer</c>, <c>project-scaffolder</c>; §4.14 "Workflows RW when a step declares it");</item>
/// <item>a path protected by the preset's own <c>protectedPaths</c>, when the step's agent owns it and the entry isn't an
/// <c>existingOnBase</c> one (<c>lib/database.types.ts</c>, which <c>db-engineer</c> writes in the generator's shape);</item>
/// <item>never: lockfiles, <c>.git</c>, <c>.aide/</c>, other CI systems, <c>.env</c> files other than <c>.env.example</c>, and
/// files that already exist on the base commit under an <c>existingOnBase</c> entry (committed migrations are immutable).</item>
/// </list>
/// </summary>
public static class PresetPathRules
{
    /// <summary>Whether <paramref name="normalisedPath"/> is one of the v1-protected paths a preset specialist may own.</summary>
    public static bool IsOwnableV1Protected(string normalisedPath)
    {
        ArgumentNullException.ThrowIfNull(normalisedPath);
        return normalisedPath.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
            || normalisedPath.Split('/')[^1].Equals(".env.example", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why a generated plan step assigned <paramref name="agentKey"/> may not declare <paramref name="normalisedPath"/>, or
    /// <c>null</c> when it may. <paramref name="existsOnBase"/>: the file exists on the version's base commit; <c>null</c>
    /// when the base couldn't be listed, which refuses every path under an <c>existingOnBase</c> entry (it can't be proven new).
    /// </summary>
    public static string? GeneratedPlanRefusal(Preset? preset, string? agentKey, string normalisedPath, bool? existsOnBase)
    {
        ArgumentNullException.ThrowIfNull(normalisedPath);
        if (existsOnBase is null && preset?.ProtectedPaths.FirstOrDefault(p => p.ExistingOnBase && p.Protects(normalisedPath, _ => true)) is { } unverifiable)
        {
            return $"declares '{normalisedPath}' under {unverifiable.Glob.Pattern}, but the base commit couldn't be read to prove it is a new file; retry when the repository is available.";
        }

        var exists = existsOnBase ?? false;
        var v1 = PathRules.IsProtected(normalisedPath);
        var presetEntries = preset?.ProtectedPaths.Where(p => p.Protects(normalisedPath, _ => exists)).ToList() ?? [];
        if (!v1 && presetEntries.Count == 0)
        {
            return null;
        }

        if (preset is null)
        {
            return $"declares the protected path '{normalisedPath}'; a generated plan may not create or modify protected paths (.git/, .aide/, CI workflows, lockfiles, .env files).";
        }

        if (presetEntries.FirstOrDefault(p => p.ExistingOnBase) is { } immutable)
        {
            return $"declares the protected path '{normalisedPath}', which already exists on the base commit ({immutable.Reason ?? $"{immutable.Glob.Pattern} files are immutable"}); add a new file instead.";
        }

        if (v1 && !IsOwnableV1Protected(normalisedPath))
        {
            return $"declares the protected path '{normalisedPath}'; no plan step may declare .git/, .aide/, lockfiles or .env files other than .env.example.";
        }

        var owner = preset.Agent(agentKey) is { } agent && agent.Owns.IsMatch(normalisedPath);
        return owner
            ? null
            : $"declares the protected path '{normalisedPath}', which only an agent that owns it may declare ({OwnersOf(preset, normalisedPath)}).";
    }

    /// <summary>
    /// §4.11 (any plan, Owner edits included): why <paramref name="normalisedPath"/> may not be declared because it is an
    /// immutable file (<c>existingOnBase</c> entry) that exists on the base commit, or <c>null</c>.
    /// </summary>
    public static string? ImmutableRefusal(Preset? preset, string normalisedPath, bool existsOnBase)
    {
        ArgumentNullException.ThrowIfNull(normalisedPath);
        return preset?.ProtectedPaths.FirstOrDefault(p => p.ExistingOnBase && p.Protects(normalisedPath, _ => existsOnBase)) is { } immutable
            ? $"declares the protected path '{normalisedPath}', which already exists on the base commit ({immutable.Reason ?? $"{immutable.Glob.Pattern} files are immutable"}); add a new file instead."
            : null;
    }

    private static string OwnersOf(Preset preset, string path)
    {
        var owners = preset.Agents.Where(a => a.Owns.IsMatch(path)).Select(a => a.Key).ToList();
        return owners.Count == 0 ? $"no agent of the {preset.Id} preset owns it" : $"the {preset.Id} preset's {string.Join(", ", owners)}";
    }

    /// <summary>
    /// §4.11 scaffold-first: whether a repository holding only <paramref name="paths"/> counts as empty — no files, or only
    /// what a new repository starts with (README, LICENSE, <c>.gitignore</c>, <c>.gitattributes</c> at the root).
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
