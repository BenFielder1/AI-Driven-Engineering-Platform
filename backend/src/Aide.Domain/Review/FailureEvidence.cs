using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aide.Domain.Execution;

namespace Aide.Domain.Review;

/// <summary>FR-236: a CI annotation on a failed check (GitHub check-run annotations): where and what.</summary>
public sealed record FailureAnnotation(string Path, int? Line, string? Level, string Message);

/// <summary>
/// FR-236: what a Git host told about a failed required check besides its name and URL: the check output's title and
/// summary, its annotations and the job log (GitHub Actions job log, GitLab job trace, Bitbucket Pipelines step log; already
/// redacted once and cut to the download cap). Every part is optional.
/// </summary>
public sealed record CheckFailureDetail(string? Title, string? Summary, IReadOnlyList<FailureAnnotation> Annotations, string? Log)
{
    public static CheckFailureDetail Empty { get; } = new(null, null, [], null);

    /// <summary>Whether the host gave anything beyond the name and URL.</summary>
    public bool HasContent => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Summary) || Annotations.Count > 0 || !string.IsNullOrWhiteSpace(Log);
}

/// <summary>
/// FR-237, §4.37: the stable failure id of a failure finding, <c>failure:&lt;source&gt;:&lt;name&gt;</c> (for example
/// <c>failure:preview:vercel</c> or <c>failure:check:build</c>). The name is lowercased and every run of characters other
/// than letters, digits, <c>.</c>, <c>_</c> and <c>-</c> becomes <c>-</c> (<c>ci / test</c> → <c>ci-test</c>). Pure.
/// </summary>
public static partial class FailureIds
{
    public const string Prefix = "failure:";

    /// <summary>A preview or deploy failure (FR-147).</summary>
    public const string Preview = "preview";

    /// <summary>A failed CI check (FR-85, FR-139).</summary>
    public const string Check = "check";

    /// <summary>The longest name kept in an id.</summary>
    public const int MaxNameLength = 80;

    public static string Of(string source, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentNullException.ThrowIfNull(name);
        return Prefix + source + ":" + Slug(name);
    }

    /// <summary>The id's name part, normalised.</summary>
    public static string Slug(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var slug = NonNameRun().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > MaxNameLength)
        {
            slug = slug[..MaxNameLength].TrimEnd('-');
        }

