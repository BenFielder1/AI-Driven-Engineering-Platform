using Aide.Domain.Presets;
using Aide.Domain.Review;

namespace Aide.Domain.Stack;

/// <summary>
/// FR-120, FR-121, FR-123–FR-127, §4.18: everything a composition gives the lifecycle agents, resolved by
/// <see cref="StackResolver.Resolve"/> from a normalised composition and the catalog. Globs, commands and prompts are
/// already expanded with the layer paths (<see cref="StackPlaceholders"/>); entries whose layer is missing are dropped.
/// </summary>
public sealed record StackResolution
{
    /// <summary><c>stack@&lt;catalog version&gt;</c>, as recorded on agent runs.</summary>
    public required string CatalogLabel { get; init; }

    public required StackComposition Composition { get; init; }

    public required StackLayerPaths Paths { get; init; }

    /// <summary>R2: <c>full-stack</c> or <c>ui</c> with <c>fe-nextjs</c>; otherwise <c>null</c>.</summary>
    public string? NextjsMode { get; init; }

    /// <summary>FR-117: the stack change the version makes (<see cref="StackChange.None"/> when there is none).</summary>
    public required StackChange Change { get; init; }

    /// <summary>The packs in play, in choice order, each with its mode (Supabase <c>host</c> when it is only the database host).</summary>
    public required IReadOnlyList<ResolvedPack> Packs { get; init; }

    /// <summary>FR-120: the specialists in play, in catalogue order (<see cref="StackAgents.All"/>).</summary>
    public required IReadOnlyList<StackSpecialist> Specialists { get; init; }

    /// <summary>FR-123: the required spec sections (deduplicated by title, case-insensitive), with their guidance.</summary>
    public required IReadOnlyList<StackSpecSection> SpecSections { get; init; }

    /// <summary>FR-124: the composed plan rules (v2 §4.11 shapes).</summary>
    public required CompositionPlanRules PlanRules { get; init; }

    /// <summary>FR-125: the packs' protected paths (added to the v1 Guardian rules).</summary>
    public required IReadOnlyList<StackProtectedPath> ProtectedPaths { get; init; }

    /// <summary>FR-127: how each generated file is handled, with its CI check expanded.</summary>
    public required IReadOnlyList<StackGeneratedFile> GeneratedFiles { get; init; }

    /// <summary>
    /// FR-127: the contract file(s) a step adding endpoints must declare (the backend pack's hand-written generated files,
    /// e.g. <c>backend/openapi/api.json</c>); empty without a backend.
    /// </summary>
    public required IReadOnlyList<string> ContractFiles { get; init; }

    /// <summary>FR-127: the backend's API surface (endpoint and DTO files); a step declaring one adds or changes endpoints.</summary>
    public required StackGlobSet ApiSurface { get; init; }

    /// <summary>FR-126: the composed review checklist (each pack's <c>Checklist</c> items with category and severity).</summary>
    public required IReadOnlyList<StackChecklistItem> Checklist { get; init; }

    /// <summary>FR-126: the deterministic review rule ids registered by the packs (<see cref="StackReviewChecks"/>).</summary>
    public required IReadOnlyList<string> ReviewChecks { get; init; }

    /// <summary>§4.22: the required environment variables (public keys carry the frontend pack's prefix).</summary>
    public required IReadOnlyList<StackEnvVar> Env { get; init; }

    /// <summary>The frontend and backend packs' Gate commands, expanded.</summary>
    public required IReadOnlyList<string> Gates { get; init; }

    /// <summary>The data pack's one-off migrate command, or <c>null</c>.</summary>
    public string? MigrateCommand { get; init; }

    /// <summary>FR-121 step 2: the rendered composition summary (the STACK.md equivalent).</summary>
    public required string Summary { get; init; }

    /// <summary>The keys of <see cref="Specialists"/>, in order.</summary>
    public IReadOnlyList<string> AgentKeys => [.. Specialists.Select(s => s.Key)];

    /// <summary>The specialist with <paramref name="key"/> (ordinal), or <c>null</c>.</summary>
    public StackSpecialist? Specialist(string? key) => key is null ? null : Specialists.FirstOrDefault(s => s.Key == key);

    /// <summary>The <c>env</c> names, in order.</summary>
    public IReadOnlyList<string> EnvNames => [.. Env.Select(e => e.Key)];

    /// <summary>The spec section titles (for spec validation).</summary>
    public IReadOnlyList<string> SpecSectionTitles => [.. SpecSections.Select(s => s.Title)];

    /// <summary>
    /// FR-125: whether a pack protects <paramref name="normalisedPath"/>. <paramref name="existsOnBase"/> answers "does this
    /// file exist on the version's base commit" (asked only for <see cref="StackProtectedPath.ExistingOnBase"/> entries).
    /// </summary>
    public bool IsProtected(string normalisedPath, Func<string, bool> existsOnBase)
    {
        ArgumentNullException.ThrowIfNull(normalisedPath);
        ArgumentNullException.ThrowIfNull(existsOnBase);
        return ProtectedPaths.Any(p => p.Protects(normalisedPath, existsOnBase));
    }

    /// <summary>Whether <paramref name="normalisedPath"/> is a data pack migration (an <c>existingOnBase</c> entry's file).</summary>
    public bool IsMigration(string normalisedPath)
    {
        ArgumentNullException.ThrowIfNull(normalisedPath);
        return ProtectedPaths.Any(p => p.ExistingOnBase && p.Protects(normalisedPath, _ => true));
    }
}

