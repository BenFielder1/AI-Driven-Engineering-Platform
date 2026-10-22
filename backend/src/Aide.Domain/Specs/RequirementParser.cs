using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aide.Domain.Agents;
using Aide.Domain.Common;

namespace Aide.Domain.Specs;

/// <summary>
/// FR-75, §4.13: finds the functional requirements of a spec's "Functional Requirements" section (the whole document
/// when the section is missing). Text inside code fences counts as body text, never as structure.
/// <list type="bullet">
/// <item>A <b>requirement line</b> is a heading, a top-level list item or a paragraph line whose text <i>starts</i> with
/// <c>FR-n</c> (after a heading number, emphasis or tags), e.g. <c>### FR-3 [changed] Export</c>,
/// <c>- **FR-3** [new] Export</c>, <c>**FR-3** [unchanged]: Export</c>. Tags anywhere on that line count (§4.13).</item>
/// <item>Its <b>body</b> runs to the next requirement line; for a heading requirement also to the next heading of the same
/// or a higher level (deeper headings such as "Acceptance criteria" are body); for a list requirement also to any heading
/// or the next top-level list item that isn't a criterion (<c>Given/When/Then/And/But/Acceptance…</c>); for a paragraph
/// requirement also to any heading. Nested list items are body.</item>
/// <item>An <b>acceptance criterion</b> is "given … when … then …" (whole words, any case, in that order, may span lines)
/// in the requirement line or body.</item>
/// <item>An <b>entry without an ID</b> is a top-level list item outside any requirement body, or a sub-heading with
/// paragraph text but no requirement and no list items under it.</item>
/// </list>
/// </summary>
public static partial class RequirementParser
{
    /// <summary>The section that holds the functional requirements (v1 FR-28).</summary>
    public const string SectionTitle = "Functional Requirements";

    /// <summary>Entry text is shortened to this many characters in problems.</summary>
    public const int MaxEntryLength = 80;

    public static RequirementScan Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var (start, end, found) = FindSection(markdown, lines.Length);
        var scanner = new Scanner();
        for (var i = start; i < end; i++)
        {
            scanner.Line(lines[i], i);
        }

