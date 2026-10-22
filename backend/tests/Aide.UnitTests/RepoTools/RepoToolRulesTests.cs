using Aide.Domain.RepoTools;

namespace Aide.UnitTests.RepoTools;

/// <summary>FR-71: repo tool argument validation, path safety (v1 §4.6), hidden paths, limits and read slicing (pure rules).</summary>
public class RepoToolRulesTests
{
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("src/../../secrets")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\file")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData(".git/config")]
    [InlineData(".GIT/config")]
    [InlineData(".git./config")]
    [InlineData("GIT~1/config")]
    [InlineData("src/CON")]
    [InlineData("file.txt:stream")]
    [InlineData("src/a`b")]
    [InlineData("src/a\nb")]
    public void FR71_unsafe_paths_are_refused_with_UNSAFE_PATH(string path)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { path });

        var read = RepoToolArguments.ParseReadFile(json);
        var list = RepoToolArguments.ParseListFiles(json);

        Assert.Equal(RepoToolErrors.UnsafePath, read.Error?.Code);
        Assert.Equal(RepoToolErrors.UnsafePath, list.Error?.Code);
    }

    [Fact]
    public void FR71_the_unsafe_path_message_says_nothing_was_read_and_stays_on_one_line()
    {
        var error = RepoToolArguments.ParseReadFile("""{"path":"../../etc/passwd"}""").Error!;

        Assert.Contains("Nothing was read", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', error.Message);
    }

    [Theory]
    [InlineData(".", "")]
    [InlineData("./", "")]
    [InlineData("", "")]
    [InlineData("src/", "src")]
    [InlineData("./src//app", "src/app")]
    [InlineData("src\\app", "src/app")]
    public void FR71_list_files_paths_are_normalised_and_the_root_is_allowed(string path, string expected)
    {
        var parsed = RepoToolArguments.ParseListFiles(System.Text.Json.JsonSerializer.Serialize(new { path }));

        Assert.Equal(expected, parsed.Value.Path);
    }

    [Fact]
    public void FR71_read_file_needs_a_file_path_not_the_root()
    {
        Assert.Equal(RepoToolErrors.UnsafePath, RepoToolArguments.ParseReadFile("""{"path":"."}""").Error?.Code);
        Assert.Equal(RepoToolErrors.InvalidArgument, RepoToolArguments.ParseReadFile("{}").Error?.Code);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(9, 3)]
    [InlineData(0, 1)]
    public void FR71_list_depth_defaults_to_2_and_is_capped_at_3(int? depth, int expected)
    {
        var json = depth is null ? """{"path":"."}""" : $$"""{"path":".","depth":{{depth}}}""";

        Assert.Equal(expected, RepoToolArguments.ParseListFiles(json).Value.Depth);
    }

    [Theory]
    [InlineData("""{"path":"a.txt","startLine":0}""")]
    [InlineData("""{"path":"a.txt","startLine":5,"endLine":2}""")]
    [InlineData("""{"path":"a.txt","startLine":"x"}""")]
    [InlineData("""{"path":3}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    public void FR71_malformed_read_arguments_are_INVALID_ARGUMENT(string json)
    {
        Assert.Equal(RepoToolErrors.InvalidArgument, RepoToolArguments.ParseReadFile(json).Error?.Code);
    }

    [Fact]
    public void FR71_read_arguments_accept_camel_and_snake_case_and_numeric_strings()
    {
        Assert.Equal(new ReadFileArguments("a.txt", 3, 9), RepoToolArguments.ParseReadFile("""{"path":"a.txt","startLine":3,"endLine":9}""").Value);
        Assert.Equal(new ReadFileArguments("a.txt", 3, 9), RepoToolArguments.ParseReadFile("""{"path":"a.txt","start_line":"3","end_line":9}""").Value);
        Assert.Equal(new ReadFileArguments("a.txt", 1, null), RepoToolArguments.ParseReadFile("""{"path":"a.txt"}""").Value);
    }

    [Fact]
    public void FR71_search_arguments_validate_query_glob_and_regex_flag()
    {
        Assert.Equal(new SearchCodeArguments("TODO", "src/**/*.cs", true), RepoToolArguments.ParseSearchCode("""{"query":"TODO","pathGlob":"./src/**/*.cs","regex":true}""").Value);
        Assert.Equal(new SearchCodeArguments("x", null, false), RepoToolArguments.ParseSearchCode("""{"query":"x"}""").Value);
        Assert.Equal(RepoToolErrors.InvalidArgument, RepoToolArguments.ParseSearchCode("""{"query":""}""").Error?.Code);
        Assert.Equal(RepoToolErrors.InvalidArgument, RepoToolArguments.ParseSearchCode($$"""{"query":"{{new string('a', 501)}}"}""").Error?.Code);
        Assert.Equal(RepoToolErrors.InvalidArgument, RepoToolArguments.ParseSearchCode("""{"query":"x","regex":"yes"}""").Error?.Code);
        Assert.Equal(RepoToolErrors.UnsafePath, RepoToolArguments.ParseSearchCode("""{"query":"x","pathGlob":"../**"}""").Error?.Code);
        Assert.Equal(RepoToolErrors.UnsafePath, RepoToolArguments.ParseSearchCode("""{"query":"x","pathGlob":".git/**"}""").Error?.Code);
        Assert.Equal(RepoToolErrors.UnsafePath, RepoToolArguments.ParseSearchCode("""{"query":"x","pathGlob":"/etc/*"}""").Error?.Code);
    }

    [Theory]
    [InlineData(".git", true)]
    [InlineData("vendor/lib/.git/config", true)]
    [InlineData(".aide", true)]
    [InlineData(".aide/rules.md", true)]
    [InlineData(".AIDE/rules.md", true)]
    [InlineData(".github/workflows/ci.yml", false)]
    [InlineData(".aidenotes", false)]
    [InlineData("src/app.ts", false)]
    public void FR71_git_and_aide_paths_are_hidden(string path, bool hidden)
    {
        Assert.Equal(hidden, RepoToolPaths.IsHidden(path));
    }

    [Theory]
    [InlineData("*.cs", "src/app/Program.cs", true)]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "src/app.ts", false)]
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/*.cs", "src/x/a.cs", false)]
    [InlineData("src/**/*.cs", "src/a.cs", true)]
    [InlineData("src/**/*.cs", "src/x/y/a.cs", true)]
    [InlineData("src/**", "src/x/y/a.cs", true)]
    [InlineData("docs/?.md", "docs/a.md", true)]
    [InlineData("docs/?.md", "docs/ab.md", false)]
    [InlineData("README.MD", "README.md", true)]
    public void FR71_path_globs_match_like_gitignore(string glob, string path, bool expected)
    {
        Assert.Equal(expected, RepoGlob.IsMatch(glob, path));
    }

    [Fact]
    public void FR71_the_26th_call_of_a_run_is_refused()
    {
        var budget = new RepoToolBudget();
        for (var i = 1; i <= RepoToolLimits.MaxCallsPerRun; i++)
        {
            Assert.True(budget.TryBeginCall(), $"call {i}");
        }

        Assert.False(budget.TryBeginCall());
        Assert.Equal(25, budget.CallsUsed);
    }

    [Fact]
    public void FR71_a_run_that_read_400_KB_is_refused_further_calls()
    {
        var budget = new RepoToolBudget();
        Assert.True(budget.TryBeginCall());
        budget.Consume(RepoToolLimits.MaxBytesPerRun + 10);

        Assert.Equal((RepoToolLimits.MaxBytesPerRun, 0), (budget.BytesUsed, budget.RemainingBytes));
        Assert.False(budget.TryBeginCall());
        Assert.Equal(400 * 1024, RepoToolLimits.MaxBytesPerRun);
    }

    [Fact]
    public void FR71_read_slices_return_the_requested_lines()
    {
        var content = string.Join('\n', Enumerable.Range(1, 10).Select(n => $"line {n}")) + "\n";

        var slice = RepoFileSlice.Take(content, 3, 5);

        Assert.Equal(("line 3\nline 4\nline 5\n", 3, 5, 10, false), (slice.Text, slice.FirstLine, slice.LastLine, slice.TotalLines, slice.Truncated));
        Assert.False(RepoFileSlice.Take(content, 1, null).Truncated);
        var pastEnd = RepoFileSlice.Take(content, 50, null);
        Assert.Equal((string.Empty, 10, false), (pastEnd.Text, pastEnd.TotalLines, pastEnd.Truncated));
    }

    [Fact]
    public void FR71_read_slices_stop_at_400_lines_and_report_truncation()
    {
        var content = string.Join('\n', Enumerable.Range(1, 1000).Select(n => $"{n}"));

        var slice = RepoFileSlice.Take(content, 1, null);

        Assert.Equal((1, 400, 1000, true), (slice.FirstLine, slice.LastLine, slice.TotalLines, slice.Truncated));
        Assert.Equal(400, slice.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void FR71_read_slices_stop_at_64_KB_on_a_line_boundary_and_report_truncation()
    {
        var line = new string('x', 999);
        var content = string.Join('\n', Enumerable.Range(0, 100).Select(_ => line));

        var slice = RepoFileSlice.Take(content, 1, null);

        Assert.True(slice.Truncated);
        Assert.Equal(65, slice.LastLine); // 65 × 1000 bytes ≤ 64 KB < 66 × 1000
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(slice.Text) <= RepoToolLimits.MaxReadBytes);
    }

    [Fact]
    public void FR71_a_single_line_longer_than_the_cap_is_cut()
    {
        var slice = RepoFileSlice.Take(new string('é', 50_000), 1, null);

        Assert.True(slice.Truncated);
        Assert.Equal(1, slice.LastLine);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(slice.Text) <= RepoToolLimits.MaxReadBytes);
    }
}
