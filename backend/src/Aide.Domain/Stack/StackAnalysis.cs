using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;

namespace Aide.Domain.Stack;

/// <summary>FR-128: a layer whose detected pack differs from the composition's (<see cref="Chosen"/> is <c>none</c> for None).</summary>
public sealed record StackMismatch(string Layer, string Detected, string Chosen, IReadOnlyList<string> Evidence);

/// <summary>FR-118, FR-128: what the stack detector found at a version's base commit, and how it differs from the composition.</summary>
public sealed record StackDetectionReport(
    IReadOnlyList<StackDetectedChoice> Suggestions,
    IReadOnlyList<StackDetectionConflict> Conflicts,
    IReadOnlyList<StackMismatch> Mismatches);

/// <summary>FR-128: what the codebase summary's "Stack &amp; Dependencies" section must name.</summary>
public sealed record StackSummaryExpectation(IReadOnlyList<string> DetectedPacks, IReadOnlyList<StackMismatch> Mismatches)
{
    public bool IsEmpty => DetectedPacks.Count == 0 && Mismatches.Count == 0;
}

/// <summary>
/// FR-128 (amends FR-70/72): the Repository Analyst and refinement get the composition and, for imports, the detector's
/// evidence. Pure rules:
/// <list type="bullet">
/// <item><see cref="Report"/>: the detector's result against the composition. A frontend, backend or data layer the detector
/// suggests one pack for, which differs from the composition's pack for that layer (None counts as a pack: <c>none</c>), is a
/// <see cref="StackMismatch"/>; conflicts and undetected layers never are (detection never blocks).</item>
/// <item><see cref="Check"/>: the summary's "Stack &amp; Dependencies" section names every detected frontend, backend and data
/// pack id and, when there are mismatches, says <c>mismatch</c> and names the composition's pack of each mismatched layer.
/// A failure is <c>MISSING_SECTIONS</c> (validated like the other required sections), with the one corrective re-run.</item>
/// </list>
/// A mismatch also makes the spec require "Stack Migration" (<see cref="StackGuidance.SpecSections"/>).
/// </summary>
public static class StackAnalysis
{
    /// <summary>The codebase summary section that names the stack (FR-70 §4.12).</summary>
    public const string StackSection = "Stack & Dependencies";

    private static readonly (string Choice, string Layer)[] PackChoices =
        [(StackChoices.Frontend, StackLayers.Frontend), (StackChoices.Backend, StackLayers.Backend), (StackChoices.Data, StackLayers.Data)];

    /// <summary>The detection report of <paramref name="detection"/> against <paramref name="composition"/>.</summary>
    public static StackDetectionReport Report(StackDetection detection, StackComposition composition)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(composition);
        var mismatches = new List<StackMismatch>();
        foreach (var (choice, layer) in PackChoices)
        {
            if (detection.Suggestion(choice) is not { } suggestion || suggestion.Option == StackIds.None)
            {
                continue;
            }

            var chosen = ChosenPack(composition, layer) ?? StackIds.None;
            if (!string.Equals(chosen, suggestion.Option, StringComparison.Ordinal))
            {
                mismatches.Add(new StackMismatch(layer, suggestion.Option, chosen, suggestion.Evidence));
            }
        }

        return new StackDetectionReport(detection.Suggestions, detection.Conflicts, mismatches);
    }

    /// <summary>What "Stack &amp; Dependencies" must name for <paramref name="report"/>.</summary>
    public static StackSummaryExpectation Expectation(StackDetectionReport report, StackComposition composition)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(composition);
        var detected = PackChoices
            .Select(c => report.Suggestions.FirstOrDefault(s => s.Choice == c.Choice)?.Option)
            .OfType<string>()
            .Where(o => o != StackIds.None)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new StackSummaryExpectation(detected, report.Mismatches);
    }

    /// <summary>
    /// FR-70 + FR-128: the eight sections (<see cref="CodebaseSummaryRules.Check"/>), then what "Stack &amp; Dependencies" must
    /// name. <c>MISSING_SECTIONS</c> with every problem in the details.
    /// </summary>
    public static Result<string> Check(string markdown, StackSummaryExpectation? expectation)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var sections = CodebaseSummaryRules.Check(markdown);
        if (sections.IsFailure || expectation is null || expectation.IsEmpty)
        {
            return sections;
        }

        var problems = Problems(SpecSections.Extract(markdown, StackSection) ?? string.Empty, expectation);
        return problems.Count == 0
            ? sections
            : Result.Failure<string>(new DomainError(
                ErrorCodes.MissingSections,
                $"The codebase summary's \"{StackSection}\" section is incomplete: {problems[0]}")
            {
                Details = problems,
            });
    }

    /// <summary>The corrective re-run's feedback: the missing sections (<see cref="CodebaseSummaryRules.CorrectiveFeedback"/>) or what the stack section lacks.</summary>
    public static string CorrectiveFeedback(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Message.StartsWith($"The codebase summary's \"{StackSection}\"", StringComparison.Ordinal)
            ? $"Your previous codebase summary was rejected: its \"{StackSection}\" section must {string.Join("; ", error.Details)}. Return the complete summary again with all eight sections."
            : CodebaseSummaryRules.CorrectiveFeedback(error.Details);
    }

    private static List<string> Problems(string section, StackSummaryExpectation expectation)
    {
        var problems = new List<string>();
        foreach (var pack in expectation.DetectedPacks.Where(p => !section.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add($"name the detected pack `{pack}`");
        }

        if (expectation.Mismatches.Count > 0)
        {
            if (!section.Contains("mismatch", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"flag the mismatch with the composition ({string.Join(", ", expectation.Mismatches.Select(m => $"{m.Layer}: `{m.Detected}` vs `{m.Chosen}`"))})");
            }

            // A layer the composition leaves at None has no pack to name ("none" would match any text); the mismatch flag covers it.
            foreach (var m in expectation.Mismatches.Where(m => m.Chosen != StackIds.None && !section.Contains(m.Chosen, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"name the composition's {m.Layer} pack `{m.Chosen}`");
            }
        }

        return problems;
    }

    private static string? ChosenPack(StackComposition composition, string layer) => layer switch
    {
        StackLayers.Frontend => composition.Frontend?.Pack,
        StackLayers.Backend => composition.Backend?.Pack,
        StackLayers.Data => composition.Data?.Pack,
        _ => null,
    };
}
