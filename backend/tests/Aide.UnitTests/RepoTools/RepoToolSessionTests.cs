using System.Text;
using System.Text.Json;
using Aide.Domain.RepoTools;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.RepoTools;

namespace Aide.UnitTests.RepoTools;

/// <summary>
/// FR-71, NFR-SEC2: the repo tools over a snapshot directory: path safety, hidden <c>.git/</c> and <c>.aide/</c>, secret
/// redaction, per-call truncation, the per-run limits and the call log stored in <c>agent_runs.tool_calls</c>.
/// </summary>
public sealed class RepoToolSessionTests : IDisposable
{
    // Built at run time so the source holds no secret-shaped literal.
    private static readonly string AwsKey = "AKIA" + "IOSFODNN7EXAMPLE";
    private static readonly string GitHubToken = "ghp" + "_" + new string('a', 20) + new string('B', 16);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aide-repotools-tests", Guid.NewGuid().ToString("N"));

    public RepoToolSessionTests()
    {
        Write("README.md", "# Demo\n\nHello from the demo repo.\n");
        Write("src/app.ts", "export const answer = 42;\n// TODO: more\n");
        Write("src/nested/deep/more/file.ts", "deep\n");
        Write("config/settings.env.sample", $"AWS_ACCESS_KEY_ID={AwsKey}\nGITHUB_TOKEN={GitHubToken}\nREGION=eu-west-1\n");
        Write(".git/config", "[remote \"origin\"]\n\turl = https://github.com/acme/widgets.git\n\tTODO-in-git\n");
        Write(".aide/rules.md", "Only humans edit this. TODO-in-aide\n");
        Write("vendor/sub/.git/HEAD", "ref: refs/heads/main\n");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static LlmToolCall Call(string tool, string json) => new("c1", tool, json);

    private static async Task<bool> JunctionAsync(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start);
        if (process is null)
        {
            return false;
        }

        await process.WaitForExitAsync(Ct);
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public async Task FR71_reading_etc_passwd_through_dotdot_returns_UNSAFE_PATH_and_reads_nothing()
    {
        var session = new RepoToolSession(_root);

        var result = await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":"../../etc/passwd"}"""), Ct);

        Assert.True(result.IsError);
        Assert.StartsWith("UNSAFE_PATH:", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("root:", result.Content, StringComparison.Ordinal);
        var logged = Assert.Single(session.Calls);
        Assert.Equal((RepoToolNames.ReadFile, 0, false, RepoToolErrors.UnsafePath), (logged.Tool, logged.Bytes, logged.Truncated, logged.Error));
        Assert.Equal(0, session.Budget.BytesUsed);
    }

