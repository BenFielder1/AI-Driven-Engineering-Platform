using System.Globalization;
using System.Text;
using Aide.Domain.Common;

namespace Aide.Domain.Artifacts;

/// <summary>
/// One line of a diff (FR-21). <see cref="OldLine"/> is the 1-based line number in the older version (null for
/// <see cref="DiffLineKind.Added"/>), <see cref="NewLine"/> the number in the newer version (null for
/// <see cref="DiffLineKind.Removed"/>). A <see cref="DiffLineKind.Skipped"/> entry stands for <see cref="Skipped"/>
/// unchanged lines starting at <see cref="OldLine"/>/<see cref="NewLine"/>, with an empty <see cref="Text"/>.
/// </summary>
public sealed record DiffLine(DiffLineKind Kind, int? OldLine, int? NewLine, string Text, int Skipped = 0);

/// <summary>A bounded diff for the API (FR-21): the collapsed line list and the unified-diff text.</summary>
public sealed record DiffResult(IReadOnlyList<DiffLine> Lines, string Unified);

/// <summary>
/// FR-21: a line diff between two artifact versions (Myers' O(ND) algorithm), plus its unified-diff text. Line endings
/// are normalised to <c>\n</c> first (§4.3), so a CRLF/LF difference is not a change.
/// </summary>
/// <remarks>
/// Pathological inputs are bounded: the common prefix and suffix are matched first, and the Myers search stops after
/// <see cref="MaxEdits"/> edits (fewer for very long inputs, see <see cref="MaxWork"/>). Beyond that the remaining middle is reported as removed-then-added — still a correct
/// diff (applying it yields the new version), just not a minimal one — so time and memory stay bounded for any input
/// the API accepts. <see cref="Diff"/> also refuses versions with more than <see cref="MaxTotalLines"/> lines together
/// and collapses unchanged runs, so a response is never much larger than the changes plus their context.
/// </remarks>
public static class LineDiff
{
    /// <summary>Lines of context around each change in the unified text (the <c>diff -u</c> default).</summary>
    public const int DefaultContext = 3;

    /// <summary>The most edits the search explores (memory grows with its square).</summary>
    public const int MaxEdits = 2000;

    /// <summary>Upper bound on the work of one search, as (lines in both middles) × (edits explored).</summary>
    public const long MaxWork = 50_000_000;

    /// <summary>The most lines both versions may have together for <see cref="Diff"/>; larger diffs are <c>VALIDATION</c>.</summary>
    public const int MaxTotalLines = 100_000;

    /// <summary>
    /// FR-21: the diff as served by the API — <see cref="Collapse">collapsed</see> lines plus unified text — or
    /// <c>VALIDATION</c> when the two versions have more than <see cref="MaxTotalLines"/> lines together.
    /// </summary>
    public static Result<DiffResult> Diff(string from, string to, string fromLabel, string toLabel, int context = DefaultContext)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var total = (long)SplitLines(from).Length + SplitLines(to).Length;
        if (total > MaxTotalLines)
        {
            var message = string.Create(
                CultureInfo.InvariantCulture,
                $"The two versions have {total} lines together; diffs are limited to {MaxTotalLines} lines.");
            return Result.Failure<DiffResult>(new DomainError(ErrorCodes.Validation, message) { Details = [$"to: {message}"] });
        }

