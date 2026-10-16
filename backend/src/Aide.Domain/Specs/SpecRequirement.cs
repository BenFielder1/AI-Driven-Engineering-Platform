namespace Aide.Domain.Specs;

/// <summary>§4.13: the iteration tag on a functional requirement's heading line (wire values <c>new</c>, <c>changed</c>, …).</summary>
public enum RequirementTag
{
    New = 0,
    Changed = 1,
    Unchanged = 2,
    Removed = 3,
}

/// <summary>
/// A functional requirement found in a spec (FR-75): its <c>FR-n</c> ID, the title text of its line, the iteration tags
/// on that line (§4.13; valid iteration specs have exactly one), whether its body has a Given/When/Then acceptance
/// criterion, and its 1-based line number.
/// </summary>
public sealed record SpecRequirement(
    string Id,
    int Number,
    string Title,
    IReadOnlyList<RequirementTag> Tags,
    bool HasAcceptanceCriterion,
    int Line)
{
    /// <summary>The single tag, or <c>null</c> when the line has none or more than one.</summary>
    public RequirementTag? Tag => Tags.Count == 1 ? Tags[0] : null;

    /// <summary>Whether the requirement is in scope of an iteration's review and plan (<c>[new]</c> or <c>[changed]</c>).</summary>
    public bool IsNewOrChanged => Tag is RequirementTag.New or RequirementTag.Changed;
}

/// <summary>
/// The requirements of a spec (<see cref="RequirementParser.Parse"/>): every <c>FR-n</c> requirement in document order,
/// the entries of the functional requirements section that look like requirements but have no ID (shortened text), and
/// whether a "Functional Requirements" section was found (when not, the whole document was scanned).
/// </summary>
public sealed record RequirementScan(
    IReadOnlyList<SpecRequirement> Requirements,
    IReadOnlyList<string> EntriesWithoutId,
    bool SectionFound)
{
    /// <summary>The distinct requirement IDs, in document order.</summary>
    public IReadOnlyList<string> Ids => [.. Requirements.Select(r => r.Id).Distinct(StringComparer.Ordinal)];

    /// <summary>The IDs tagged <c>[new]</c> or <c>[changed]</c> (the iteration's scope), in document order.</summary>
    public IReadOnlyList<string> NewOrChangedIds =>
        [.. Requirements.Where(r => r.IsNewOrChanged).Select(r => r.Id).Distinct(StringComparer.Ordinal)];
}