/// <summary>The layer paths of a composition (FR-119; <c>null</c> = the layer is None).</summary>
public sealed record StackLayerPaths(string? Frontend, string? Backend)
{
    /// <summary>§4.18 <c>{app}</c>: the backend path, else the frontend path (the Next.js full-stack app).</summary>
    public string? App => Backend ?? Frontend;
}

/// <summary>A pack in play and its mode (<c>baas</c>/<c>host</c> for Supabase, the Next.js mode, or <c>none</c>).</summary>
public sealed record ResolvedPack(StackPack Pack, string Choice, string Mode)
{
    public string Id => Pack.Id;

    public string Layer => Pack.Manifest.Layer;

    /// <summary>§4.18: Supabase as the database host of another data pack: it only lends its <c>Production notes</c>.</summary>
    public bool IsSupabaseHost => Pack.Id == StackIds.Supabase && Mode == StackIds.HostMode;
}

/// <summary>
/// FR-120: a specialist in play: its catalogue entry, the globs it owns (expanded, <c>!</c> = exclusion), its resolved
/// skills (FR-121, in prompt order) and its prompt (provenance stripped, placeholders expanded).
/// </summary>
public sealed record StackSpecialist(
    string Key,
    string Name,
    string Description,
    IReadOnlyList<string> OwnedGlobs,
    StackGlobSet Owns,
    IReadOnlyList<StackSkill> Skills,
    string Prompt)
{
    /// <summary>FR-121: what the run records: <c>packs[] {id, version, sections[]}</c>, in prompt order.</summary>
    public IReadOnlyList<StackPackRecord> Packs =>
    [
        .. Skills.GroupBy(s => (s.Pack, s.Version))
            .Select(g => new StackPackRecord(g.Key.Pack, g.Key.Version, [.. g.Select(s => s.Section)])),
    ];
}

/// <summary>A resolved skill section: rendered as <c>## Skill: &lt;pack&gt; — &lt;section&gt;</c> (FR-121).</summary>
public sealed record StackSkill(string Pack, string Version, string Section, string Text)
{
    public string Label => $"{Pack}@{Version}";
}

/// <summary>FR-121: a pack an agent run loaded, with the sections it loaded.</summary>
public sealed record StackPackRecord(string Id, string Version, IReadOnlyList<string> Sections)
{
    public string Label => $"{Id}@{Version}";
}

/// <summary>FR-123: a required spec section, its guidance, and where it comes from (a pack id or <c>core</c>).</summary>
public sealed record StackSpecSection(string Title, string Guidance, string Source);

/// <summary>FR-124: the composed plan rules, in the v2 §4.11 shapes (<see cref="CompositionPlanValidator"/>).</summary>
public sealed record CompositionPlanRules(
    string Scaffolder,
    IReadOnlyList<StackOrderRule> Order,
    IReadOnlyList<StackExclusiveRule> Exclusive,
    IReadOnlyList<StackFileLimit> FileLimits);

/// <summary>
/// A step declaring a file matching <see cref="Before"/> must come before every step declaring a file matching
/// <see cref="After"/>. An error, unless <see cref="Soft"/> (then a <c>PLAN_ORDER</c> warning).
/// </summary>
public sealed record StackOrderRule(StackGlobSet Before, StackGlobSet After, bool Soft, string? Reason, string Pack);

/// <summary>A step assigned <see cref="Agent"/> must not declare files matching <see cref="NotWith"/> (always an error).</summary>
public sealed record StackExclusiveRule(string Agent, StackGlobSet NotWith, string? Reason, string Pack);

/// <summary>A step declares at most <see cref="Max"/> files matching <see cref="Glob"/> (an error).</summary>
public sealed record StackFileLimit(PathGlob Glob, int Max, string Pack);

/// <summary>
/// FR-125: a protected path: <see cref="Glob"/>, except paths matching <see cref="Except"/>; with
/// <see cref="ExistingOnBase"/> only files that already exist on the version's base commit (immutable migrations).
/// </summary>
public sealed record StackProtectedPath(PathGlob Glob, bool ExistingOnBase, IReadOnlyList<PathGlob> Except, string Reason, string Pack)
{
    public bool Protects(string normalisedPath, Func<string, bool> existsOnBase)
    {
        ArgumentNullException.ThrowIfNull(existsOnBase);
        return Glob.IsMatch(normalisedPath)
            && !PathGlob.AnyMatch(Except, normalisedPath)
            && (!ExistingOnBase || existsOnBase(normalisedPath));
    }
}

/// <summary>FR-127: a generated file (expanded glob), its handling and its CI check (expanded), and the pack declaring it.</summary>
public sealed record StackGeneratedFile(string Glob, PathGlob Matcher, string Handling, string? CiCheck, string Pack);

/// <summary>FR-126: a checklist item of a pack, mapped to a v1 compliance category and severity.</summary>
public sealed record StackChecklistItem(string Pack, string Item, ReviewCategory Category, Severity Severity);

/// <summary>§4.22: a required environment variable: the components it applies to, its targets and the packs requiring it.</summary>
public sealed record StackEnvVar(
    string Key,
    IReadOnlyList<string> AppliesTo,
    IReadOnlyList<string> Targets,
    bool Sensitive,
    bool Public,
    string Description,
    IReadOnlyList<string> Packs);
