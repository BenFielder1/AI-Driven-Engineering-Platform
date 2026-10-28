namespace Aide.Domain.Stack;

/// <summary>
/// The choices made in the Stack step (§4.17), possibly partial. A missing layer (<c>null</c>) is "not chosen yet" for
/// <see cref="StackCompatibility.Evaluate"/>; for <see cref="StackCompatibility.Validate"/> it means None (the local dev loop
/// and the origin model take their defaults). <see cref="StackIds.None"/> as a pack id is an explicit None.
/// </summary>
public sealed record StackSelection
{
    public StackComponentPick? Frontend { get; init; }

    public StackComponentPick? Backend { get; init; }

    public StackDataPick? Data { get; init; }

    public StackVcsPick? Vcs { get; init; }

    public StackDeployPick? Deploy { get; init; }

    /// <summary><c>same-origin</c>, <c>proxy</c> or <c>cross-origin</c> (R10).</summary>
    public string? OriginModel { get; init; }
}

/// <summary>A frontend or backend pick: the pack (or <c>none</c>) and its path in the repo (FR-119; default from the pack).</summary>
public sealed record StackComponentPick(string Pack, string? Path = null);

/// <summary>A data pick; <see cref="Mode"/> defaults to the option's mode (Supabase: <c>baas</c>).</summary>
public sealed record StackDataPick(string Pack, string? Mode = null);

/// <summary>A VCS pick: the pack, the Git host instance (GitLab), and whether that instance is self-managed (R9).</summary>
public sealed record StackVcsPick(string Pack, Guid? InstanceId = null, bool SelfManaged = false);

/// <summary>The Deployment layer's picks: option ids of the frontend, backend and database hosts and the local dev loop.</summary>
public sealed record StackDeployPick(string? FrontendHost = null, string? BackendHost = null, string? DatabaseHost = null, string? Local = null);

/// <summary>
/// §4.18: the normalised composition, as pinned on a release version. None is <c>null</c> for the frontend, backend, data
/// and hosts; <see cref="OriginModel"/> is <c>null</c> without a frontend (API only).
/// </summary>
public sealed record StackComposition
{
    public required string CatalogVersion { get; init; }

    /// <summary>The catalog hash the composition was validated against; <c>null</c> for compositions written by the FR-129 preset migration (re-pinned on the next write).</summary>
    public required string? CatalogHash { get; init; }

    public StackComponent? Frontend { get; init; }

    public StackComponent? Backend { get; init; }

    public StackDataLayer? Data { get; init; }

    public required StackVcs Vcs { get; init; }

    public required StackDeploy Deploy { get; init; }

    public string? OriginModel { get; init; }

    public required StackDerived Derived { get; init; }

    /// <summary>The caveats of the chosen options (shown on the summary card).</summary>
    public required IReadOnlyList<StackCaveat> Caveats { get; init; }
}

public sealed record StackComponent(string Pack, string Path);

public sealed record StackDataLayer(string Pack, string? Mode);

public sealed record StackVcs(string Pack, Guid? InstanceId, bool SelfManaged);

/// <summary>Host option ids (<c>null</c> = None) and the local dev loop (<c>compose</c> or <c>none</c>).</summary>
public sealed record StackDeploy(string? FrontendHost, string? BackendHost, string? DatabaseHost, string Local);

/// <summary>
/// §4.18 derived facts: the Next.js mode (R2), the specialists in play (FR-120), the origin models the Owner may switch
/// between (R10), and the unions of the selected packs' spec sections, env names and gate commands.
/// </summary>
public sealed record StackDerived(
    string? NextjsMode,
    IReadOnlyList<string> Agents,
    IReadOnlyList<string> OriginModelOptions,
    IReadOnlyList<string> SpecSections,
    IReadOnlyList<string> EnvNames,
    IReadOnlyList<string> Gates);

/// <summary>A caveat of a chosen option: the choice (<see cref="Layer"/>), the option, the rule and its text.</summary>
public sealed record StackCaveat(string Layer, string Option, string Rule, string Text);

/// <summary>FR-116: a broken rule: the choice (<see cref="Layer"/>, e.g. <c>data</c> or <c>backendHost</c>), the option, the rule and the reason.</summary>
public sealed record StackViolation(string Layer, string Option, string Rule, string Reason);

/// <summary>A reason for an option's state: the rule id and its text.</summary>
public sealed record StackReason(string Rule, string Text);

/// <summary>An option's state for the choices made so far.</summary>
public sealed record StackOptionEvaluation(string Option, StackOptionState State, IReadOnlyList<StackReason> Reasons, bool Recommended);

/// <summary>
/// One choice of <c>POST /api/stack/options</c>: the current selection (if any), the default to preselect (R12 Compose,
/// "same as frontend", the R10 origin model) and every option with its state.
/// </summary>
public sealed record StackChoiceEvaluation(string Choice, string Layer, string? Selected, string? Default, IReadOnlyList<StackOptionEvaluation> Options);

/// <summary>§4.17 <c>Evaluate</c>: every choice with its options' states, and the Next.js mode when known.</summary>
public sealed record StackEvaluation(IReadOnlyList<StackChoiceEvaluation> Choices, string? NextjsMode)
{
    public StackChoiceEvaluation Choice(string id) => Choices.First(c => c.Choice == id);
}

/// <summary>A malformed input field (<c>VALIDATION</c>): the field path and the message.</summary>
public sealed record StackInputError(string Field, string Message)
{
    public override string ToString() => $"{Field}: {Message}";
}

/// <summary>
/// FR-116 <c>Validate</c>: the normalised composition, or the violations (<c>STACK_INCOMPATIBLE</c>), or input errors
/// (<c>VALIDATION</c>, checked first).
/// </summary>
public sealed record StackValidation(StackComposition? Composition, IReadOnlyList<StackViolation> Violations, IReadOnlyList<StackInputError> InputErrors)
{
    public bool IsValid => Composition is not null;
}
