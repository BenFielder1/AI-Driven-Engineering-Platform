using System.Diagnostics;
using Aide.Domain.Artifacts;

namespace Aide.UnitTests.Artifacts;

/// <summary>FR-21: line diff between two artifact versions.</summary>
public class LineDiffTests
{
    [Fact]
    public void FR21_Spec_v1_and_v3_diff_shows_the_changed_lines()
    {
        const string v1 = "# Spec\n\n## API\nGET /todos\nPOST /todos\n\n## Data\nTodo(id, title)\n";
        const string v3 = "# Spec\n\n## API\nGET /todos\nPOST /todos\nDELETE /todos/{id}\n\n## Data\nTodo(id, title, done)\n";

        var lines = LineDiff.Compute(v1, v3);

        Assert.Equal(
            [
                (DiffLineKind.Context, 1, 1, "# Spec"),
                (DiffLineKind.Context, 2, 2, ""),
                (DiffLineKind.Context, 3, 3, "## API"),
                (DiffLineKind.Context, 4, 4, "GET /todos"),
                (DiffLineKind.Context, 5, 5, "POST /todos"),
                (DiffLineKind.Added, null, 6, "DELETE /todos/{id}"),
                (DiffLineKind.Context, 6, 7, ""),
                (DiffLineKind.Context, 7, 8, "## Data"),
                (DiffLineKind.Removed, 8, null, "Todo(id, title)"),
                (DiffLineKind.Added, null, 9, "Todo(id, title, done)"),
            ],
            lines.Select(l => (l.Kind, l.OldLine, l.NewLine, l.Text)));
    }

    [Fact]
    public void FR21_Unified_text_has_headers_and_hunks_with_three_lines_of_context()
    {
        const string v1 = "a\nb\nc\nd\ne\nf\ng\nh\ni\nj\nk\nl\n";
        const string v2 = "a\nB\nc\nd\ne\nf\ng\nh\ni\nj\nk\nL\n";

        var unified = LineDiff.Unified(LineDiff.Compute(v1, v2), "spec v1", "spec v2");

        Assert.Equal(
            "--- spec v1\n+++ spec v2\n"
            + "@@ -1,5 +1,5 @@\n a\n-b\n+B\n c\n d\n e\n"
            + "@@ -9,4 +9,4 @@\n i\n j\n k\n-l\n+L\n",
            unified);
    }

    [Fact]
    public void FR21_Nearby_changes_share_one_hunk()
    {
        var unified = LineDiff.Unified(LineDiff.Compute("a\nb\nc\nd\n", "A\nb\nc\nD\n"), "x", "y");

        Assert.Equal("--- x\n+++ y\n@@ -1,4 +1,4 @@\n-a\n+A\n b\n c\n-d\n+D\n", unified);
    }

    [Fact]
    public void FR21_Hunk_ranges_for_empty_sides_and_single_lines_follow_diff_u()
    {
        Assert.Equal("--- x\n+++ y\n@@ -0,0 +1 @@\n+only\n", LineDiff.Unified(LineDiff.Compute(string.Empty, "only\n"), "x", "y"));
        Assert.Equal("--- x\n+++ y\n@@ -1 +0,0 @@\n-only\n", LineDiff.Unified(LineDiff.Compute("only\n", string.Empty), "x", "y"));
    }

    [Fact]
    public void FR21_Identical_versions_have_no_changes_and_an_empty_unified_diff()
    {
        var lines = LineDiff.Compute("a\nb\n", "a\r\nb\r\n");

        Assert.All(lines, l => Assert.Equal(DiffLineKind.Context, l.Kind));
        Assert.Equal(string.Empty, LineDiff.Unified(lines, "x", "y"));
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("a", new[] { "a" })]
    [InlineData("a\n", new[] { "a" })]
    [InlineData("a\n\n", new[] { "a", "" })]
    [InlineData("a\r\nb", new[] { "a", "b" })]
    public void FR21_Lines_are_split_on_normalised_endings_without_a_trailing_empty_line(string content, string[] expected) =>
        Assert.Equal(expected, LineDiff.SplitLines(content));

