using System.ComponentModel.DataAnnotations;
using Aide.Domain.Stack;

namespace Aide.Api.Features.Stack;

// ---------- GET /api/stack/catalog ----------

/// <summary>
/// The stack catalog (FR-113, §4.16) for the wizard's Stack step, Settings → Stack and Admin → Stack catalog: the five
/// layers, the choices in evaluation order (frontend, backend, data, vcs, then the deployment choices frontendHost,
/// backendHost, databaseHost, local and originModel) with their options, the rule statements and the recommended
/// combinations. <c>version</c> and <c>contentHash</c> are what release versions pin.
/// </summary>
public sealed record StackCatalogDto(
    string Version,
    string ContentHash,
    IReadOnlyList<StackLayerDto> Layers,
    IReadOnlyList<StackChoiceDto> Choices,
    IReadOnlyList<StackRuleDto> Rules,
    IReadOnlyList<StackRecommendationDto> Recommendations);

/// <summary>One of the five layers (Frontend, Backend, Data, VCS, Deployment).</summary>
public sealed record StackLayerDto(string Id, string Name, string Description);

/// <summary>A choice of the Stack step; <c>layer</c> is the layer it belongs to (the deployment choices share <c>deploy</c>).</summary>
public sealed record StackChoiceDto(string Id, string Layer, string Name, IReadOnlyList<StackOptionDto> Options);

/// <summary>
/// An option card: name, one-line description, chips, and for pack options the pack id, its version
/// (<c>&lt;plugin version&gt;+aide.&lt;n&gt;</c>), its stack-packs source and its modes. <c>mode</c> is the pack mode the option
/// selects (Supabase: <c>baas</c>). Plain options (None, External, origin models) have no pack.
/// </summary>
public sealed record StackOptionDto(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Chips,
    string? Pack,
    string? PackVersion,
    StackPackSourceDto? Source,
    IReadOnlyList<StackModeDto> Modes,
    string? Mode);

/// <summary>Where a pack was adapted from (FR-122): the stack-packs plugin, its version and the snapshot date.</summary>
public sealed record StackPackSourceDto(string Plugin, string Version, string Snapshot);

public sealed record StackModeDto(string Id, string Name, string Description);

/// <summary>A compatibility rule (§4.17): its stable id (<c>R1</c>…) and statement.</summary>
public sealed record StackRuleDto(string Id, string Statement);

/// <summary>A recommended combination (stack-packs <c>compose-stack</c>): option ids per choice id.</summary>
public sealed record StackRecommendationDto(string Id, string Name, IReadOnlyDictionary<string, IReadOnlyList<string>> Choices);

// ---------- POST /api/stack/options and /api/stack/validate ----------

/// <summary>
/// A (partial) composition (§4.17, §4.18). For <c>/options</c> a missing layer is "not chosen yet"; for <c>/validate</c> it
/// is None, except <c>vcs</c> (required), <c>deploy.local</c> (default: Compose when there's a backend or a database, R12)
/// and <c>originModel</c> (the default of R10). <c>"none"</c> as a pack or host is an explicit None.
/// </summary>
public sealed record StackCompositionRequest
{
    public StackComponentSelection? Frontend { get; init; }

    public StackComponentSelection? Backend { get; init; }

    public StackDataSelection? Data { get; init; }

    public StackVcsSelection? Vcs { get; init; }

    public StackDeploySelection? Deploy { get; init; }

    /// <summary><c>same-origin</c>, <c>proxy</c> or <c>cross-origin</c>.</summary>
    [MaxLength(StackDtoLimits.IdMaxLength)]
    public string? OriginModel { get; init; }
}

/// <summary>A frontend or backend: the pack id (or <c>none</c>) and its path in the repo (FR-119; default <c>frontend</c>/<c>backend</c>).</summary>
public sealed record StackComponentSelection
{
    [Required]
    [MaxLength(StackDtoLimits.IdMaxLength)]
    public string Pack { get; init; } = string.Empty;

    [MaxLength(StackCompatibility.PathMaxLength)]
    public string? Path { get; init; }
}

/// <summary>The data layer: the pack id (or <c>none</c>); <c>mode</c> defaults to the option's (Supabase: <c>baas</c>).</summary>
public sealed record StackDataSelection
{
    [Required]
    [MaxLength(StackDtoLimits.IdMaxLength)]
    public string Pack { get; init; } = string.Empty;

    [MaxLength(StackDtoLimits.IdMaxLength)]
    public string? Mode { get; init; }
}

/// <summary>The VCS layer: the pack id, the GitLab host instance, and whether it is self-managed (R9).</summary>
public sealed record StackVcsSelection
{
    [Required]
    [MaxLength(StackDtoLimits.IdMaxLength)]
    public string Pack { get; init; } = string.Empty;

    public Guid? InstanceId { get; init; }

    public bool SelfManaged { get; init; }
}

