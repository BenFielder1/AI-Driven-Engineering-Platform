using System.Text.RegularExpressions;

namespace Aide.Domain.Stack;

/// <summary>
/// §4.16, D-V4-3: a skill's sections are its <c>## </c> headings (outside fenced code), matched after stripping one
/// trailing parenthetical: <c>## Modelling (BaaS)</c> is <c>Modelling</c>, <c>## Config as code (`netlify.toml`)</c> is
/// <c>Config as code</c>. Matching is exact (ordinal) after that, so <c>Gates</c> doesn't match <c>Gate</c>.
/// </summary>
public static partial class SkillSections
{
    /// <summary>The section names of <paramref name="markdown"/>, in order.</summary>
    public static IReadOnlyList<string> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var sections = new List<string>();
        var inFence = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                sections.Add(Normalise(line[3..]));
            }
        }

        return sections;
    }

    /// <summary>A heading as matched: trimmed, without one trailing parenthetical.</summary>
    public static string Normalise(string heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        return TrailingParenthetical().Replace(heading.Trim(), string.Empty).Trim();
    }

    /// <summary>The text of section <paramref name="section"/> (heading line included), or <c>null</c> when absent.</summary>
    public static string? Extract(string markdown, string section)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(section);
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inFence = false;
        int? start = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || !line.StartsWith("## ", StringComparison.Ordinal))
            {
                continue;
            }

            if (start is { } s)
            {
                return string.Join('\n', lines[s..i]).TrimEnd();
            }

            if (Normalise(line[3..]) == section)
            {
                start = i;
            }
        }

        return start is { } from ? string.Join('\n', lines[from..]).TrimEnd() : null;
    }

    [GeneratedRegex(@"\s*\([^()]*\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingParenthetical();
}

/// <summary>
/// §4.16: a pack condition on the composition (<c>specSections[].when</c>, <c>requiredEnv[].when</c>):
/// <c>&lt;fact&gt; == &lt;value&gt;</c> or <c>!=</c>, several joined by <c>&amp;&amp;</c>. Facts: <c>mode</c> (the pack's own
/// mode in the composition: the data mode, Supabase <c>host</c>, or the Next.js mode), <c>originModel</c>, and every
/// <see cref="StackFacts"/> fact.
/// </summary>
public static partial class StackWhen
{
    public const string ModeFact = "mode";

    /// <summary>Whether <paramref name="condition"/> is well formed; <paramref name="reason"/> says why not.</summary>
    public static bool IsValid(string condition, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(condition);
        foreach (var part in condition.Split("&&"))
        {
            var match = Comparison().Match(part);
            if (!match.Success)
            {
                reason = $"'{part.Trim()}' isn't '<fact> == <value>' or '<fact> != <value>'.";
                return false;
            }

            var fact = match.Groups["fact"].Value;
            if (fact is not (ModeFact or StackChoices.OriginModel) && !StackFacts.IsKnown(fact))
            {
                reason = $"'{fact}' isn't a known fact (mode, originModel, {string.Join(", ", StackChoices.Order.Where(c => c != StackChoices.OriginModel))}, {StackFacts.VcsHost}, {StackFacts.NextjsMode}).";
                return false;
            }
        }

        reason = null;
        return true;
    }

    /// <summary>Evaluates a valid condition; a fact missing from <paramref name="facts"/> counts as <c>none</c>.</summary>
    public static bool Holds(string? condition, IReadOnlyDictionary<string, string> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        foreach (var part in condition.Split("&&"))
        {
            var match = Comparison().Match(part);
            if (!match.Success)
            {
                return false;
            }

            var actual = facts.TryGetValue(match.Groups["fact"].Value, out var value) ? value : StackIds.None;
            var equal = actual == match.Groups["value"].Value;
            if (equal != (match.Groups["op"].Value == "=="))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(@"^\s*(?<fact>[A-Za-z]+)\s*(?<op>==|!=)\s*(?<value>[A-Za-z0-9._-]+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Comparison();
}