    [Theory]
    [InlineData("""{"path":"/etc/passwd"}""")]
    [InlineData("""{"path":"C:\\Windows\\win.ini"}""")]
    [InlineData("""{"path":".git/config"}""")]
    [InlineData("""{"path":".git./config"}""")]
    [InlineData("""{"path":"GIT~1/config"}""")]
    public async Task FR71_absolute_drive_and_git_alias_paths_return_UNSAFE_PATH(string json)
    {
        var session = new RepoToolSession(_root);

        var result = await session.ExecuteAsync(Call(RepoToolNames.ReadFile, json), Ct);

        Assert.StartsWith("UNSAFE_PATH:", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("github.com", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR71_a_symlink_escaping_the_snapshot_is_UNSAFE_PATH()
    {
        var outside = Path.Combine(Path.GetTempPath(), "aide-repotools-tests", "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside the repo");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows without the symlink privilege: a directory junction (a reparse point too) needs none.
            if (!OperatingSystem.IsWindows() || !await JunctionAsync(Path.Combine(_root, "escape"), outside))
            {
                Assert.Skip("Creating symbolic links needs privileges on this machine.");
            }
        }

        var session = new RepoToolSession(_root);

        var read = await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":"escape/secret.txt"}"""), Ct);
        var list = await session.ExecuteAsync(Call(RepoToolNames.ListFiles, """{"path":".","depth":3}"""), Ct);
        var search = await session.ExecuteAsync(Call(RepoToolNames.SearchCode, """{"query":"outside the repo"}"""), Ct);

        Assert.StartsWith("UNSAFE_PATH:", read.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("escape", list.Content, StringComparison.Ordinal);
        Assert.StartsWith("0 matches", search.Content, StringComparison.Ordinal);
        Directory.Delete(Path.Combine(_root, "escape")); // the link only, never its target's content
        Directory.Delete(outside, recursive: true);
    }

    [Fact]
    public async Task FR71_git_and_aide_are_invisible_to_every_tool()
    {
        var session = new RepoToolSession(_root);

        var list = await session.ExecuteAsync(Call(RepoToolNames.ListFiles, """{"path":".","depth":3}"""), Ct);
        var search = await session.ExecuteAsync(Call(RepoToolNames.SearchCode, """{"query":"TODO"}"""), Ct);
        var readAide = await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":".aide/rules.md"}"""), Ct);
        var listAide = await session.ExecuteAsync(Call(RepoToolNames.ListFiles, """{"path":".AIDE"}"""), Ct);
        var readGit = await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":"vendor/sub/.git/HEAD"}"""), Ct);

        Assert.Contains("README.md", list.Content, StringComparison.Ordinal);
        Assert.Contains("src/nested/", list.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(".git", list.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(".aide", list.Content, StringComparison.Ordinal);
        Assert.Contains("src/app.ts:2:", search.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO-in-git", search.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO-in-aide", search.Content, StringComparison.Ordinal);
        Assert.StartsWith("NOT_FOUND:", readAide.Content, StringComparison.Ordinal);
        Assert.StartsWith("NOT_FOUND:", listAide.Content, StringComparison.Ordinal);
        Assert.StartsWith("UNSAFE_PATH:", readGit.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR71_list_files_respects_the_depth()
    {
        var session = new RepoToolSession(_root);

        var one = await session.ExecuteAsync(Call(RepoToolNames.ListFiles, """{"path":".","depth":1}"""), Ct);
        var three = await session.ExecuteAsync(Call(RepoToolNames.ListFiles, """{"path":"src","depth":3}"""), Ct);

        Assert.Contains("src/\n", one.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("src/app.ts", one.Content, StringComparison.Ordinal);
        Assert.Contains("src/nested/deep/more/", three.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("src/nested/deep/more/file.ts", three.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NFRSEC2_FR71_secrets_are_redacted_in_read_file_and_search_code()
    {
        var session = new RepoToolSession(_root);

        var read = await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":"config/settings.env.sample"}"""), Ct);
        var search = await session.ExecuteAsync(Call(RepoToolNames.SearchCode, """{"query":"_"}"""), Ct);
        var probe = await session.ExecuteAsync(Call(RepoToolNames.SearchCode, """{"query":"AKIA[A-Z0-9]{4}","regex":true}"""), Ct);

        foreach (var content in new[] { read.Content, search.Content })
        {
            Assert.DoesNotContain(AwsKey, content, StringComparison.Ordinal);
            Assert.DoesNotContain(GitHubToken, content, StringComparison.Ordinal);
        }

        Assert.Contains("AWS_ACCESS_KEY_ID=[REDACTED:aws_access_key_id]", read.Content, StringComparison.Ordinal);
        Assert.Contains("GITHUB_TOKEN=[REDACTED:github_token]", read.Content, StringComparison.Ordinal);
        Assert.Contains("REGION=eu-west-1", read.Content, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:github_token]", search.Content, StringComparison.Ordinal);
        Assert.StartsWith("0 matches", probe.Content, StringComparison.Ordinal); // a regex can't probe a redacted secret
    }

    [Fact]
    public async Task NFRSEC2_FR71_a_secret_in_the_arguments_is_redacted_in_the_log()
    {
        var session = new RepoToolSession(_root);

        await session.ExecuteAsync(Call(RepoToolNames.SearchCode, $$"""{"query":"{{GitHubToken}}"}"""), Ct);

        Assert.DoesNotContain(GitHubToken, session.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR71_read_file_returns_the_range_and_truncates_at_400_lines()
    {
        Write("big.txt", string.Join('\n', Enumerable.Range(1, 1000).Select(n => $"row {n}")) + "\n");
        var session = new RepoToolSession(_root);

        var range = await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":"big.txt","startLine":10,"endLine":12}"""), Ct);
        var all = await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":"big.txt"}"""), Ct);

        Assert.Equal("big.txt (lines 10-12 of 1000)\nrow 10\nrow 11\nrow 12\n", range.Content);
        Assert.StartsWith("big.txt (lines 1-400 of 1000; truncated", all.Content, StringComparison.Ordinal);
        Assert.EndsWith("row 400\n", all.Content, StringComparison.Ordinal);
        Assert.Equal([false, true], session.Calls.Select(c => c.Truncated));
    }

    [Fact]
    public async Task FR71_read_file_truncates_at_64_KB()
    {
        Write("wide.txt", string.Join('\n', Enumerable.Range(0, 200).Select(_ => new string('w', 999))));
        var session = new RepoToolSession(_root);

        var result = await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":"wide.txt"}"""), Ct);

        Assert.Contains("truncated", result.Content.Split('\n')[0], StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(result.Content) <= RepoToolLimits.MaxReadBytes + 200);
        Assert.True(session.Calls[0].Truncated);
    }

    [Fact]
    public async Task FR71_the_26th_call_returns_TOOL_LIMIT()
    {
        var session = new RepoToolSession(_root);
        for (var i = 0; i < 25; i++)
        {
            Assert.False((await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":"README.md"}"""), Ct)).IsError);
        }

        Assert.False(session.LimitReached);
        var refused = await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":"README.md"}"""), Ct);

        Assert.True(refused.IsError);
        Assert.StartsWith("TOOL_LIMIT:", refused.Content, StringComparison.Ordinal);
        Assert.Contains("Answer now", refused.Content, StringComparison.Ordinal);
        Assert.True(session.LimitReached);
        Assert.Equal(26, session.Calls.Count);
        Assert.Equal(RepoToolErrors.ToolLimit, session.Calls[^1].Error);
    }

    [Fact]
    public async Task FR71_a_run_reads_at_most_400_KB()
    {
        Write("wide.txt", string.Join('\n', Enumerable.Range(0, 400).Select(_ => new string('w', 999))));
        var session = new RepoToolSession(_root);
        var calls = 0;
        LlmToolResult result;
        do
        {
            result = await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":"wide.txt"}"""), Ct);
            calls++;
        }
        while (!result.IsError && calls < 20);

        Assert.StartsWith("TOOL_LIMIT:", result.Content, StringComparison.Ordinal);
        Assert.Contains("400 KB", result.Content, StringComparison.Ordinal);
        Assert.Equal(RepoToolLimits.MaxBytesPerRun, session.Budget.BytesUsed);
        Assert.Equal(RepoToolLimits.MaxBytesPerRun, session.Calls.Sum(c => c.Bytes));
        Assert.True(calls < 25);
    }

    [Fact]
    public async Task FR71_search_code_finds_plain_text_and_regex_matches_within_a_glob()
    {
        var session = new RepoToolSession(_root);

        var plain = await session.ExecuteAsync(Call(RepoToolNames.SearchCode, """{"query":"todo"}"""), Ct);
        var regex = await session.ExecuteAsync(Call(RepoToolNames.SearchCode, """{"query":"answer\\s*=\\s*\\d+","regex":true,"pathGlob":"src/**/*.ts"}"""), Ct);
        var outsideGlob = await session.ExecuteAsync(Call(RepoToolNames.SearchCode, """{"query":"Hello","pathGlob":"*.ts"}"""), Ct);
        var badRegex = await session.ExecuteAsync(Call(RepoToolNames.SearchCode, """{"query":"(","regex":true}"""), Ct);

        Assert.Contains("src/app.ts:2: // TODO: more", plain.Content, StringComparison.Ordinal);
        Assert.Contains("src/app.ts:1: export const answer = 42;", regex.Content, StringComparison.Ordinal);
        Assert.StartsWith("0 matches", outsideGlob.Content, StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT:", badRegex.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR71_A_pathological_regex_on_a_large_file_stops_at_the_search_budget_with_SEARCH_TIMEOUT_and_partial_matches()
    {
        Write("0-hit.txt", "needle here\n");
        Write("big.txt", string.Join('\n', Enumerable.Range(0, 5000).Select(_ => new string('a', 40))));
        var session = new RepoToolSession(_root);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // The backreference rules out linear-time matching, so this backtracks exponentially on every line of big.txt.
        var result = await session.ExecuteAsync(Call(RepoToolNames.SearchCode, """{"query":"needle|(a+)+\\1!","regex":true}"""), Ct);

        watch.Stop();
        Assert.True(watch.Elapsed < RepoToolLimits.SearchTimeout * 2, $"took {watch.Elapsed}");
        Assert.True(result.IsError);
        Assert.StartsWith("SEARCH_TIMEOUT:", result.Content, StringComparison.Ordinal);
        Assert.Contains("0-hit.txt:1: needle here", result.Content, StringComparison.Ordinal);
        Assert.Equal("SEARCH_TIMEOUT", session.Calls[0].Error);
    }

    [Fact]
    public async Task FR71_A_regex_without_backreferences_matches_in_linear_time()
    {
        Write("big.txt", string.Join('\n', Enumerable.Range(0, 5000).Select(_ => new string('a', 40))));
        var session = new RepoToolSession(_root);

        var result = await session.ExecuteAsync(Call(RepoToolNames.SearchCode, """{"query":"(a+)+!","regex":true}"""), Ct);

        Assert.False(result.IsError);
        Assert.StartsWith("0 matches", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR71_search_code_returns_at_most_50_matches()
    {
        Write("many.txt", string.Join('\n', Enumerable.Range(1, 80).Select(n => $"needle {n}")));
        var session = new RepoToolSession(_root);

        var result = await session.ExecuteAsync(Call(RepoToolNames.SearchCode, """{"query":"needle"}"""), Ct);

        Assert.Equal(50, result.Content.Split('\n').Count(l => l.StartsWith("many.txt:", StringComparison.Ordinal)));
        Assert.Contains("truncated", result.Content.Split('\n')[0], StringComparison.Ordinal);
        Assert.True(session.Calls[0].Truncated);
    }

    [Fact]
    public async Task FR71_every_call_is_logged_with_tool_arguments_bytes_truncated_and_error()
    {
        var session = new RepoToolSession(_root);

        var ok = await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":"README.md","startLine":1}"""), Ct);
        await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":"missing.md"}"""), Ct);
        await session.ExecuteAsync(Call("run_shell", """{"cmd":"rm -rf /"}"""), Ct);
        await session.ExecuteAsync(Call(RepoToolNames.ListFiles, "not json"), Ct);

        using var log = JsonDocument.Parse(session.ToJson());
        var calls = log.RootElement.EnumerateArray().ToList();
        Assert.Equal(4, calls.Count);
        Assert.Equal("read_file", calls[0].GetProperty("tool").GetString());
        Assert.Equal("README.md", calls[0].GetProperty("arguments").GetProperty("path").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount(ok.Content), calls[0].GetProperty("bytes").GetInt32());
        Assert.False(calls[0].GetProperty("truncated").GetBoolean());
        Assert.False(calls[0].TryGetProperty("error", out _));
        Assert.Equal("NOT_FOUND", calls[1].GetProperty("error").GetString());
        Assert.Equal(("run_shell", "INVALID_ARGUMENT"), (calls[2].GetProperty("tool").GetString(), calls[2].GetProperty("error").GetString()));
        Assert.Equal(("not json", "INVALID_ARGUMENT"), (calls[3].GetProperty("arguments").GetString(), calls[3].GetProperty("error").GetString()));
        Assert.Equal("[]", new RepoToolSession(_root).ToJson());
    }

    [Fact]
    public async Task FR71_binary_files_are_not_shown()
    {
        File.WriteAllBytes(Path.Combine(_root, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
        var session = new RepoToolSession(_root);

        var result = await session.ExecuteAsync(Call(RepoToolNames.ReadFile, """{"path":"logo.png"}"""), Ct);

        Assert.False(result.IsError);
        Assert.Contains("binary file", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR71_the_fallback_context_lists_the_repository_without_hidden_paths()
    {
        var session = new RepoToolSession(_root);

        var context = await session.FallbackContextAsync(Ct);

        Assert.StartsWith("<repository_files>", context, StringComparison.Ordinal);
        Assert.Contains("README.md", context, StringComparison.Ordinal);
        Assert.DoesNotContain(".aide", context, StringComparison.Ordinal);
        Assert.Empty(session.Calls);
    }
}
