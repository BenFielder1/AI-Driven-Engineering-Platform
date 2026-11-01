using Aide.Domain.Agents;
using Aide.Domain.Common;

namespace Aide.Domain.Specs;

/// <summary>
/// What a spec is validated against (§4.13, FR-75). <see cref="FirstVersion"/>: a project's first version from scratch
/// (v1's eleven sections, requirement IDs and criteria). <see cref="NewVersion"/>: an iteration with the previous
/// released version's locked spec as baseline. <see cref="ImportFirstVersion"/>: an import's first version (an
/// iteration without a baseline spec). <see cref="StackSections"/> holds the composition's (FR-123)
/// required extra sections; empty without a stack.
/// </summary>
public sealed record SpecValidationContext
{
    /// <summary>A new version or an import: "Changes in this version" and requirement tags are required.</summary>
    public bool IsIteration { get; init; }

    /// <summary>The first version of an imported project: <c>[changed]</c>/<c>[removed]</c> need no baseline.</summary>
    public bool IsFirstImportVersion { get; init; }

    /// <summary>The requirement IDs of the baseline (previous released) spec (<see cref="RequirementParser.Ids"/>).</summary>
    public IReadOnlyCollection<string> BaselineRequirementIds { get; init; } = [];

    /// <summary>The composition's required extra section titles (FR-123, §4.16 <c>specSections[].title</c>); empty without a stack.</summary>
    public IReadOnlyList<string> StackSections { get; init; } = [];

    public static SpecValidationContext FirstVersion { get; } = new();

    public static SpecValidationContext NewVersion(IEnumerable<string> baselineRequirementIds, IEnumerable<string>? stackSections = null)
    {
        ArgumentNullException.ThrowIfNull(baselineRequirementIds);
        return new()
        {
            IsIteration = true,
            BaselineRequirementIds = [.. baselineRequirementIds.Select(RequirementParser.NormaliseId).OfType<string>().Distinct(StringComparer.Ordinal)],
            StackSections = [.. stackSections ?? []],
        };
    }

    public static SpecValidationContext ImportFirstVersion(IEnumerable<string>? stackSections = null) => new()
    {
        IsIteration = true,
        IsFirstImportVersion = true,
        StackSections = [.. stackSections ?? []],
    };

    /// <summary>The sections the spec must have (<see cref="SpecSections.RequiredFor"/>).</summary>
    public IReadOnlyList<string> RequiredSections => SpecSections.RequiredFor(IsIteration, StackSections);

    /// <summary>
    /// The prompt's spec mode (§4.13): <c>first_version</c>, <c>new_version</c> (cumulative, tagged against the baseline) or
    /// <c>import_first_version</c> (cumulative, existing behaviour reverse-engineered as <c>[unchanged]</c>).
    /// </summary>
    public string Mode => !IsIteration ? FirstVersionMode : IsFirstImportVersion ? ImportFirstVersionMode : NewVersionMode;

    public const string FirstVersionMode = "first_version";
    public const string NewVersionMode = "new_version";
    public const string ImportFirstVersionMode = "import_first_version";

    /// <summary>
    /// The context of a release version's spec (§4.13): with the previous released version's locked spec
    /// (<paramref name="baselineSpecification"/>) a <see cref="NewVersion"/> against its requirement IDs; an iteration
    /// without one (an import's first version, or a version that builds on code AIDE never specified) an
    /// <see cref="ImportFirstVersion"/>; otherwise the <see cref="FirstVersion"/>.
    /// </summary>
    public static SpecValidationContext ForVersion(bool isIteration, string? baselineSpecification, IEnumerable<string>? stackSections = null)
    {
        if (!string.IsNullOrWhiteSpace(baselineSpecification))
        {
            return NewVersion(RequirementParser.Parse(baselineSpecification).Ids, stackSections);
        }

        if (isIteration)
        {
            return ImportFirstVersion(stackSections);
        }

        var sections = stackSections?.ToList() ?? [];
        return sections.Count == 0 ? FirstVersion : new SpecValidationContext { StackSections = sections };
    }
}

