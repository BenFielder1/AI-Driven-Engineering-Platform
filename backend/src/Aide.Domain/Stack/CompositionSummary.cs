using System.Text.RegularExpressions;

namespace Aide.Domain.Stack;

/// <summary>
/// FR-121 step 2, FR-123, FR-128: renders <c>core/composition-summary.md</c>, the STACK.md equivalent, separately from the
/// prompt templates. Placeholders use the prompt syntax (<c>{{UPPER_SNAKE}}</c> required, <c>{{UPPER_SNAKE?}}</c> optional);
/// values are inserted literally in one pass. A required placeholder without a value renders <c>n/a</c> (so a catalog
/// typo shows up in prompts instead of failing runs; <see cref="Placeholders"/> lists the names the resolver fills).
/// Leading HTML comments (the provenance line and the template's own notes) are stripped.
/// </summary>
public static partial class CompositionSummary
{
    /// <summary>The placeholder names <see cref="StackResolver"/> fills.</summary>
    public static IReadOnlyList<string> Placeholders { get; } =
    [
        "STACK_CATALOG", "FRONTEND_PACK", "FRONTEND_PATH", "FRONTEND_NOTES", "BACKEND_PACK", "BACKEND_PATH", "BACKEND_NOTES",
        "DATA_PACK", "DATA_LOCATION", "DATA_MODE", "VCS_PACK", "CI_FILE", "FRONTEND_HOST", "BACKEND_HOST", "DATABASE_HOST",
        "LOCAL_DEV_LOOP", "NEXTJS_MODE", "ORIGIN_MODEL", "ORIGIN_MODEL_TEXT", "CONTRACT", "ENV_VARS", "GATES",
        "GENERATED_FILES", "SPECIALISTS", "CAVEATS", "STACK_CHANGE",
    ];

    /// <summary>The placeholder names used by <paramref name="template"/> (after its leading comments).</summary>
    public static IReadOnlyList<string> Used(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return [.. PlaceholderPattern().Matches(StripLeadingComments(template)).Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Renders <paramref name="template"/>: leading comments stripped, placeholders filled, runs of blank lines collapsed.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        var body = StripLeadingComments(template);
        var filled = PlaceholderPattern().Replace(body, m =>
            values.TryGetValue(m.Groups["name"].Value, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : m.Groups["optional"].Success ? string.Empty : "n/a");
        return BlankLines().Replace(filled, "\n\n").Trim();
    }

    /// <summary>
    /// The text without its leading HTML comments (each <c>&lt;!-- … --&gt;</c>, possibly multi-line) and surrounding blank
    /// lines, with <c>\n</c> line endings and no BOM: the provenance line of adapted files (§4.16) isn't prompt content.
    /// </summary>
    public static string StripLeadingComments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rest = text.TrimStart('﻿').ReplaceLineEndings("\n").TrimStart();
        while (rest.StartsWith("<!--", StringComparison.Ordinal))
        {
            var end = rest.IndexOf("-->", 4, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            rest = rest[(end + 3)..].TrimStart();
        }

        return rest;
    }

    [GeneratedRegex(@"\{\{(?<name>[A-Z][A-Z0-9_]*)(?<optional>\?)?\}\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"\n[ \t]*\n(?:[ \t]*\n)+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlankLines();
}