/// <summary>The Deployment layer: option ids of the frontend, backend and database hosts (or <c>none</c>) and the local dev loop (<c>compose</c>/<c>none</c>).</summary>
public sealed record StackDeploySelection
{
    [MaxLength(StackDtoLimits.IdMaxLength)]
    public string? FrontendHost { get; init; }

    [MaxLength(StackDtoLimits.IdMaxLength)]
    public string? BackendHost { get; init; }

    [MaxLength(StackDtoLimits.IdMaxLength)]
    public string? DatabaseHost { get; init; }

    [MaxLength(StackDtoLimits.IdMaxLength)]
    public string? Local { get; init; }
}

/// <summary>
/// <c>POST /api/stack/options</c> (§4.17): every choice in evaluation order with every option's state. A selection that
/// earlier choices now forbid is <c>invalidated</c> (the wizard clears it and names the reason); later choices are
/// evaluated without it. <c>nextjsMode</c> is R2's derived mode once known.
/// </summary>
public sealed record StackOptionsDto(string CatalogVersion, IReadOnlyList<StackChoiceStateDto> Choices, string? NextjsMode);

/// <summary>
/// A choice's options for the choices made so far: <c>selected</c> is the current selection, <c>default</c> the option to
/// preselect (R12 Compose, "same as frontend" for the backend host, the R10 origin model).
/// </summary>
public sealed record StackChoiceStateDto(string Choice, string Layer, string? Selected, string? Default, IReadOnlyList<StackOptionStateDto> Options);

/// <summary>An option's state (<c>allowed</c>, <c>caveat</c>, <c>forbidden</c>, <c>invalidated</c>) with its reasons, and whether a recommended combination includes it.</summary>
public sealed record StackOptionStateDto(string Id, StackOptionState State, IReadOnlyList<StackReasonDto> Reasons, bool Recommended);

/// <summary>A rule id (<c>R1</c>…) and its text, e.g. <c>R3</c> "Prisma needs Express, NestJS or Next.js full-stack".</summary>
public sealed record StackReasonDto(string Rule, string Text);

/// <summary>
/// The normalised composition (§4.18) returned by <c>POST /api/stack/validate</c>: None is <c>null</c>;
/// <c>originModel</c> is <c>null</c> without a frontend (API only).
/// </summary>
public sealed record StackCompositionDto(
    string CatalogVersion,
    string CatalogHash,
    StackComponentDto? Frontend,
    StackComponentDto? Backend,
    StackDataDto? Data,
    StackVcsDto Vcs,
    StackDeployDto Deploy,
    string? OriginModel,
    StackDerivedDto Derived,
    IReadOnlyList<StackCaveatDto> Caveats);

public sealed record StackComponentDto(string Pack, string Path);

public sealed record StackDataDto(string Pack, string? Mode);

public sealed record StackVcsDto(string Pack, Guid? InstanceId, bool SelfManaged);

/// <summary>Host option ids (<c>null</c> = None) and the local dev loop (<c>compose</c> or <c>none</c>).</summary>
public sealed record StackDeployDto(string? FrontendHost, string? BackendHost, string? DatabaseHost, string Local);

/// <summary>
/// Derived facts (§4.18): the Next.js mode (R2), the specialists in play (FR-120), the origin models the Owner can switch
/// between (R10), and the selected packs' spec sections, env names and gate commands.
/// </summary>
public sealed record StackDerivedDto(
    string? NextjsMode,
    IReadOnlyList<string> Agents,
    IReadOnlyList<string> OriginModelOptions,
    IReadOnlyList<string> SpecSections,
    IReadOnlyList<string> EnvNames,
    IReadOnlyList<string> Gates);

/// <summary>A caveat of a chosen option: the choice (<c>layer</c>), the option, the rule and its text.</summary>
public sealed record StackCaveatDto(string Layer, string Option, string Rule, string Text);

/// <summary>
/// One entry of the <c>violations</c> extension of a <c>STACK_INCOMPATIBLE</c> problem (FR-116): the choice
/// (<c>layer</c>, e.g. <c>data</c> or <c>backendHost</c>), the option, the rule id and the reason.
/// </summary>
public sealed record StackViolationDto(string Layer, string Option, string Rule, string Reason);

// ---------- GET /api/admin/stack ----------

/// <summary>
/// Admin → Stack catalog (§6 <c>GET /api/admin/stack</c>): the catalog version and hash, whether it loaded (<c>loaded</c>
/// or <c>failed</c>), every load error, and each pack with its source, version, hash and load status.
/// </summary>
public sealed record AdminStackCatalogDto(string? Version, string ContentHash, string Status, IReadOnlyList<string> Errors, IReadOnlyList<AdminStackPackDto> Packs);