/// <summary>
/// The outcome of <see cref="SpecValidator.Validate"/>: every problem by kind, the requirements found, and — when
/// invalid — the <see cref="Error"/> to record (code by priority: <c>MISSING_SECTIONS</c>, then
/// <c>MISSING_REQUIREMENT_IDS</c>, then <c>INVALID_REQUIREMENT_TAGS</c>; its details list <i>every</i> problem of every
/// kind, so the one corrective re-run can fix them all).
/// </summary>
public sealed record SpecValidationResult(
    IReadOnlyList<string> MissingSections,
    IReadOnlyList<string> RequirementProblems,
    IReadOnlyList<string> TagProblems,
    RequirementScan Scan)
{
    public bool IsValid => MissingSections.Count == 0 && RequirementProblems.Count == 0 && TagProblems.Count == 0;

    public IReadOnlyList<SpecRequirement> Requirements => Scan.Requirements;

    public DomainError? Error
    {
        get
        {
            if (IsValid)
            {
                return null;
            }

            var code = MissingSections.Count > 0 ? ErrorCodes.MissingSections
                : RequirementProblems.Count > 0 ? ErrorCodes.MissingRequirementIds
                : ErrorCodes.InvalidRequirementTags;
            IReadOnlyList<string> details =
            [
                .. MissingSections.Select(s => $"Missing section: \"{s}\"."),
                .. RequirementProblems,
                .. TagProblems,
            ];
            var head = code switch
            {
                ErrorCodes.MissingSections => $"The specification is missing {MissingSections.Count} required section(s): {string.Join(", ", MissingSections)}.",
                ErrorCodes.MissingRequirementIds => $"The specification has {RequirementProblems.Count} functional requirement problem(s): {RequirementProblems[0]}",
                _ => $"The specification has {TagProblems.Count} requirement tag problem(s): {TagProblems[0]}",
            };
            var more = details.Count - (code == ErrorCodes.MissingSections ? MissingSections.Count : 1);
            return new DomainError(code, more > 0 ? $"{head} (+{more} more)" : head) { Details = details };
        }
    }
}

/// <summary>
/// FR-28 as amended by FR-75 and §4.13: validates a generated (or human-edited) technical specification.
/// <list type="number">
/// <item>Required sections (<see cref="SpecValidationContext.RequiredSections"/>) → <c>MISSING_SECTIONS</c>.</item>
/// <item>Every functional requirement has an <c>FR-n</c> ID (once) and a Given/When/Then acceptance criterion, and there
/// is at least one → <c>MISSING_REQUIREMENT_IDS</c> (D-V2-13: every spec, preset or not). A <c>[removed]</c> requirement
/// needs no criterion.</item>
/// <item>Iterations only: each requirement line carries exactly one tag; at least one <c>[new]</c> or <c>[changed]</c>; every
/// <c>[changed]</c>/<c>[removed]</c> ID is in the baseline spec, except on an import's first version →
/// <c>INVALID_REQUIREMENT_TAGS</c>.</item>
/// </list>
/// Each failure gets the same one corrective re-run as v1's <c>MISSING_SECTIONS</c> (<see cref="CorrectiveFeedback"/>).
/// </summary>
public static class SpecValidator
{
    public static SpecValidationResult Validate(string markdown, SpecValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(context);
        var missing = SpecSections.Missing(markdown, context.RequiredSections);
        var scan = RequirementParser.Parse(markdown);
        return new SpecValidationResult(missing, RequirementProblems(scan), context.IsIteration ? TagProblems(scan, context) : [], scan);
    }

    /// <summary>
    /// <see cref="Validate"/> as a result for the spec job: the spec text on success, the <see cref="SpecValidationResult.Error"/>
    /// otherwise.
    /// </summary>
    public static Result<string> Check(string markdown, SpecValidationContext context)
    {
        var result = Validate(markdown, context);
        return result.Error is { } error ? Result.Failure<string>(error) : Result.Success(markdown);
    }

