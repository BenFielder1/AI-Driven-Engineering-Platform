using System.Globalization;
using System.Text.RegularExpressions;

namespace Aide.IntegrationTests.Characterisation;

/// <summary>
/// §4.58 normalisation of the strings in a characterisation snapshot (FR-260). Run-specific values are replaced so that the
/// same scenario always gives the same bytes:
/// <list type="bullet">
/// <item>known literals first (the project slug, test folders, user emails), longest first;</item>
/// <item>ids (GUIDs, with or without dashes) become <c>&lt;kind&gt;#n</c>: ids registered with a kind are numbered per kind in
/// registration order (creation order), any other id in first-seen order as <c>id#n</c>; users get fixed role labels;</item>
/// <item>git commit SHAs (40 hex) become <c>sha#n</c> in first-seen order, and their short forms (7–12 hex, e.g. in a Fake
/// deployment URL) <c>sha#n:k</c>; other short hex tokens with a digit and a letter become <c>hex#n</c>;</item>
/// <item>ISO 8601 timestamps and dates become <c>&lt;timestamp&gt;</c> and <c>&lt;date&gt;</c>.</item>
/// </list>
/// SHA-256 content hashes (64 hex) are never touched.
/// </summary>
internal sealed partial class SnapshotNormaliser
{
    private readonly Dictionary<Guid, string> _kinds = [];
    private readonly Dictionary<Guid, string> _labels = [];
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _shaLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _hexLabels = new(StringComparer.Ordinal);
    private readonly List<string> _knownShas = [];
    private readonly List<KeyValuePair<string, string>> _literals = [];

    /// <summary>Replaces every occurrence of <paramref name="value"/> with <paramref name="replacement"/> (before any pattern).</summary>
    public void AddLiteral(string? value, string replacement)
    {
        if (string.IsNullOrEmpty(value) || _literals.Exists(l => string.Equals(l.Key, value, StringComparison.Ordinal)))
        {
            return;
        }

        _literals.Add(new KeyValuePair<string, string>(value, replacement));
        _literals.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
    }

    /// <summary>A fixed label for an id (e.g. <c>user:owner</c>).</summary>
    public void SetLabel(Guid id, string label) => _labels[id] = label;

    /// <summary>The kind an id is numbered as; the first kind registered for an id wins.</summary>
    public void SetKind(Guid id, string kind) => _kinds.TryAdd(id, kind);

    /// <summary>The id's label, numbering it now if it has none yet.</summary>
    public string Label(Guid id)
    {
        if (_labels.TryGetValue(id, out var label))
        {
            return label;
        }

        label = Next(_kinds.GetValueOrDefault(id, "id"));
        _labels[id] = label;
        return label;
    }

    /// <summary>Remembers the full commit SHAs in <paramref name="text"/>, so their short forms can be recognised anywhere.</summary>
    public void CollectShas(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (Match match in Sha().Matches(text))
        {
            if (!_knownShas.Contains(match.Value, StringComparer.Ordinal))
            {
                _knownShas.Add(match.Value);
            }
        }
    }

    public string Normalise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var (value, replacement) in _literals)
        {
            text = text.Replace(value, replacement, StringComparison.Ordinal);
        }

        text = GuidWithDashes().Replace(text, m => Label(Guid.Parse(m.Value, CultureInfo.InvariantCulture)));
        text = Sha().Replace(text, m => ShaLabel(m.Value));
        text = GuidWithoutDashes().Replace(text, m => Label(Guid.ParseExact(m.Value, "N")));
        text = Timestamp().Replace(text, "<timestamp>");
        text = Date().Replace(text, "<date>");
        text = ShortHex().Replace(text, m => ShortLabel(m.Value));
        return text;
    }

    /// <summary>
    /// <paramref name="text"/> with every run-specific value blanked (no labels assigned): a stable sort key for entries
    /// written in one transaction, which share a creation time and whose v7 ids are random within a millisecond.
    /// </summary>
    public static string Blind(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = GuidWithDashes().Replace(text, "*");
        text = Sha().Replace(text, "*");
        text = GuidWithoutDashes().Replace(text, "*");
        text = Timestamp().Replace(text, "*");
        text = Date().Replace(text, "*");
        return ShortHex().Replace(text, "*");
    }

    private string Next(string kind)
    {
        var n = _counters.GetValueOrDefault(kind) + 1;
        _counters[kind] = n;
        return string.Create(CultureInfo.InvariantCulture, $"{kind}#{n}");
    }

    private string ShaLabel(string sha)
    {
        if (!_shaLabels.TryGetValue(sha, out var label))
        {
            label = Next("sha");
            _shaLabels[sha] = label;
        }

        return label;
    }

    private string ShortLabel(string hex)
    {
        var full = _knownShas.Find(s => s.StartsWith(hex, StringComparison.Ordinal));
        if (full is not null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{ShaLabel(full)}:{hex.Length}");
        }

        if (!_hexLabels.TryGetValue(hex, out var label))
        {
            label = Next("hex");
            _hexLabels[hex] = label;
        }

        return label;
    }

    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GuidWithDashes();

    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9a-f]{40}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Sha();

    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9a-f]{32}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GuidWithoutDashes();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"(?<!\d)\d{4}-\d{2}-\d{2}(?!\d)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Date();

    [GeneratedRegex("(?<![0-9A-Za-z])(?=[0-9a-f]*[0-9])(?=[0-9a-f]*[a-f])[0-9a-f]{7,12}(?![0-9A-Za-z])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ShortHex();
}