    [Theory]
    [InlineData("a\nb\nc\nd\n", "x\nb\ny\nd\nz\n")]
    [InlineData("1\n2\n3\n4\n5\n6\n", "6\n5\n4\n3\n2\n1\n")]
    [InlineData("a\na\nb\na\n", "b\na\na\na\nb\n")]
    [InlineData("", "")]
    [InlineData("same\n", "same\n")]
    public void FR21_The_diff_reconstructs_both_versions(string from, string to) => AssertReconstructs(LineDiff.Compute(from, to), from, to);

    [Fact]
    public void FR21_The_diff_is_minimal_for_a_single_insertion_in_a_long_text()
    {
        var from = string.Join('\n', Enumerable.Range(1, 500).Select(i => $"line {i}"));
        var to = from.Replace("line 250\n", "line 250\ninserted\n", StringComparison.Ordinal);

        var lines = LineDiff.Compute(from, to);

        var change = Assert.Single(lines, l => l.Kind != DiffLineKind.Context);
        Assert.Equal((DiffLineKind.Added, (int?)null, (int?)251, "inserted"), (change.Kind, change.OldLine, change.NewLine, change.Text));
    }

    [Fact]
    public void FR21_Minimal_edits_are_found_in_interleaved_changes()
    {
        var lines = LineDiff.Compute("a\nb\nc\na\nb\nb\na\n", "c\nb\na\nb\na\nc\n");

        // Myers' classic example: the shortest edit script has 5 edits.
        Assert.Equal(5, lines.Count(l => l.Kind != DiffLineKind.Context));
    }

