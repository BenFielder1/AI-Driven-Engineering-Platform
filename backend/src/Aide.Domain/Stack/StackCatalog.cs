namespace Aide.Domain.Stack;

/// <summary>
/// FR-113, §4.16: the stack catalog, loaded once from <c>Agents/Stack/</c> by <see cref="StackCatalogLoader"/>. Immutable.
/// <see cref="Version"/> (catalog.json, semver) and <see cref="ContentHash"/> (SHA-256 over every file) are what release
/// versions pin and agent runs record (<see cref="Label"/>).
/// </summary>
public sealed record StackCatalog
{
    public required string Version { get; init; }

    public required string ContentHash { get; init; }

    public required IReadOnlyList<StackLayer> Layers { get; init; }

    /// <summary>The choices, in <see cref="StackChoices.Order"/>.</summary>
    public required IReadOnlyList<StackChoice> Choices { get; init; }

    /// <summary>Every pack, by id.</summary>
    public required IReadOnlyDictionary<string, StackPack> Packs { get; init; }

    public required StackRules Rules { get; init; }

    public required IReadOnlyList<StackRecommendation> Recommendations { get; init; }

    /// <summary>The adapted stack-core <c>stack-api-contract</c> skill (<c>core/stack-api-contract.md</c>).</summary>
    public required string StackApiContract { get; init; }

    /// <summary>The composition summary template (<c>core/composition-summary.md</c>).</summary>
    public required string CompositionSummaryTemplate { get; init; }

    /// <summary>Adapted specialist prompts (<c>agents/&lt;key&gt;.md</c>), by agent key; may be empty.</summary>
    public required IReadOnlyDictionary<string, string> AgentPrompts { get; init; }

    /// <summary>§4.16: <c>stack@&lt;version&gt;</c>, as recorded on agent runs.</summary>
    public string Label => $"stack@{Version}";

    /// <summary>The choice with <paramref name="id"/> (one of <see cref="StackChoices.Order"/>).</summary>
    public StackChoice Choice(string id) =>
        Choices.FirstOrDefault(c => c.Id == id) ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown stack choice.");
}

/// <summary>A catalog layer (one of the five Stack sub-steps).</summary>
public sealed record StackLayer(string Id, string Name, string Description);

/// <summary>A choice of the Stack step (§4.17) with its options in display order.</summary>
public sealed record StackChoice(string Id, string Layer, string Name, IReadOnlyList<StackOption> Options)
{
    /// <summary>The option with <paramref name="id"/>, or <c>null</c>.</summary>
    public StackOption? Find(string? id) => id is null ? null : Options.FirstOrDefault(o => o.Id == id);

    /// <summary>Whether the choice offers <see cref="StackIds.None"/>.</summary>
    public bool HasNone => Find(StackIds.None) is not null;
}

/// <summary>
/// An option of a choice: a pack (name, chips and description come from its manifest unless overridden), or a plain
/// option (None, External, an origin model). <see cref="Mode"/> is the pack mode the option selects (Supabase BaaS);
/// <see cref="VcsHost"/>/<see cref="SelfManagedVcsHost"/> are the R9 hosts of a VCS option.
/// </summary>
public sealed record StackOption
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public IReadOnlyList<string> Chips { get; init; } = [];

    public string? Pack { get; init; }

    public string? Mode { get; init; }

    public string? VcsHost { get; init; }

    public string? SelfManagedVcsHost { get; init; }
}

/// <summary>
/// A loaded pack: its manifest, the adapted skill text, the skill's section headings (as matched, §4.16: trailing
/// parenthetical stripped) and the SHA-256 of its files.
/// </summary>
public sealed record StackPack(PackManifest Manifest, string Skill, IReadOnlyList<string> Sections, string ContentHash)
{
    public string Id => Manifest.Id;

    /// <summary><c>&lt;id&gt;@&lt;version&gt;</c>, as recorded on agent runs (FR-121).</summary>
    public string Label => $"{Manifest.Id}@{Manifest.Version}";
}

/// <summary>A recommended combination from stack-packs <c>compose-stack</c>: option ids per choice.</summary>
public sealed record StackRecommendation(string Id, string Name, IReadOnlyDictionary<string, IReadOnlyList<string>> Choices);

/// <summary>§4.17: the compatibility rules as data.</summary>
public sealed record StackRules
{
    /// <summary>Rule id (<c>R1</c>…) → one-line statement.</summary>
    public required IReadOnlyDictionary<string, string> Statements { get; init; }

    public required IReadOnlyList<StackConstraint> Constraints { get; init; }

    /// <summary>R10, first match wins; the last entry has no condition.</summary>
    public required IReadOnlyList<OriginModelRule> OriginModel { get; init; }

    /// <summary>Default options (R12, "same as frontend"), first applicable entry per choice wins.</summary>
    public required IReadOnlyList<StackDefault> Defaults { get; init; }
}

/// <summary>
/// A rule entry: on <see cref="Choices"/>, each of <see cref="Options"/> gets <see cref="State"/> (forbidden or caveat) with
/// <see cref="Text"/> when any <see cref="When"/> clause holds (or there are none) and no <see cref="Unless"/> clause holds.
/// A clause whose facts aren't all known yet never fires the rule (it can't hold for sure, and it may hold for an unless).
/// <see cref="Text"/> may use <c>{option}</c> and <c>{&lt;choice&gt;}</c> placeholders (option names).
/// </summary>
public sealed record StackConstraint(
    string Rule,
    IReadOnlyList<string> Choices,
    IReadOnlyList<string> Options,
    StackOptionState State,
    IReadOnlyList<StackClause> When,
    IReadOnlyList<StackClause> Unless,
    string Text);

/// <summary>R10: when <see cref="When"/> matches (no clauses: always), only <see cref="Allowed"/> origin models are allowed.</summary>
public sealed record OriginModelRule(IReadOnlyList<StackClause> When, IReadOnlyList<string> Allowed, string? Default, string Text);

/// <summary>
/// A default: <see cref="Option"/>, or the option picked for <see cref="SameAs"/>, when <see cref="When"/> matches and it
/// isn't forbidden. An <see cref="Implicit"/> default also applies when a validated composition leaves the choice out
/// (R12 Compose, Supabase BaaS's database host); other defaults (the backend host "same as frontend") only preselect.
/// </summary>
public sealed record StackDefault(string Choice, string? Option, string? SameAs, IReadOnlyList<StackClause> When, bool Implicit = false);

/// <summary>A conjunction of fact matches (catalog.json: <c>{"backend": ["be-express", "be-nestjs"], "vcsHost": ["!github"]}</c>).</summary>
public sealed record StackClause(IReadOnlyDictionary<string, StackFactMatch> Facts);

/// <summary>A fact match: the value is in <see cref="In"/> (when not empty) and not in <see cref="NotIn"/> (values written <c>!x</c>).</summary>
public sealed record StackFactMatch(IReadOnlyList<string> In, IReadOnlyList<string> NotIn)
{
    public bool Matches(string value) =>
        (In.Count == 0 || In.Contains(value, StringComparer.Ordinal)) && !NotIn.Contains(value, StringComparer.Ordinal);
}

/// <summary>§4.17: the state of an option for the choices made so far.</summary>
public enum StackOptionState
{
    Allowed,
    Caveat,
    Forbidden,

    /// <summary>The option is the current selection but earlier choices now forbid it (<c>/options</c> only).</summary>
    Invalidated,
}