        scanner.Finish();
        return new RequirementScan(scanner.Requirements, scanner.WithoutId, found);
    }

    /// <summary>The requirement IDs of <paramref name="markdown"/> (e.g. the baseline spec's, for §4.13 <c>[changed]</c> checks).</summary>
    public static IReadOnlyList<string> Ids(string markdown) => Parse(markdown).Ids;

    /// <summary>Normalises a requirement ID (<c>fr-07</c> → <c>FR-7</c>); <c>null</c> when it isn't one.</summary>
    public static string? NormaliseId(string? text)
    {
        if (text is null || IdOnlyPattern().Match(text.Trim()) is not { Success: true } match
            || !int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        return "FR-" + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The ID a line of text starts with (e.g. a missing-feature line <c>FR-3 Export: not found</c>), or <c>null</c>.</summary>
    public static string? LeadingId(string? text)
    {
        if (text is null || LeadingIdPattern().Match(text) is not { Success: true } match
            || !int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        return "FR-" + number.ToString(CultureInfo.InvariantCulture);
    }

    private static (int Start, int End, bool Found) FindSection(string markdown, int lineCount)
    {
        var headings = SpecSections.Headings(markdown).ToList();
        var index = headings.FindIndex(h => SpecSections.SameTitle(h.Title, SectionTitle));
        if (index < 0)
        {
            return (0, lineCount, false);
        }

        var section = headings[index];
        var next = headings.FindIndex(index + 1, h => h.Level <= section.Level);
        return (section.Line + 1, next < 0 ? lineCount : headings[next].Line, true);
    }

    private static bool TryRequirement(string text, out int number, out string title, out IReadOnlyList<RequirementTag> tags)
    {
        number = 0;
        title = string.Empty;
        tags = [];
        var stripped = SpecSections.LeadingNumberPattern().Replace(text.Trim(), string.Empty);
        var match = RequirementStartPattern().Match(stripped);
        if (!match.Success || !int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        tags = [.. TagPattern().Matches(stripped).Select(m => EnumStrings.Parse<RequirementTag>(m.Groups["tag"].Value.ToLowerInvariant()))];
        var rest = TagPattern().Replace(stripped[(match.Index + match.Length)..], string.Empty);
        title = Clean(rest).Trim(':', '-', '–', '—', '.', ' ', '\t');
        return true;
    }

    private static string Clean(string text) =>
        WhitespacePattern().Replace(text.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal), " ").Trim(' ', '*', '_');

    private static string Shorten(string text)
    {
        var clean = Clean(text);
        return clean.Length > MaxEntryLength ? clean[..MaxEntryLength] + "…" : clean;
    }

    private enum Kind
    {
        Heading,
        List,
        Paragraph,
    }

    private sealed class Open(Kind kind, int number, string title, IReadOnlyList<RequirementTag> tags, int level, int indent, int line, string text)
    {
        public Kind Kind { get; } = kind;

        public int Number { get; } = number;

        public string Title { get; } = title;

        public IReadOnlyList<RequirementTag> Tags { get; } = tags;

        public int Level { get; } = level;

        public int Indent { get; } = indent;

        public int Line { get; } = line;

        public StringBuilder Body { get; } = new StringBuilder(text).Append('\n');
    }

    private sealed class Group(string title, int level)
    {
        public string Title { get; } = title;

        public int Level { get; } = level;

        public bool HasRequirement { get; set; }

        public bool HasParagraph { get; set; }

        public bool HasListItem { get; set; }
    }

    private sealed class Scanner
    {
        private readonly List<Group> _groups = [];
        private Open? _current;
        private bool _inFence;

        public List<SpecRequirement> Requirements { get; } = [];

        public List<string> WithoutId { get; } = [];

        public void Line(string raw, int index)
        {
            var trimmed = raw.Trim();
            if (SpecSections.IsFence(trimmed))
            {
                _inFence = !_inFence;
                _current?.Body.Append(raw).Append('\n');
                return;
            }

            if (_inFence || trimmed.Length == 0)
            {
                _current?.Body.Append(raw).Append('\n');
                return;
            }

            if (HeadingPattern().Match(trimmed) is { Success: true } heading)
            {
                OnHeading(heading.Groups["hashes"].Length, heading.Groups["title"].Value, index);
                return;
            }

            if (ListItemPattern().Match(raw) is { Success: true } item)
            {
                OnListItem(Indent(item.Groups["indent"].Value), item.Groups["text"].Value, raw, index);
                return;
            }

            OnParagraph(trimmed, raw, index);
        }

        public void Finish()
        {
            Close();
            while (_groups.Count > 0)
            {
                PopGroup();
            }
        }

        private void OnHeading(int level, string title, int index)
        {
            var isRequirement = TryRequirement(title, out var number, out var clean, out var tags);
            if (_current is { Kind: Kind.Heading } open && level > open.Level && !isRequirement)
            {
                open.Body.Append(title).Append('\n');
                return;
            }

            Close();
            while (_groups.Count > 0 && _groups[^1].Level >= level)
            {
                PopGroup();
            }

            if (isRequirement)
            {
                MarkRequirement();
                _current = new Open(Kind.Heading, number, clean, tags, level, 0, index, title);
            }
            else
            {
                _groups.Add(new Group(title, level));
            }
        }

        private void OnListItem(int indent, string text, string raw, int index)
        {
            var topLevel = indent <= 1;
            if (topLevel && TryRequirement(text, out var number, out var title, out var tags))
            {
                Close();
                MarkRequirement();
                _current = new Open(Kind.List, number, title, tags, 0, indent, index, text);
                return;
            }

            if (_current is { } open)
            {
                if (open.Kind != Kind.List || indent > open.Indent || IsCriterion(text))
                {
                    open.Body.Append(raw).Append('\n');
                    return;
                }

                Close();
            }

            if (!topLevel || IsCriterion(text))
            {
                return;
            }

            WithoutId.Add(Shorten(text));
            if (_groups.Count > 0)
            {
                _groups[^1].HasListItem = true;
            }
        }

        private void OnParagraph(string trimmed, string raw, int index)
        {
            if (TryRequirement(trimmed, out var number, out var title, out var tags))
            {
                Close();
                MarkRequirement();
                _current = new Open(Kind.Paragraph, number, title, tags, 0, 0, index, trimmed);
                return;
            }

            if (_current is { } open)
            {
                open.Body.Append(raw).Append('\n');
                return;
            }

            if (_groups.Count > 0)
            {
                _groups[^1].HasParagraph = true;
            }
        }

        private void MarkRequirement()
        {
            foreach (var group in _groups)
            {
                group.HasRequirement = true;
            }
        }

        private void PopGroup()
        {
            var group = _groups[^1];
            _groups.RemoveAt(_groups.Count - 1);
            if (!group.HasRequirement && group.HasParagraph && !group.HasListItem)
            {
                WithoutId.Add(Shorten(group.Title));
            }
        }

        private void Close()
        {
            if (_current is not { } open)
            {
                return;
            }

            Requirements.Add(new SpecRequirement(
                "FR-" + open.Number.ToString(CultureInfo.InvariantCulture),
                open.Number,
                open.Title,
                open.Tags,
                AcceptanceCriterionPattern().IsMatch(open.Body.ToString()),
                open.Line + 1));
            _current = null;
        }

        private static bool IsCriterion(string text) => CriterionStartPattern().IsMatch(text);

        private static int Indent(string whitespace) => whitespace.Sum(c => c == '\t' ? 4 : 1);
    }

    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<title>.+?)\s*#*\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?:[-*+]|\d{1,3}[.)])[ \t]+(?<text>.*\S)\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ListItemPattern();

    // The line starts with the ID, after emphasis, brackets or tags: "**FR-3**", "[new] FR-3", "`FR-3`", "Requirement FR-3".
    [GeneratedRegex(@"^(?:[\s*_`~>]|\[(?:new|changed|unchanged|removed)\])*(?:requirement\s+)?\[?FR-(?<n>\d{1,9})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RequirementStartPattern();

    [GeneratedRegex(@"^FR-(?<n>\d{1,9})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdOnlyPattern();

    [GeneratedRegex(@"^[\s*_`\[]*FR-(?<n>\d{1,9})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LeadingIdPattern();

    [GeneratedRegex(@"\[(?<tag>new|changed|unchanged|removed)\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\bgiven\b.*?\bwhen\b.*?\bthen\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AcceptanceCriterionPattern();

    [GeneratedRegex(@"^[\s*_`]*(?:given|when|then|and|but|acceptance|scenario)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CriterionStartPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespacePattern();
}
