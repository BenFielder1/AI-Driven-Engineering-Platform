using System.Globalization;
using System.Text;

namespace Aide.IntegrationTests.Characterisation;

/// <summary>
/// FR-261: compares a recorded snapshot with the committed <c>Characterisation/Snapshots/&lt;scenario&gt;.json</c> (read from the
/// source tree, found by walking up to the folder with <c>Aide.slnx</c>). A difference fails with a unified-style diff. When the
/// committed file is missing, or <c>UPDATE_SNAPSHOTS=1</c>, the actual snapshot is written to
/// <c>backend/TestResults/characterisation/&lt;scenario&gt;.json</c> (uploaded by CI) and the test fails too, so CI can never go
/// green without committed snapshots. A difference also writes the actual snapshot there.
/// </summary>
internal static class SnapshotAssert
{
    public const string UpdateVariable = "UPDATE_SNAPSHOTS";

    private const int ContextLines = 3;
    private const int MaxHunkLines = 60;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Match(string scenario, string actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var backend = BackendDirectory();
        var committed = Path.Combine(backend, "tests", "Aide.IntegrationTests", "Characterisation", "Snapshots", scenario + ".json");
        var output = Path.Combine(backend, "TestResults", "characterisation", scenario + ".json");

        if (string.Equals(Environment.GetEnvironmentVariable(UpdateVariable), "1", StringComparison.Ordinal))
        {
            Write(output, actual);
            Assert.Fail($"Snapshot updated ({UpdateVariable}=1): written to {output}. Copy it to {committed} and commit it (FR-261: never after M54).");
        }

        if (!File.Exists(committed))
        {
            Write(output, actual);
            Assert.Fail($"Snapshot missing: written to {output}. Review it and commit it as {committed} (FR-261).");
        }

        var expected = File.ReadAllText(committed, Utf8NoBom).ReplaceLineEndings("\n");
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            Write(output, actual);
            Assert.Fail($"The {scenario} snapshot differs from the committed one (FR-261). Actual written to {output}.\n{Diff(committed, expected, actual)}");
        }
    }

    /// <summary>The first differing region of the two texts as a unified-style hunk with 3 lines of context.</summary>
    public static string Diff(string committedPath, string expected, string actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var before = expected.Split('\n');
        var after = actual.Split('\n');

        var start = 0;
        while (start < before.Length && start < after.Length && string.Equals(before[start], after[start], StringComparison.Ordinal))
        {
            start++;
        }

        var endBefore = before.Length - 1;
        var endAfter = after.Length - 1;
        while (endBefore >= start && endAfter >= start && string.Equals(before[endBefore], after[endAfter], StringComparison.Ordinal))
        {
            endBefore--;
            endAfter--;
        }

        var contextStart = Math.Max(0, start - ContextLines);
        var removed = endBefore - start + 1;
        var added = endAfter - start + 1;
        var diff = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"--- {committedPath}\n")
            .Append("+++ actual\n")
            .Append(CultureInfo.InvariantCulture, $"@@ -{contextStart + 1},{removed + start - contextStart} +{contextStart + 1},{added + start - contextStart} @@\n");
        for (var i = contextStart; i < start; i++)
        {
            diff.Append(' ').Append(before[i]).Append('\n');
        }

        AppendLines(diff, '-', before, start, removed);
        AppendLines(diff, '+', after, start, added);
        for (var i = endBefore + 1; i < Math.Min(before.Length, endBefore + 1 + ContextLines); i++)
        {
            diff.Append(' ').Append(before[i]).Append('\n');
        }

        return diff.ToString();
    }

    /// <summary>The <c>backend</c> folder: the nearest ancestor of the test assembly's folder holding <c>Aide.slnx</c>.</summary>
    public static string BackendDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Aide.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No folder with Aide.slnx above {AppContext.BaseDirectory}: the characterisation snapshots are read from the source tree.");
    }

    private static void AppendLines(StringBuilder diff, char marker, string[] lines, int start, int count)
    {
        foreach (var line in lines.Skip(start).Take(Math.Min(count, MaxHunkLines)))
        {
            diff.Append(marker).Append(line).Append('\n');
        }

        if (count > MaxHunkLines)
        {
            diff.Append(CultureInfo.InvariantCulture, $"{marker}… {count - MaxHunkLines} more line(s)\n");
        }
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8NoBom);
    }
}
