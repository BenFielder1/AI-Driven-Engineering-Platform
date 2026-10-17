using Aide.Domain.Review;

namespace Aide.Domain.Presets;

/// <summary>
/// FR-74, §4.11, D-V2-12: a tech stack preset — a versioned, content-hashed bundle of data loaded from
/// <c>Agents/Presets/&lt;id&gt;/</c> (see <see cref="PresetLoader"/> and <c>Presets/README.md</c> for the file format).
/// Every referenced file is already read: agent prompts, skills and the spec conventions are text here.
/// </summary>
public sealed record Preset
{
    /// <summary>Lowercase kebab-case id, equal to the folder name, e.g. <c>nextjs-supabase</c>.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The preset's own version (<c>preset.json</c> <c>version</c>), pinned on release versions and recorded on runs.</summary>
    public required string Version { get; init; }

    /// <summary>SHA-256 (lowercase hex) over every file of the preset folder (<see cref="PresetLoader.ContentHash"/>).</summary>
    public required string ContentHash { get; init; }

    public required string Description { get; init; }

    /// <summary>Stack chips, e.g. <c>Next.js (App Router)</c>.</summary>
    public required IReadOnlyList<string> Stack { get; init; }

    /// <summary>Supported deployment targets (<see cref="PresetVocabulary.DeployTargets"/>), e.g. <c>vercel</c>.</summary>
    public required IReadOnlyList<string> DeployTargets { get; init; }

    public required IReadOnlyList<PresetEnvVar> RequiredEnv { get; init; }

    /// <summary>FR-75: extra spec sections the Spec Generator must write (titles feed <c>SpecValidator</c>).</summary>
    public required IReadOnlyList<PresetSpecSection> SpecSections { get; init; }

    /// <summary>Stack conventions (Markdown) for the spec generator and every specialist; empty when the preset has none.</summary>
    public required string SpecConventions { get; init; }

    public required PresetPlanRules PlanRules { get; init; }

    /// <summary>FR-76: the specialist agent catalogue, in <c>preset.json</c> order.</summary>
    public required IReadOnlyList<PresetAgent> Agents { get; init; }

    public required PresetReview Review { get; init; }

    /// <summary>§4.11: paths added to the Guardian's protected list (with the "unless declared in this step" exception).</summary>
    public required IReadOnlyList<PresetProtectedPath> ProtectedPaths { get; init; }

    /// <summary>FR-74: what agent runs record, <c>&lt;id&gt;@&lt;version&gt;</c> (e.g. <c>nextjs-frontend@1.0.0</c>).</summary>
    public string RunLabel => PresetRules.RunLabel(Id, Version);

    /// <summary>The spec section titles (FR-75), for <c>SpecValidationContext</c>'s <c>presetSections</c>.</summary>
    public IReadOnlyList<string> SpecSectionTitles => [.. SpecSections.Select(s => s.Title)];

    /// <summary>The catalogue keys, in order.</summary>
    public IReadOnlyList<string> AgentKeys => [.. Agents.Select(a => a.Key)];

    /// <summary>The agent with <paramref name="key"/> (ordinal), or <c>null</c>.</summary>
    public PresetAgent? Agent(string? key) => key is null ? null : Agents.FirstOrDefault(a => a.Key == key);

    /// <summary>
    /// §4.11: whether the preset protects <paramref name="normalisedPath"/>. <paramref name="existsOnBase"/> answers
    /// "does this file exist on the version's base commit" (only asked for <see cref="PresetProtectedPath.ExistingOnBase"/> entries).
    /// </summary>
    public bool IsProtected(string normalisedPath, Func<string, bool> existsOnBase)
    {
        ArgumentNullException.ThrowIfNull(existsOnBase);
        return ProtectedPaths.Any(p => p.Protects(normalisedPath, existsOnBase));
    }
}

/// <summary>A required environment variable (FR-88 inventory): <c>targets</c> ⊆ production/preview/development.</summary>
public sealed record PresetEnvVar(string Key, IReadOnlyList<string> Targets, bool Sensitive, string Description);

/// <summary>An extra spec section (FR-75) and the guidance the Spec Generator gets for it.</summary>
public sealed record PresetSpecSection(string Title, string Guidance);