    /// <summary>The feedback for the corrective re-run: every problem, then the rules the spec must follow.</summary>
    public static string CorrectiveFeedback(DomainError error, SpecValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(context);
        var reasons = error.Details.Count > 0 ? error.Details : [error.Message];
        var rules = new List<string>
        {
            "Include every required section as a `##` heading with the exact title: "
                + string.Join(", ", context.RequiredSections.Select(s => $"\"{s}\"")) + ".",
            "Under \"Functional Requirements\", start every requirement's heading or list line with its ID `FR-n` "
                + "(e.g. `### FR-3 Export to CSV`), each ID once, and give each at least one acceptance criterion written as "
                + "\"Given …, when …, then …\".",
        };
        if (context.IsIteration)
        {
            rules.Add("Tag every requirement's heading line with exactly one of [new], [changed], [unchanged] or [removed]; "
                + "at least one requirement must be [new] or [changed]"
                + (context.IsFirstImportVersion
                    ? "; existing behaviour reverse-engineered from the codebase is [unchanged]."
                    : "; [changed] and [removed] may only use IDs from the previous version's specification."));
        }

        return $"Your previous specification was rejected with {error.Code} for these reasons:\n"
            + string.Join("\n", reasons.Select(r => "- " + r))
            + "\nReturn the complete specification again, fixing all of them:\n"
            + string.Join("\n", rules.Select(r => "- " + r));
    }

    private static List<string> RequirementProblems(RequirementScan scan)
    {
        var problems = new List<string>();
        if (scan.Requirements.Count == 0)
        {
            problems.Add($"No functional requirements with an FR-n ID were found{(scan.SectionFound ? " under \"Functional Requirements\"" : string.Empty)}.");
        }

        problems.AddRange(scan.EntriesWithoutId.Select(e => $"Requirement without an FR-n ID: \"{e}\"."));
        problems.AddRange(scan.Requirements
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} is used by {g.Count()} requirements; each ID must be unique."));
        problems.AddRange(scan.Requirements
            .Where(r => !r.HasAcceptanceCriterion && r.Tag != RequirementTag.Removed)
            .Select(r => $"{r.Id} has no Given/When/Then acceptance criterion."));
        return problems;
    }

    private static List<string> TagProblems(RequirementScan scan, SpecValidationContext context)
    {
        var problems = new List<string>();
        foreach (var requirement in scan.Requirements)
        {
            if (requirement.Tags.Count == 0)
            {
                problems.Add($"{requirement.Id} has no tag; tag its line with exactly one of [new], [changed], [unchanged] or [removed].");
            }
            else if (requirement.Tags.Count > 1)
            {
                problems.Add($"{requirement.Id} has {requirement.Tags.Count} tags ({string.Join(" ", requirement.Tags.Select(t => $"[{t.ToWire()}]"))}); use exactly one.");
            }
        }

        // Counted on any tag of the line, so a doubly tagged [new] [changed] line reports only its double tag.
        if (scan.Requirements.Count > 0 && !scan.Requirements.Any(r => r.Tags.Any(t => t is RequirementTag.New or RequirementTag.Changed)))
        {
            problems.Add("No requirement is tagged [new] or [changed]; a new version must add or change at least one.");
        }

        if (!context.IsFirstImportVersion)
        {
            var baseline = context.BaselineRequirementIds.ToHashSet(StringComparer.Ordinal);
            problems.AddRange(scan.Requirements
                .Where(r => (r.Tag is RequirementTag.Changed or RequirementTag.Removed) && !baseline.Contains(r.Id))
                .Select(r => $"{r.Id} is tagged [{r.Tag!.Value.ToWire()}] but isn't in the previous version's specification; use [new] for a new requirement."));
        }

        return problems;
    }
}
