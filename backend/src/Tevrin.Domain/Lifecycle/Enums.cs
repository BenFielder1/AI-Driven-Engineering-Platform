namespace Tevrin.Domain.Lifecycle;

// The summary keeps 0.6.1's text, so the generated contract (backend/openapi/api.json) is unchanged; the order type it names
// was removed in FR-293 (the entity spells it without the identifier). Since FR-293 the stage order is the SDLC definition's
// (SdlcStages), and Phase stays as the SDLC's typed stage id for entities, DTOs and rules.
/// <summary>
/// The lifecycle phases, in order (§4.1; 0.5.0 §4.27 adds <see cref="Analysis"/> first). Stored as snake_case strings, so the
/// numeric values only give the order (Phase&#x4F;rder) and may be renumbered.
/// </summary>
public enum Phase
{
    /// <summary>FR-179, §4.27: the optional first phase of a version that infers its stack (<c>stack_source = infer</c>).</summary>
    Analysis = 0,
    Refinement = 1,
    Specification = 2,
    Planning = 3,
    Execution = 4,
    Review = 5,
    Release = 6,
}

/// <summary>Project status (§4.1: status follows phase; <c>released</c>, <c>rolled_back</c> and <c>awaiting_merge</c> are special).</summary>
public enum ProjectStatus
{
    Active = 0,
    Building = 1,
    Reviewing = 2,
    Releasing = 3,
    Released = 4,
    RolledBack = 5,

    /// <summary>
    /// FR-65: in the release phase, the release attempt's merge was refused (branch protection, required reviews or checks,
    /// missing permission, or the <c>manual</c> merge strategy) and the merge poll waits for the PR to be merged on GitHub.
    /// </summary>
    AwaitingMerge = 6,
}