    [Fact]
    public void FR21_Pathologically_different_large_inputs_finish_and_still_reconstruct_both_versions()
    {
        var from = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => $"a{i}"));
        var to = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => $"b{i}"));

        var started = Stopwatch.StartNew();
        var lines = LineDiff.Compute(from, to);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");
        AssertReconstructs(lines, from, to);
        Assert.Equal(40_000, lines.Count);
    }

    [Fact]
    public void FR21_Unified_text_of_a_large_diff_with_thousands_of_hunks_is_linear()
    {
        // 200,000 lines, a change every 50 → 4,000 hunks (quadratic counting would be ~10⁹ steps).
        var lines = new List<DiffLine>(200_000);
        for (var i = 0; i < 200_000; i++)
        {
            lines.Add(i % 50 == 25
                ? new DiffLine(DiffLineKind.Added, null, i + 1, $"new {i}")
                : new DiffLine(DiffLineKind.Context, i + 1, i + 1, $"line {i}"));
        }

        var started = Stopwatch.StartNew();
        var unified = LineDiff.Unified(lines, "x", "y");
        var collapsed = LineDiff.Collapse(lines);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"took {started.Elapsed}");
        Assert.Equal(4_000, unified.Split('\n').Count(l => l.StartsWith("@@", StringComparison.Ordinal)));
        Assert.Equal(4_000 + 4_001, collapsed.Count(l => l.Kind is DiffLineKind.Added or DiffLineKind.Skipped));
    }

    [Fact]
    public void FR21_Hunk_headers_after_many_hunks_carry_the_right_line_numbers()
    {
        var from = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"l{i}")) + "\n";
        var to = from.Replace("l20\n", "L20\n", StringComparison.Ordinal).Replace("l50\n", "", StringComparison.Ordinal).Replace("l80\n", "l80\nnew\n", StringComparison.Ordinal);

        var unified = LineDiff.Unified(LineDiff.Compute(from, to), "x", "y");

        Assert.Equal(
            ["@@ -17,7 +17,7 @@", "@@ -47,7 +47,6 @@", "@@ -78,6 +77,7 @@"],
            unified.Split('\n').Where(l => l.StartsWith("@@", StringComparison.Ordinal)));
    }

    [Fact]
    public void FR21_Collapse_keeps_three_lines_of_context_and_replaces_each_longer_unchanged_run_with_one_skipped_entry()
    {
        var from = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"l{i}")) + "\n";
        var to = from.Replace("l10\n", "L10\n", StringComparison.Ordinal);

        var collapsed = LineDiff.Collapse(LineDiff.Compute(from, to));

        Assert.Equal(
            [
                (DiffLineKind.Skipped, 1, 1, "", 6),
                (DiffLineKind.Context, 7, 7, "l7", 0),
                (DiffLineKind.Context, 8, 8, "l8", 0),
                (DiffLineKind.Context, 9, 9, "l9", 0),
                (DiffLineKind.Removed, 10, null, "l10", 0),
                (DiffLineKind.Added, null, 10, "L10", 0),
                (DiffLineKind.Context, 11, 11, "l11", 0),
                (DiffLineKind.Context, 12, 12, "l12", 0),
                (DiffLineKind.Context, 13, 13, "l13", 0),
                (DiffLineKind.Skipped, 14, 14, "", 7),
            ],
            collapsed.Select(l => (l.Kind, l.OldLine, l.NewLine, l.Text, l.Skipped)));
    }

    [Fact]
    public void FR21_Collapse_of_identical_versions_is_one_skipped_entry_and_of_nothing_is_empty()
    {
        var same = LineDiff.Collapse(LineDiff.Compute("a\nb\n", "a\nb\n"));

        var skipped = Assert.Single(same);
        Assert.Equal((DiffLineKind.Skipped, (int?)1, (int?)1, 2), (skipped.Kind, skipped.OldLine, skipped.NewLine, skipped.Skipped));
        Assert.Empty(LineDiff.Collapse(LineDiff.Compute(string.Empty, string.Empty)));
    }

    [Fact]
    public void FR21_Diff_returns_collapsed_lines_and_unified_text()
    {
        var result = LineDiff.Diff("a\nb\n", "a\nB\n", "x", "y");

        Assert.True(result.IsSuccess);
        Assert.Equal("--- x\n+++ y\n@@ -1,2 +1,2 @@\n a\n-b\n+B\n", result.Value.Unified);
        Assert.Equal(3, result.Value.Lines.Count);
    }

    [Fact]
    public void FR21_Versions_with_more_than_the_line_limit_together_are_VALIDATION()
    {
        var half = string.Concat(Enumerable.Repeat("x\n", (LineDiff.MaxTotalLines / 2) + 1));

        var tooLarge = LineDiff.Diff(half, half, "x", "y");
        var atLimit = LineDiff.Diff(string.Concat(Enumerable.Repeat("x\n", LineDiff.MaxTotalLines / 2)), string.Concat(Enumerable.Repeat("x\n", LineDiff.MaxTotalLines / 2)), "x", "y");

        Assert.Equal(Aide.Domain.Common.ErrorCodes.Validation, tooLarge.Error?.Code);
        Assert.True(atLimit.IsSuccess);
    }

    private static void AssertReconstructs(IReadOnlyList<DiffLine> lines, string from, string to)
    {
        var oldLines = LineDiff.SplitLines(from);
        var newLines = LineDiff.SplitLines(to);
        Assert.Equal(oldLines, lines.Where(l => l.Kind != DiffLineKind.Added).Select(l => l.Text));
        Assert.Equal(newLines, lines.Where(l => l.Kind != DiffLineKind.Removed).Select(l => l.Text));
        Assert.Equal(Enumerable.Range(1, oldLines.Length), lines.Where(l => l.OldLine is not null).Select(l => l.OldLine!.Value));
        Assert.Equal(Enumerable.Range(1, newLines.Length), lines.Where(l => l.NewLine is not null).Select(l => l.NewLine!.Value));
    }
}
