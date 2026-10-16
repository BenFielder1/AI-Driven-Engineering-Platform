using Aide.Domain.Agents;
using Aide.Domain.Common;

namespace Aide.Domain.Artifacts;

/// <summary>
/// FR-70, §4.12: the <c>codebase_summary</c> artifact. The Repository Analyst writes it at the start of every release
/// version with existing code (every new version, an import's first version). It is agent-authored and auto-locked: it is
/// stored <c>locked</c> straight away (never a draft, no approval gate), is never restorable (FR-58), is produced in
/// refinement automatically, and approving other artifacts never invalidates it. It must have the eight sections of
/// <see cref="RequiredSections"/> (headings matched like spec sections, <see cref="SpecSections"/>).
/// </summary>
public static class CodebaseSummaryRules
{
    /// <summary>FR-70: the required sections, in order.</summary>
    public static IReadOnlyList<string> RequiredSections { get; } =
    [
        "Overview",
        "Stack & Dependencies",
        "Structure",
        "Key Modules",
        "Data & APIs",
        "Conventions",
        "Tests & CI",
        "Notable Risks",
    ];

    /// <summary>Whether a type is stored locked by its agent without an approval gate (only <c>codebase_summary</c>).</summary>
    public static bool IsAutoLocked(ArtifactType type) => type == ArtifactType.CodebaseSummary;

    /// <summary>The required sections missing from <paramref name="markdown"/>, in order.</summary>
    public static IReadOnlyList<string> Missing(string markdown) => SpecSections.Missing(markdown, RequiredSections);

    /// <summary><c>MISSING_SECTIONS</c> listing every missing section (the analyst gets one corrective re-run), or success.</summary>
    public static Result<string> Check(string markdown)
    {
        var missing = Missing(markdown);
        return missing.Count == 0
            ? Result.Success(markdown)
            : Result.Failure<string>(new DomainError(
                ErrorCodes.MissingSections,
                $"The codebase summary is missing {missing.Count} required section(s): {string.Join(", ", missing)}.")
            {
                Details = missing,
            });
    }

    /// <summary>The feedback for the analyst's corrective re-run: the missing sections, by their exact names.</summary>
    public static string CorrectiveFeedback(IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        return "Your previous codebase summary was rejected because it is missing these required sections: "
            + string.Join(", ", missing.Select(m => $"\"{m}\""))
            + ". Return the complete summary again with all eight sections as `##` headings with the exact titles: "
            + string.Join(", ", RequiredSections.Select(s => $"\"{s}\"")) + ".";
    }
}