/// <summary>A pack folder: what its <c>pack.json</c> says (when readable), the SHA-256 of its files, and <c>loaded</c>/<c>failed</c> with the reasons.</summary>
public sealed record AdminStackPackDto(
    string Id,
    string? Layer,
    string? Name,
    string? Version,
    StackPackSourceDto? Source,
    string ContentHash,
    string Status,
    IReadOnlyList<string> Errors);

internal static class StackDtoLimits
{
    public const int IdMaxLength = 100;
}

internal static class StackMapping
{
    public const string Loaded = "loaded";
    public const string Failed = "failed";

    public static StackCatalogDto ToDto(StackCatalog catalog) => new(
        catalog.Version,
        catalog.ContentHash,
        [.. catalog.Layers.Select(l => new StackLayerDto(l.Id, l.Name, l.Description))],
        [.. catalog.Choices.Select(c => new StackChoiceDto(c.Id, c.Layer, c.Name, [.. c.Options.Select(o => ToDto(catalog, o))]))],
        [.. catalog.Rules.Statements.OrderBy(s => int.Parse(s.Key[1..], System.Globalization.CultureInfo.InvariantCulture)).Select(s => new StackRuleDto(s.Key, s.Value))],
        [.. catalog.Recommendations.Select(r => new StackRecommendationDto(r.Id, r.Name, r.Choices))]);

    private static StackOptionDto ToDto(StackCatalog catalog, StackOption option)
    {
        var pack = option.Pack is not null && catalog.Packs.TryGetValue(option.Pack, out var p) ? p.Manifest : null;
        return new StackOptionDto(
            option.Id,
            option.Name,
            option.Description,
            option.Chips,
            option.Pack,
            pack?.Version,
            pack is null ? null : new StackPackSourceDto(pack.Source.Plugin, pack.Source.Version, pack.Source.Snapshot),
            pack is null ? [] : [.. pack.Modes.Select(m => new StackModeDto(m.Id, m.Name, m.Description))],
            option.Mode);
    }

    public static StackSelection ToSelection(StackCompositionRequest request) => new()
    {
        Frontend = request.Frontend is { } f ? new StackComponentPick(f.Pack, f.Path) : null,
        Backend = request.Backend is { } b ? new StackComponentPick(b.Pack, b.Path) : null,
        Data = request.Data is { } d ? new StackDataPick(d.Pack, d.Mode) : null,
        Vcs = request.Vcs is { } v ? new StackVcsPick(v.Pack, v.InstanceId, v.SelfManaged) : null,
        Deploy = request.Deploy is { } x ? new StackDeployPick(x.FrontendHost, x.BackendHost, x.DatabaseHost, x.Local) : null,
        OriginModel = request.OriginModel,
    };

    public static StackOptionsDto ToDto(StackCatalog catalog, StackEvaluation evaluation) => new(
        catalog.Version,
        [.. evaluation.Choices.Select(c => new StackChoiceStateDto(
            c.Choice,
            c.Layer,
            c.Selected,
            c.Default,
            [.. c.Options.Select(o => new StackOptionStateDto(o.Option, o.State, [.. o.Reasons.Select(r => new StackReasonDto(r.Rule, r.Text))], o.Recommended))]))],
        evaluation.NextjsMode);

    public static StackCompositionDto ToDto(StackComposition c) => new(
        c.CatalogVersion,
        c.CatalogHash,
        c.Frontend is { } f ? new StackComponentDto(f.Pack, f.Path) : null,
        c.Backend is { } b ? new StackComponentDto(b.Pack, b.Path) : null,
        c.Data is { } d ? new StackDataDto(d.Pack, d.Mode) : null,
        new StackVcsDto(c.Vcs.Pack, c.Vcs.InstanceId, c.Vcs.SelfManaged),
        new StackDeployDto(c.Deploy.FrontendHost, c.Deploy.BackendHost, c.Deploy.DatabaseHost, c.Deploy.Local),
        c.OriginModel,
        new StackDerivedDto(c.Derived.NextjsMode, c.Derived.Agents, c.Derived.OriginModelOptions, c.Derived.SpecSections, c.Derived.EnvNames, c.Derived.Gates),
        [.. c.Caveats.Select(x => new StackCaveatDto(x.Layer, x.Option, x.Rule, x.Text))]);

    public static AdminStackCatalogDto ToAdminDto(Infrastructure.Stack.IStackCatalog catalog) => new(
        catalog.Version,
        catalog.ContentHash,
        catalog.Catalog is null ? Failed : Loaded,
        [.. catalog.Errors.Select(e => e.ToString())],
        [.. catalog.Packs.Select(p => new AdminStackPackDto(
            p.Id,
            p.Layer,
            p.Name,
            p.Version,
            p.Source is { } s ? new StackPackSourceDto(s.Plugin, s.Version, s.Snapshot) : null,
            p.ContentHash,
            p.Loaded ? Loaded : Failed,
            p.Errors))]);
}