        var lines = Compute(from, to);
        return Result.Success(new DiffResult(Collapse(lines, context), Unified(lines, fromLabel, toLabel, context)));
    }

    /// <summary>Every line of both versions in order, as context, removed or added.</summary>
    public static IReadOnlyList<DiffLine> Compute(string from, string to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var a = SplitLines(from);
        var b = SplitLines(to);

        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && string.Equals(a[prefix], b[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix
            && string.Equals(a[a.Length - 1 - suffix], b[b.Length - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        var lines = new List<DiffLine>(a.Length + b.Length - prefix - suffix);
        for (var i = 0; i < prefix; i++)
        {
            lines.Add(new DiffLine(DiffLineKind.Context, i + 1, i + 1, a[i]));
        }

        var middleA = a[prefix..(a.Length - suffix)];
        var middleB = b[prefix..(b.Length - suffix)];
        foreach (var (kind, ai, bi) in Middle(middleA, middleB))
        {
            lines.Add(kind switch
            {
                DiffLineKind.Context => new DiffLine(kind, prefix + ai + 1, prefix + bi + 1, middleA[ai]),
                DiffLineKind.Removed => new DiffLine(kind, prefix + ai + 1, null, middleA[ai]),
                _ => new DiffLine(kind, null, prefix + bi + 1, middleB[bi]),
            });
        }

        for (var i = 0; i < suffix; i++)
        {
            var ai = a.Length - suffix + i;
            var bi = b.Length - suffix + i;
            lines.Add(new DiffLine(DiffLineKind.Context, ai + 1, bi + 1, a[ai]));
        }

        return lines;
    }

    /// <summary>
    /// Unified-diff text (<c>diff -u</c> format) of <paramref name="lines"/> (as from <see cref="Compute"/>):
    /// <c>--- from</c>/<c>+++ to</c> headers, then hunks with <paramref name="context"/> lines of context. Empty when
    /// nothing changed. One pass over the lines.
    /// </summary>
    public static string Unified(IReadOnlyList<DiffLine> lines, string fromLabel, string toLabel, int context = DefaultContext)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfNegative(context);
        var hunks = Hunks(lines, context);
        if (hunks.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.Append("--- ").Append(fromLabel).Append('\n');
        text.Append("+++ ").Append(toLabel).Append('\n');

        // Running line counts up to `position`; hunks are disjoint and in order, so every line is counted once.
        var position = 0;
        var oldBefore = 0;
        var newBefore = 0;
        foreach (var (start, end) in hunks)
        {
            for (; position < start; position++)
            {
                Count(lines[position].Kind, ref oldBefore, ref newBefore);
            }

            var oldCount = 0;
            var newCount = 0;
            for (var i = start; i < end; i++)
            {
                Count(lines[i].Kind, ref oldCount, ref newCount);
            }

            text.Append("@@ -").Append(Range(oldBefore, oldCount)).Append(" +").Append(Range(newBefore, newCount)).Append(" @@\n");
            for (var i = start; i < end; i++)
            {
                text.Append(lines[i].Kind switch
                {
                    DiffLineKind.Added => '+',
                    DiffLineKind.Removed => '-',
                    _ => ' ',
                }).Append(lines[i].Text).Append('\n');
            }

            oldBefore += oldCount;
            newBefore += newCount;
            position = end;
        }

        return text.ToString();
    }

    /// <summary>
    /// The structured diff for side-by-side rendering: the hunks of <see cref="Unified"/> (each change with
    /// <paramref name="context"/> lines of context) and, for every run of unchanged lines outside them (between two
    /// hunks, before the first, after the last, or the whole text when nothing changed), one
    /// <see cref="DiffLineKind.Skipped"/> entry. One pass over the lines.
    /// </summary>
    public static IReadOnlyList<DiffLine> Collapse(IReadOnlyList<DiffLine> lines, int context = DefaultContext)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfNegative(context);
        var result = new List<DiffLine>();
        var position = 0;
        foreach (var (start, end) in Hunks(lines, context))
        {
            AddSkipped(result, lines, position, start);
            for (var i = start; i < end; i++)
            {
                result.Add(lines[i]);
            }

            position = end;
        }

        AddSkipped(result, lines, position, lines.Count);
        return result;
    }

    /// <summary>The lines of <paramref name="content"/> without their endings; a final line ending adds no empty line.</summary>
    public static string[] SplitLines(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
        {
            return [];
        }

        var normalised = ContentHash.NormaliseLineEndings(content);
        var lines = normalised.Split('\n');
        return normalised.EndsWith('\n') ? lines[..^1] : lines;
    }

    /// <summary>
    /// The [start, end) index ranges of the hunks: each change with <paramref name="context"/> lines either side, merged
    /// when the unchanged gap between two changes is at most 2 × context. Disjoint and in order. O(lines).
    /// </summary>
    private static List<(int Start, int End)> Hunks(IReadOnlyList<DiffLine> lines, int context)
    {
        var hunks = new List<(int Start, int End)>();
        var start = -1;
        var lastChange = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind is not (DiffLineKind.Added or DiffLineKind.Removed))
            {
                continue;
            }

            if (start >= 0 && i - lastChange - 1 > 2 * context)
            {
                hunks.Add((start, Math.Min(lines.Count, lastChange + 1 + context)));
                start = -1;
            }

            if (start < 0)
            {
                start = Math.Max(0, i - context);
            }

            lastChange = i;
        }

        if (start >= 0)
        {
            hunks.Add((start, Math.Min(lines.Count, lastChange + 1 + context)));
        }

        return hunks;
    }

    private static void Count(DiffLineKind kind, ref int oldLines, ref int newLines)
    {
        if (kind != DiffLineKind.Added)
        {
            oldLines++;
        }

        if (kind != DiffLineKind.Removed)
        {
            newLines++;
        }
    }

    private static void AddSkipped(List<DiffLine> result, IReadOnlyList<DiffLine> lines, int start, int end)
    {
        if (end > start)
        {
            var first = lines[start];
            result.Add(new DiffLine(DiffLineKind.Skipped, first.OldLine, first.NewLine, string.Empty, end - start));
        }
    }

    /// <summary>A range of a hunk header: "start,count", or just "start" for one line; start is the line before an empty range.</summary>
    private static string Range(int before, int count) => count switch
    {
        0 => string.Create(CultureInfo.InvariantCulture, $"{before},0"),
        1 => (before + 1).ToString(CultureInfo.InvariantCulture),
        _ => string.Create(CultureInfo.InvariantCulture, $"{before + 1},{count}"),
    };

    /// <summary>The edit script of two sequences with no common prefix or suffix: (kind, index in a, index in b).</summary>
    private static List<(DiffLineKind Kind, int A, int B)> Middle(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        if (n == 0 || m == 0)
        {
            return Replace(n, m);
        }

        var budget = (int)Math.Min(MaxEdits, Math.Max(1, MaxWork / (n + m)));
        var limit = Math.Min(n + m, budget);
        var offset = limit + 1;
        var v = new int[(2 * limit) + 3];
        var trace = new List<int[]>();

        for (var d = 0; d <= limit; d++)
        {
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && string.Equals(a[x], b[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    trace.Add(v[(offset - d)..(offset + d + 1)]);
                    return Backtrack(a, b, trace);
                }
            }

            trace.Add(v[(offset - d)..(offset + d + 1)]);
        }

        // Over budget: report the whole middle as replaced.
        return Replace(n, m);
    }

    private static List<(DiffLineKind Kind, int A, int B)> Backtrack(string[] a, string[] b, List<int[]> trace)
    {
        var script = new List<(DiffLineKind Kind, int A, int B)>(a.Length + b.Length);
        var x = a.Length;
        var y = b.Length;
        for (var d = trace.Count - 1; d > 0; d--)
        {
            var previous = trace[d - 1];
            var k = x - y;
            int At(int diagonal) => previous[diagonal + d - 1];
            var down = k == -d || (k != d && At(k - 1) < At(k + 1));
            var previousK = down ? k + 1 : k - 1;
            var previousX = At(previousK);
            var previousY = previousX - previousK;

            while (x > previousX && y > previousY)
            {
                x--;
                y--;
                script.Add((DiffLineKind.Context, x, y));
            }

            if (down)
            {
                y--;
                script.Add((DiffLineKind.Added, x, y));
            }
            else
            {
                x--;
                script.Add((DiffLineKind.Removed, x, y));
            }
        }

        while (x > 0 && y > 0)
        {
            x--;
            y--;
            script.Add((DiffLineKind.Context, x, y));
        }

        script.Reverse();
        return script;
    }

    private static List<(DiffLineKind Kind, int A, int B)> Replace(int n, int m)
    {
        var script = new List<(DiffLineKind Kind, int A, int B)>(n + m);
        for (var i = 0; i < n; i++)
        {
            script.Add((DiffLineKind.Removed, i, 0));
        }

        for (var j = 0; j < m; j++)
        {
            script.Add((DiffLineKind.Added, 0, j));
        }

        return script;
    }
}