        return slug.Length == 0 ? "unnamed" : slug;
    }

    /// <summary>The source part of <paramref name="id"/> (<c>preview</c>, <c>check</c>), or <c>null</c> when it isn't a failure id.</summary>
    public static string? SourceOf(string? id)
    {
        if (id is null || !id.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = id.Split(':');
        return parts.Length >= 3 && parts[1].Length > 0 ? parts[1] : null;
    }

    /// <summary>How the source reads in the prompt and the evidence block.</summary>
    public static string SourceLabel(string id) => SourceOf(id) switch
    {
        Preview => "preview deployment",
        Check => "CI check",
        { } other => other,
        null => "unknown",
    };

    /// <summary>
    /// Whether <paramref name="text"/> names <paramref name="id"/> as a whole token (ignoring case):
    /// <c>failure:check:build</c> is not named by <c>failure:check:build-docker</c>.
    /// </summary>
    public static bool IsNamedIn(string? text, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var start = 0;
        while ((start = text.IndexOf(id, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = start + id.Length;
            var before = start == 0 || !IsIdChar(text[start - 1]) && text[start - 1] != ':';
            var after = end >= text.Length || !IsIdChar(text[end]);
            if (before && after)
            {
                return true;
            }

            start = end;
        }

        return false;
    }

    /// <summary>
    /// FR-237: the findings with every failure id unique: a second (third, …) failure with the same id, in list order, gets
    /// <c>-2</c> (<c>-3</c>, …), so no failure's evidence is lost to another's.
    /// </summary>
    public static IReadOnlyList<ReviewFinding> Disambiguate(IReadOnlyList<ReviewFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ReviewFinding>(findings.Count);
        foreach (var finding in findings)
        {
            if (finding.FailureId is not { } id || used.Add(id))
            {
                result.Add(finding);
                continue;
            }

            var n = 2;
            while (!used.Add(string.Create(CultureInfo.InvariantCulture, $"{id}-{n}")))
            {
                n++;
            }

            result.Add(finding with { FailureId = string.Create(CultureInfo.InvariantCulture, $"{id}-{n}") });
        }

        return result;
    }

    private static bool IsIdChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';

    [GeneratedRegex("[^a-z0-9._-]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonNameRun();
}

/// <summary>
/// FR-236, §4.38: the failure excerpt given to the review agent and attached to fix steps. Built from the stored,
/// already-redacted log:
/// <list type="number">
/// <item><see cref="SecretScanner.Redact"/> runs again (NFR: every excerpt is redacted twice).</item>
/// <item>ANSI escape codes are stripped.</item>
/// <item>The 40 lines around the first error line (10 before it, 30 from it) plus the last 20 lines, joined by
/// <c>… N lines omitted</c> when they don't overlap. With no error line, the last 60 lines.</item>
/// <item>Up to 10 annotations as <c>path:line level message</c>.</item>
/// <item>At most 6,000 characters per failure (<see cref="MaxPerFailure"/>) and 18,000 across all failures in one prompt
/// (<see cref="CapAll"/>): over the total, failures keep their excerpt in severity order and the rest are cut to their
/// first error line.</item>
/// </list>
/// The first error line is the first line matching <c>error|Error|ERR!|failed|FAILED|Cannot|Can't|not found</c>
/// (case-sensitive as written). Pure.
/// </summary>
public static partial class FailureExcerpt
{
    public const int MaxPerFailure = 6000;

    public const int MaxTotal = 18_000;

    public const int LinesBeforeError = 10;

    public const int LinesFromError = 30;

    public const int TailLines = 20;

    public const int NoErrorLines = 60;

    public const int MaxAnnotations = 10;

    /// <summary>A longer log line (a minified bundle, a progress bar) is cut to this many characters.</summary>
    public const int MaxLineLength = 500;

    /// <summary>The longest check summary kept in an excerpt.</summary>
    public const int MaxSummaryLength = 1000;

    /// <summary>The note that joins or starts a cut excerpt.</summary>
    public static string Omitted(int lines) => string.Create(CultureInfo.InvariantCulture, $"… {lines} lines omitted");

    /// <summary>
    /// The excerpt of a failure: the <paramref name="header"/> lines (e.g. the check name and URL, the check output's title
    /// and summary), then up to 10 annotations, then the log excerpt; capped at <see cref="MaxPerFailure"/>.
    /// </summary>
    public static string Build(string? log, IReadOnlyList<FailureAnnotation>? annotations = null, IReadOnlyList<string?>? header = null) =>
        Compose(log, annotations, header).Text;

    /// <summary>
    /// <see cref="Build"/> with the excerpt's first error line taken from the annotation and log lines only (never the header:
    /// a check title such as "Build failed" would make every failure of the check look the same), for the fingerprint (§4.37).
    /// </summary>
    public static FailureExcerptResult Compose(string? log, IReadOnlyList<FailureAnnotation>? annotations = null, IReadOnlyList<string?>? header = null)
    {
        var lines = new List<string>();
        foreach (var text in header ?? [])
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                lines.AddRange(Clean(text));
            }
        }

        var body = new List<string>();
        foreach (var a in (annotations ?? []).Take(MaxAnnotations))
        {
            var where = a.Line is { } line ? $"{a.Path}:{line.ToString(CultureInfo.InvariantCulture)}" : a.Path;
            var text = string.Join(' ', new[] { where, a.Level, OneLine(a.Message) }.Where(p => !string.IsNullOrWhiteSpace(p)));
            body.AddRange(Clean(text));
        }

        body.AddRange(LogExcerpt(LogLines(log)));
        lines.AddRange(body);
        var errorLine = FirstErrorIndex(body) is { } i ? body[i].Trim() : null;
        return new FailureExcerptResult(Cap(string.Join('\n', lines).Trim('\n'), MaxPerFailure), errorLine);
    }

    /// <summary>The log as lines: redacted again, ANSI stripped, line endings normalised, long lines cut, trailing blank lines dropped.</summary>
    public static IReadOnlyList<string> LogLines(string? log)
    {
        if (string.IsNullOrEmpty(log))
        {
            return [];
        }

        var lines = Clean(log).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        return lines;
    }

    /// <summary>§4.38 step 3: the window around the first error line plus the tail, or the last 60 lines.</summary>
    public static IReadOnlyList<string> LogExcerpt(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            return [];
        }

        if (FirstErrorIndex(lines) is not { } error)
        {
            return [.. lines.Skip(Math.Max(0, lines.Count - NoErrorLines))];
        }

        var windowStart = Math.Max(0, error - LinesBeforeError);
        var windowEnd = Math.Min(lines.Count, error + LinesFromError);
        var tailStart = Math.Max(0, lines.Count - TailLines);
        if (tailStart <= windowEnd)
        {
            return [.. lines.Skip(windowStart)];
        }

        return [.. lines.Skip(windowStart).Take(windowEnd - windowStart), Omitted(tailStart - windowEnd), .. lines.Skip(tailStart)];
    }

    /// <summary>The index of the first error line, or <c>null</c>.</summary>
    public static int? FirstErrorIndex(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        for (var i = 0; i < lines.Count; i++)
        {
            if (ErrorLine().IsMatch(lines[i]))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="line"/> matches the §4.37 error pattern.</summary>
    public static bool IsErrorLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return ErrorLine().IsMatch(line);
    }

    /// <summary>The first error line of <paramref name="excerpt"/> (trimmed), or <c>null</c>.</summary>
    public static string? FirstErrorLine(string? excerpt)
    {
        if (string.IsNullOrEmpty(excerpt))
        {
            return null;
        }

        var lines = excerpt.ReplaceLineEndings("\n").Split('\n');
        return FirstErrorIndex(lines) is { } i ? lines[i].Trim() : null;
    }

    /// <summary>Removes ANSI escape sequences (colours, cursor moves) and other control characters except tabs and line breaks.</summary>
    public static string StripAnsi(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var stripped = AnsiSequence().Replace(text, string.Empty);
        return string.Concat(stripped.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t'));
    }

    /// <summary>
    /// At most <paramref name="max"/> characters: whole lines from the top, then <c>… N lines omitted</c> for the lines cut
    /// from the bottom (a single over-long first line is cut by characters).
    /// </summary>
    public static string Cap(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= max)
        {
            return text;
        }

        var lines = text.Split('\n');
        var kept = new StringBuilder();
        var count = 0;
        foreach (var line in lines)
        {
            var note = Omitted(lines.Length - count - 1).Length + 1;
            if (kept.Length + line.Length + 1 + note > max)
            {
                break;
            }

            kept.Append(line).Append('\n');
            count++;
        }

        if (count == 0)
        {
            var room = Math.Max(0, max - Omitted(lines.Length - 1).Length - 2);
            return lines[0][..Math.Min(lines[0].Length, room)] + "…\n" + Omitted(lines.Length - 1);
        }

        return kept.Append(Omitted(lines.Length - count)).ToString();
    }

    /// <summary>
    /// FR-239: at most <paramref name="max"/> characters, cutting whole lines from the top and noting the cut on the first
    /// line (<c>… N lines omitted</c>); what is left is the end of the excerpt.
    /// </summary>
    public static string CutFromTop(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= max)
        {
            return text;
        }

        var lines = text.Split('\n');
        var start = 0;
        var length = text.Length;
        while (start < lines.Length - 1 && length + Omitted(start).Length + 1 > max)
        {
            length -= lines[start].Length + 1;
            start++;
        }

        var rest = string.Join('\n', lines.Skip(start));
        var result = start == 0 ? rest : Omitted(start) + "\n" + rest;
        return result.Length <= max ? result : result[^Math.Max(0, max)..];
    }

    /// <summary>
    /// §4.38 step 5: the findings with failure excerpts cut to <paramref name="max"/> characters in total. Failures keep their
    /// excerpt in severity order (critical first, then in list order) while it fits; the rest are cut to their first error line
    /// (else their first line). Other findings are unchanged; the order of the list is kept.
    /// </summary>
    public static IReadOnlyList<ReviewFinding> CapAll(IReadOnlyList<ReviewFinding> findings, int max = MaxTotal)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var result = findings.ToArray();
        var budget = max;
        var order = findings
            .Select((finding, index) => (Finding: finding, Index: index))
            .Where(f => f.Finding.FailureId is not null && !string.IsNullOrEmpty(f.Finding.Evidence))
            .OrderByDescending(f => f.Finding.Severity)
            .ThenBy(f => f.Index)
            .ToList();
        foreach (var (finding, index) in order)
        {
            var evidence = finding.Evidence!;
            if (evidence.Length <= budget)
            {
                budget -= evidence.Length;
                continue;
            }

            var first = FirstErrorLine(evidence) ?? evidence.Split('\n')[0].Trim();
            var cut = first.Length > MaxLineLength ? first[..MaxLineLength] + "…" : first;
            result[index] = finding with { Evidence = cut };
            budget = Math.Max(0, budget - cut.Length);
        }

        return result;
    }

    /// <summary>Redacted, ANSI-stripped and redacted again (a colour code can split a secret the first pass missed).</summary>
    private static IEnumerable<string> Clean(string text) =>
        SecretScanner.Redact(StripAnsi(SecretScanner.Redact(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))))
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Select(l => l.Length > MaxLineLength ? l[..MaxLineLength] + "…" : l);

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    [GeneratedRegex(@"error|Error|ERR!|failed|FAILED|Cannot|Can't|not found", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AnsiSequence();
}

/// <summary>
/// §4.37: a failure's fingerprint is its failure id plus the first error line of its excerpt, with timestamps, absolute temp
/// paths, hex runs of 7 or more characters and digits replaced by <c>#</c>; the id alone when the excerpt has no error
/// line. Two failures with the same fingerprint in consecutive cycles are the same failure repeated (FR-240). Pure.
/// </summary>
public static partial class FailureFingerprint
{
    public static string Of(string failureId, string? excerpt)
    {
        ArgumentException.ThrowIfNullOrEmpty(failureId);
        return ForLine(failureId, FailureExcerpt.FirstErrorLine(excerpt));
    }

    /// <summary>The fingerprint from an already known first error line (<see cref="ReviewFinding.FailureErrorLine"/>).</summary>
    public static string ForLine(string failureId, string? errorLine)
    {
        ArgumentException.ThrowIfNullOrEmpty(failureId);
        return string.IsNullOrWhiteSpace(errorLine) ? failureId : failureId + "|" + Normalise(errorLine);
    }

    /// <summary>
    /// A finding's fingerprint: its recorded first error line (from log and annotation lines only), or the failure id alone
    /// when it has none (§4.37). Every producer of a failure id records the error line, so the evidence (which starts with the
    /// check title and summary) is never searched.
    /// </summary>
    public static string Of(ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentException.ThrowIfNullOrEmpty(finding.FailureId);
        return ForLine(finding.FailureId, finding.FailureErrorLine);
    }

    /// <summary>The line with its volatile parts replaced by <c>#</c>.</summary>
    public static string Normalise(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = Timestamp().Replace(line.Trim(), "#");
        text = TempPath().Replace(text, "#");
        text = HexRun().Replace(text, "#");
        return Digits().Replace(text, "#");
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}(?:[T ]\d{1,2}:\d{2}(?::\d{2}(?:[.,]\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)?|\b\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"(?:/private)?/(?:tmp|var/tmp|var/folders)/[^\s'""()]*|[A-Za-z]:\\(?:Windows\\Temp|Users\\[^\\\s]+\\AppData\\Local\\Temp)\\[^\s'""()]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TempPath();

    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9a-fA-F]{7,}(?![0-9A-Za-z])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HexRun();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Digits();
}

/// <summary>A failure excerpt (§4.38) and its first error line from the annotation and log lines (<c>null</c>: none), for §4.37.</summary>
public sealed record FailureExcerptResult(string Text, string? ErrorLine);

/// <summary>A failure as a review cycle recorded it (in <c>compliance_reports.external_checks.failures</c>): id, fingerprint and consecutive cycles.</summary>
public sealed record FailureRecord(string Id, string Fingerprint, int Cycles);

/// <summary>FR-240: a failure seen in consecutive cycles: how many (this one included) and the previous cycle's fix step text.</summary>
public sealed record FailureRepeat(int Cycles, string? PreviousStepText);

/// <summary>
/// FR-240: repeated failures across review cycles. A failure whose fingerprint (§4.37) was in the previous cycle's report
/// is repeated: its cycle count is the previous count + 1 (consecutive cycles, this one included). Repeats change only the
/// prompt, never the review decision (D-0.6.1-6). Pure.
/// </summary>
public static class FailureHistory
{
    /// <summary>The longest previous fix step text included in the prompt.</summary>
    public const int MaxPreviousStepLength = 2000;

    /// <summary>This cycle's failure records: one per failure finding, its cycles counted from <paramref name="previous"/>.</summary>
    public static IReadOnlyList<FailureRecord> Records(IEnumerable<ReviewFinding> findings, IReadOnlyList<FailureRecord>? previous)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var result = new List<FailureRecord>();
        foreach (var finding in findings)
        {
            if (finding.FailureId is not { } id || result.Any(r => r.Id == id))
            {
                continue;
            }

            var fingerprint = FailureFingerprint.Of(finding);
            var before = previous?.FirstOrDefault(p => string.Equals(p.Fingerprint, fingerprint, StringComparison.Ordinal));
            result.Add(new FailureRecord(id, fingerprint, before is null ? 1 : Math.Max(1, before.Cycles) + 1));
        }

        return result;
    }

    /// <summary>
    /// The repeated failures of <paramref name="current"/> by id, each with the text of the previous plan's step(s) that named
    /// it (evidence blocks removed), or <c>null</c> when no step did.
    /// </summary>
    public static IReadOnlyDictionary<string, FailureRepeat> Repeats(IReadOnlyList<FailureRecord> current, IReadOnlyList<PlanStep>? previousPlan)
    {
        ArgumentNullException.ThrowIfNull(current);
        var result = new Dictionary<string, FailureRepeat>(StringComparer.Ordinal);
        foreach (var record in current.Where(r => r.Cycles > 1))
        {
            var steps = (previousPlan ?? [])
                .Where(s => FailureIds.IsNamedIn(s.Title, record.Id) || FailureIds.IsNamedIn(s.Task, record.Id))
                .Select(s => $"Step {s.Number.ToString(CultureInfo.InvariantCulture)}: {s.Title}\n{FailureEvidenceSteps.StripBlocks(s.Task)}".Trim())
                .ToList();
            var text = steps.Count == 0 ? null : string.Join("\n\n", steps);
            if (text is { Length: > MaxPreviousStepLength })
            {
                text = text[..MaxPreviousStepLength] + "…";
            }

            result[record.Id] = new FailureRepeat(record.Cycles, text);
        }

        return result;
    }
}
