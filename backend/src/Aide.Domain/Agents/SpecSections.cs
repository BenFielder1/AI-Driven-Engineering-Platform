using System.Text.RegularExpressions;
using Aide.Domain.Common;

namespace Aide.Domain.Agents;

/// <summary>
/// FR-28: the eleven sections every technical specification must have. A section is present when a Markdown heading
/// (any level, outside code fences) has its name, ignoring case, surrounding whitespace, emphasis markers and a leading
/// number such as <c>1.</c> or <c>10)</c>.
/// </summary>
public static partial class SpecSections
{
    /// <summary>The required section names, in order (FR-28).</summary>
    public static IReadOnlyList<string> Required { get; } =
    [
        "Overview",
        "Functional Requirements",
        "Technical Architecture",
        "Data Models",
        "API Contracts",
        "Feature Specifications",
        "Design System & UI/UX",
        "Security & Authentication",
        "Testing Strategy",
        "Deployment & Infrastructure",
        "Open Questions",
    ];

    /// <summary>§4.13: the section every iteration spec (a new version, or an import's first version) adds.</summary>
    public const string ChangesInThisVersion = "Changes in this version";

    /// <summary>
    /// §4.13 required sections: v1's eleven (<see cref="Required"/>); then a preset's extra sections
    /// (<paramref name="presetSections"/>, phase 8; empty without a preset); then <see cref="ChangesInThisVersion"/> for an
    /// iteration. Duplicates (ignoring case and formatting) are listed once.
    /// </summary>
    public static IReadOnlyList<string> RequiredFor(bool isIteration, IEnumerable<string>? presetSections = null)
    {
        var result = new List<string>(Required);
        var seen = new HashSet<string>(Required.Select(Normalise), StringComparer.OrdinalIgnoreCase);
        foreach (var extra in presetSections ?? [])
        {
            if (!string.IsNullOrWhiteSpace(extra) && seen.Add(Normalise(extra)))
            {
                result.Add(extra.Trim());
            }
        }

        if (isIteration && seen.Add(Normalise(ChangesInThisVersion)))
        {
            result.Add(ChangesInThisVersion);
        }

        return result;
    }

    /// <summary>The required sections that have no heading in <paramref name="markdown"/>, in the required order.</summary>
    public static IReadOnlyList<string> Missing(string markdown) => Missing(markdown, Required);

    /// <summary>The sections of <paramref name="required"/> that have no heading in <paramref name="markdown"/>, in order.</summary>
    public static IReadOnlyList<string> Missing(string markdown, IReadOnlyList<string> required)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(required);
        var headings = new HashSet<string>(Headings(markdown).Select(h => Normalise(h.Title)), StringComparer.OrdinalIgnoreCase);
        return [.. required.Where(name => !headings.Contains(Normalise(name)))];
    }

    /// <summary>A Markdown heading outside code fences: its level (1–6), raw title text and 0-based line index.</summary>
    internal readonly record struct Heading(int Level, string Title, int Line);

    /// <summary>Every heading outside code fences, in document order.</summary>
    internal static IEnumerable<Heading> Headings(string markdown)
    {
        var inFence = false;
        var index = 0;
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (IsFence(line))
            {
                inFence = !inFence;
            }
            else if (!inFence && HeadingPattern().Match(line) is { Success: true } match)
            {
                yield return new Heading(match.Groups["hashes"].Length, match.Groups["title"].Value, index);
            }

            index++;
        }
    }

    internal static bool IsFence(string trimmedLine) =>
        trimmedLine.StartsWith("```", StringComparison.Ordinal) || trimmedLine.StartsWith("~~~", StringComparison.Ordinal);

    /// <summary>Whether two section titles name the same section (ignoring case, emphasis, a leading number and a trailing colon).</summary>
    internal static bool SameTitle(string left, string right) =>
        string.Equals(Normalise(left), Normalise(right), StringComparison.OrdinalIgnoreCase);

    /// <summary><c>MISSING_SECTIONS</c> listing every missing section, or success.</summary>
    public static Result Check(string markdown)
    {
        var missing = Missing(markdown);
        return missing.Count == 0
            ? Result.Success()
            : Result.Failure(new DomainError(
                ErrorCodes.MissingSections,
                $"The specification is missing {missing.Count} required section(s): {string.Join(", ", missing)}.")
            {
                Details = missing,
            });
    }

    /// <summary>The feedback for the corrective re-run (FR-28): the missing sections, by their exact names.</summary>
    public static string CorrectiveFeedback(IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        return "Your previous specification was rejected because it is missing these required sections: "
            + string.Join(", ", missing.Select(m => $"\"{m}\""))
            + ". Return the complete specification again with all eleven sections as `##` headings with the exact titles.";
    }

    internal static string Normalise(string title)
    {
        var text = title.Replace("*", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
        text = LeadingNumberPattern().Replace(text.Trim(), string.Empty);
        return WhitespacePattern().Replace(text.Trim().Trim('_').Trim().TrimEnd(':').Trim(), " ");
    }

    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<title>.+?)\s*#*\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^(?:section\s+)?\d+(?:\.\d+)*[.)]?\s+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    internal static partial Regex LeadingNumberPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespacePattern();
}
