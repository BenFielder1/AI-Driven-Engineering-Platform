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

    /// <summary>The required sections that have no heading in <paramref name="markdown"/>, in the required order.</summary>
    public static IReadOnlyList<string> Missing(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var headings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inFence = false;
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence && HeadingPattern().Match(line) is { Success: true } match)
            {
                headings.Add(Normalise(match.Groups["title"].Value));
            }
        }

        return [.. Required.Where(name => !headings.Contains(Normalise(name)))];
    }

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

    private static string Normalise(string title)
    {
        var text = title.Replace("*", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
        text = LeadingNumberPattern().Replace(text.Trim(), string.Empty);
        return WhitespacePattern().Replace(text.Trim().Trim('_').Trim().TrimEnd(':').Trim(), " ");
    }

    [GeneratedRegex(@"^#{1,6}\s+(?<title>.+?)\s*#*\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^(?:section\s+)?\d+(?:\.\d+)*[.)]?\s+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LeadingNumberPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespacePattern();
}