/// <summary>
/// A specialist agent (FR-76): <c>owns</c> are the paths it is expected to write (a mismatch is an <c>AGENT_SCOPE</c>
/// warning); <c>prompt</c> and every skill (<c>skills/&lt;name&gt;.md</c>) are rendered after the base code generator prompt.
/// </summary>
public sealed record PresetAgent
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required GlobSet Owns { get; init; }

    /// <summary>Skill names in render order.</summary>
    public required IReadOnlyList<string> Skills { get; init; }

    /// <summary>The prompt file as written in <c>preset.json</c>, e.g. <c>agents/db-engineer.md</c>.</summary>
    public required string PromptFile { get; init; }

    /// <summary>The prompt text.</summary>
    public required string Prompt { get; init; }

    /// <summary>The skill texts, in <see cref="Skills"/> order.</summary>
    public required IReadOnlyList<PresetSkill> SkillTexts { get; init; }
}

/// <summary>A condensed, AIDE-adapted skill (<c>skills/&lt;name&gt;.md</c>).</summary>
public sealed record PresetSkill(string Name, string Text);

/// <summary>§4.11 plan rules, checked after the v1 plan rules (<see cref="PresetPlanValidator"/>).</summary>
public sealed record PresetPlanRules
{
    /// <summary>On an empty repository step 1 must be assigned <see cref="Scaffolder"/> (an error).</summary>
    public bool ScaffoldFirstWhenEmpty { get; init; }

    /// <summary>The scaffolding agent's key (default <see cref="PresetRules.DefaultScaffolder"/>).</summary>
    public string Scaffolder { get; init; } = PresetRules.DefaultScaffolder;

    public IReadOnlyList<PresetOrderRule> Order { get; init; } = [];

    public IReadOnlyList<PresetExclusiveRule> Exclusive { get; init; } = [];

    /// <summary>Per-glob file limits: a step declares at most <see cref="PresetFileLimit.Max"/> files matching the glob (e.g. one migration per step).</summary>
    public IReadOnlyList<PresetFileLimit> FileLimits { get; init; } = [];
}

/// <summary>A step declares at most <see cref="Max"/> files matching <see cref="Glob"/> (an error).</summary>
public sealed record PresetFileLimit(PathGlob Glob, int Max);

/// <summary>
/// A step declaring a file matching <see cref="Before"/> must come before every step declaring a file matching
/// <see cref="After"/>. An error, unless <see cref="Soft"/> (then a <c>PLAN_ORDER</c> warning).
/// </summary>
public sealed record PresetOrderRule(GlobSet Before, GlobSet After, bool Soft, string? Reason);

/// <summary>A step assigned <see cref="Agent"/> must not declare files matching <see cref="NotWith"/> (an error).</summary>
public sealed record PresetExclusiveRule(string Agent, GlobSet NotWith, string? Reason);

/// <summary>FR-77: the review checklist and the preset's deterministic rule ids (<see cref="PresetVocabulary.DeterministicRules"/>).</summary>
public sealed record PresetReview(IReadOnlyList<PresetChecklistItem> Checklist, IReadOnlyList<string> Deterministic);

/// <summary>A checklist item, mapped to a v1 compliance category (§4.7) with a severity.</summary>
public sealed record PresetChecklistItem(ReviewCategory Category, string Item, Severity Severity);

/// <summary>
/// A protected path (§4.11): <see cref="Glob"/>, except paths matching <see cref="Except"/>; with
/// <see cref="ExistingOnBase"/> only files that already exist on the version's base commit (immutable migrations).
/// </summary>
public sealed record PresetProtectedPath(PathGlob Glob, bool ExistingOnBase, IReadOnlyList<PathGlob> Except, string? Reason = null)
{
    public bool Protects(string normalisedPath, Func<string, bool> existsOnBase)
    {
        ArgumentNullException.ThrowIfNull(existsOnBase);
        return Glob.IsMatch(normalisedPath)
            && !PathGlob.AnyMatch(Except, normalisedPath)
            && (!ExistingOnBase || existsOnBase(normalisedPath));
    }
}
