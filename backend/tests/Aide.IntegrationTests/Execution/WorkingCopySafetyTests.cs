using Aide.Domain.Execution;
using Aide.Infrastructure.Git;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Execution;

/// <summary>
/// FR-34, FR-35, §4.6: the working copy never lets a step write outside it or into the repository's <c>.git</c> (Windows
/// aliases, short names, streams, links), discards crash leftovers before a step, never patches an existing history with
/// an empty commit, and runs git with an allow-listed environment. Uses the real git CLI and the test workspace root.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class WorkingCopySafetyTests(AideApiFactory factory)
{
    private const string Branch = "aide/safety-test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IWorkingCopies WorkingCopies => factory.Services.GetRequiredService<IWorkingCopies>();

    private IGitCli Git => factory.Services.GetRequiredService<IGitCli>();

    public static TheoryData<string> EscapingPaths() =>
    [
        ".git./config",
        ".git /config",
        ".GIT/config",
        "GIT~1/config",
        "a:stream",
        "src/a.ts:stream",
        "../outside.txt",
        "src/../../outside.txt",
        "CON",
        Path.Combine(Path.GetTempPath(), "aide-absolute-escape.txt"),
    ];

    [Theory]
    [MemberData(nameof(EscapingPaths))]
    public async Task FR34_A_step_file_that_would_escape_the_working_copy_or_reach_git_internals_is_refused(string path)
    {
        var id = await NewRepositoryAsync();
        var configBefore = await File.ReadAllTextAsync(Path.Combine(WorkingCopies.PathFor(id), ".git", "config"), Ct);
        var headBefore = await RevParseAsync(id, "HEAD");

        await Assert.ThrowsAsync<WorkingCopyException>(() => CommitAsync(id, (path, "[filter \"x\"]\n\tclean = calc.exe\n")));

        Assert.Equal(configBefore, await File.ReadAllTextAsync(Path.Combine(WorkingCopies.PathFor(id), ".git", "config"), Ct));
        Assert.Equal(headBefore, await RevParseAsync(id, "HEAD"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(WorkingCopies.PathFor(id))!, "outside.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "aide-absolute-escape.txt")));
    }

    [Fact]
    public async Task FR34_A_symlinked_directory_is_never_read_or_written_through()
    {
        var id = await NewRepositoryAsync();
        var outside = Directory.CreateDirectory(Path.Combine(factory.WorkspacesRoot, $"outside-{Guid.NewGuid():N}")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "outside", Ct);
        var link = Path.Combine(WorkingCopies.PathFor(id), "linked");
        if (OperatingSystem.IsWindows())
        {
            // A junction needs no privilege on Windows, unlike a symbolic link; the working copy must refuse both.
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, outside])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            await mklink.WaitForExitAsync(Ct);
            Assert.True(Directory.Exists(link), "The junction could not be created.");
        }
        else
        {
            TryLink(() => Directory.CreateSymbolicLink(link, outside));
        }

        // Reading through the link is refused (the file reads as missing).
        Assert.Equal(WorkingFileState.Missing, (await WorkingCopies.ReadAsync(id, ["linked/keep.txt"], Ct))[0].State);

        // Writing: the untracked link is discarded before the step (only the link, never its target's content), or the
        // write is refused; either way nothing lands outside the working copy.
        try
        {
            await CommitAsync(id, ("linked/x.txt", "escaped"));
        }
        catch (WorkingCopyException)
        {
            // Refused: also fine.
        }

        Assert.False(File.Exists(Path.Combine(outside, "x.txt")));
        Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt"), Ct));
    }

    [Fact]
    public async Task FR34_A_symlinked_file_is_never_read_or_written_through()
    {
        var id = await NewRepositoryAsync();
        var target = Path.Combine(factory.WorkspacesRoot, $"outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(target, "original", Ct);
        TryLink(() => File.CreateSymbolicLink(Path.Combine(WorkingCopies.PathFor(id), "linked.txt"), target));

        Assert.Equal(WorkingFileState.Missing, (await WorkingCopies.ReadAsync(id, ["linked.txt"], Ct))[0].State);
        try
        {
            await CommitAsync(id, ("linked.txt", "overwritten"));
        }
        catch (WorkingCopyException)
        {
            // Refused: also fine.
        }

        Assert.Equal("original", await File.ReadAllTextAsync(target, Ct));
    }

    [Fact]
    public async Task FR34_Leftovers_of_a_crashed_step_are_discarded_before_the_next_step_is_written()
    {
        var id = await NewRepositoryAsync();
        var root = WorkingCopies.PathFor(id);
        await CommitAsync(id, ("src/kept.ts", "committed\n"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "kept.ts"), "half-written\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "leftover.ts"), "uncommitted\n", Ct);

        var read = await WorkingCopies.ReadAsync(id, ["src/kept.ts"], Ct);
        Assert.Equal("half-written\n", read[0].Content);
        await WorkingCopies.CheckoutBranchAsync(id, "main", Branch, Ct);
        var afterPrepare = await WorkingCopies.ReadAsync(id, ["src/kept.ts", "leftover.ts"], Ct);
        var commit = await CommitAsync(id, ("src/next.ts", "next\n"));

        Assert.Equal("committed\n", afterPrepare[0].Content);
        Assert.Equal(WorkingFileState.Missing, afterPrepare[1].State);
        Assert.Equal(["src/next.ts"], commit.Files.Select(f => f.Path));
        var changed = await RunGitAsync(id, "show", "--name-only", "--format=", commit.Sha);
        Assert.Equal("src/next.ts", changed.Trim());
    }

    [Fact]
    public async Task FR35_An_existing_history_without_the_default_branch_fails_preparation_without_an_empty_commit()
    {
        var id = await NewRepositoryAsync();
        await RunGitAsync(id, "branch", "-m", "main", "trunk");
        var before = await RunGitAsync(id, "rev-list", "--all", "--count");

        var failure = await Assert.ThrowsAsync<WorkingCopyException>(() => WorkingCopies.InitAsync(id, "main", Ct));

        Assert.Contains("'main'", failure.Message, StringComparison.Ordinal);
        Assert.Equal(before, await RunGitAsync(id, "rev-list", "--all", "--count"));
    }

    [Fact]
    public async Task FR35_A_new_repository_gets_exactly_one_empty_initial_commit()
    {
        var id = await NewRepositoryAsync();
        await WorkingCopies.InitAsync(id, "main", Ct);

        Assert.Equal("1", (await RunGitAsync(id, "rev-list", "--count", "main")).Trim());
    }

    [Fact]
    public async Task FR35_Git_runs_with_an_allow_listed_environment_without_the_API_secrets()
    {
        var id = await NewRepositoryAsync();
        const string Name = "AIDE_TEST_SECRET_FOR_GIT";
        Environment.SetEnvironmentVariable(Name, "do-not-leak");
        Environment.SetEnvironmentVariable("GIT_DIR", "/tmp/not-this-repo");
        try
        {
            var result = await Git.RunAsync(WorkingCopies.PathFor(id), ["-c", "alias.envdump=!env", "envdump"], Ct);

            Assert.True(result.Succeeded, result.StandardError);
            Assert.DoesNotContain(Name, result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("do-not-leak", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("not-this-repo", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("GIT_CONFIG_NOSYSTEM=1", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, null);
            Environment.SetEnvironmentVariable("GIT_DIR", null);
        }
    }

    // ---------- helpers ----------

    private async Task<Guid> NewRepositoryAsync()
    {
        var id = Guid.NewGuid();
        await WorkingCopies.InitAsync(id, "main", Ct);
        await WorkingCopies.CheckoutBranchAsync(id, "main", Branch, Ct);
        return id;
    }

    private Task<StepCommitResult> CommitAsync(Guid id, params (string Path, string Content)[] files)
    {
        var stepId = Guid.NewGuid();
        var message = CodeGeneration.CommitMessage(1, "Safety", files.Select(f => f.Path), stepId);
        return WorkingCopies.CommitStepAsync(id, Branch, stepId, [.. files.Select(f => new ProducedFile(f.Path, f.Content))], message, Ct);
    }

    private async Task<string> RevParseAsync(Guid id, string rev) => (await RunGitAsync(id, "rev-parse", rev)).Trim();

    private async Task<string> RunGitAsync(Guid id, params string[] arguments)
    {
        var result = await Git.RunAsync(WorkingCopies.PathFor(id), arguments, Ct);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
        return result.StandardOutput.ReplaceLineEndings("\n");
    }

    /// <summary>Creates a link, or skips the test when the OS or account can't (e.g. Windows without Developer Mode).</summary>
    private static void TryLink(Action create)
    {
        try
        {
            create();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic links can't be created here: {ex.GetType().Name}.");
        }
    }
}
