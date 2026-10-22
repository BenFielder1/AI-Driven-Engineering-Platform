using System.Text;
using Aide.Domain.RepoTools;
using Aide.Infrastructure;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.RepoTools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.UnitTests.RepoTools;

/// <summary>
/// FR-71, D-V2-9: credential-free snapshots of a project's working copy, with the real git CLI in a temporary workspace
/// root: no <c>.git</c>, no <c>.aide/</c>, no links; cached by (project, commit); a dirty working tree is copied; a missing
/// commit is fetched through the remote's per-command configuration, which never lands in the snapshot.
/// </summary>
public sealed class RepoSnapshotTests : IDisposable
{
    private readonly string _workspaces = Path.Combine(Path.GetTempPath(), "aide-snapshot-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;
    private readonly IGitCli _git;
    private readonly IWorkingCopies _workingCopies;
    private readonly IRepoSnapshotProvider _snapshots;

    public RepoSnapshotTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspaces:Root"] = _workspaces })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAideInfrastructure();
        _services = services.BuildServiceProvider();
        _git = _services.GetRequiredService<IGitCli>();
        _workingCopies = _services.GetRequiredService<IWorkingCopies>();
        _snapshots = _services.GetRequiredService<IRepoSnapshotProvider>();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _services.Dispose();
        Delete(_workspaces);
    }

    private async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var result = await _git.RunAsync(directory, arguments, Ct);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)}: {result.StandardError}");
        return result.StandardOutput.Trim();
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A Local-style repository in the project's working copy with one commit; returns its SHA.</summary>
    private async Task<(Guid ProjectId, string Root, string Sha)> RepositoryAsync()
    {
        var projectId = Guid.CreateVersion7();
        var root = _workingCopies.PathFor(projectId);
        Directory.CreateDirectory(root);
        await GitAsync(root, "init", "-q", "-b", "main");
        Write(root, "README.md", "# Widgets\n");
        Write(root, "src/app.ts", "export const v = 1;\n");
        Write(root, ".aide/rules.md", "humans only\n");
        Write(root, "link-target.txt", "../../../../etc/passwd");
        await GitAsync(root, "add", "-A");

        // A symbolic link pointing out of the repository, recorded straight in the index (no OS privileges needed).
        var blob = await GitAsync(root, "hash-object", "-w", "link-target.txt");
        await GitAsync(root, "update-index", "--add", "--cacheinfo", $"120000,{blob},escape");
        await GitAsync(root, "commit", "-q", "--no-verify", "-m", "initial");

        // With core.symlinks=false (GitCli) the link is checked out as a plain file holding its target, so the tree is clean.
        await GitAsync(root, "checkout", "-q", "--", "escape");
        return (projectId, root, await GitAsync(root, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task FR71_a_commit_snapshot_has_the_files_but_no_git_aide_or_links()
    {
        var (projectId, _, sha) = await RepositoryAsync();

        var outcome = await _snapshots.ForCommitAsync(projectId, sha, null, Ct);

        Assert.True(outcome.Ok, outcome.Reason);
        using var snapshot = outcome.Snapshot!;
        Assert.Equal((sha, false), (snapshot.Commit, snapshot.WorkingTree));
        Assert.Equal("# Widgets\n", File.ReadAllText(Path.Combine(snapshot.Root, "README.md")));
        Assert.True(File.Exists(Path.Combine(snapshot.Root, "src", "app.ts")));
        Assert.False(Directory.Exists(Path.Combine(snapshot.Root, ".git")));
        Assert.False(File.Exists(Path.Combine(snapshot.Root, ".git")));
        Assert.False(Directory.Exists(Path.Combine(snapshot.Root, ".aide")));
        Assert.False(Path.Exists(Path.Combine(snapshot.Root, "escape")));
        Assert.DoesNotContain(_workingCopies.PathFor(projectId), snapshot.Root, StringComparison.OrdinalIgnoreCase);

        var tools = new RepoToolSession(snapshot.Root);
        var read = await tools.ExecuteAsync(new LlmToolCall("1", RepoToolNames.ReadFile, """{"path":"escape"}"""), Ct);
        Assert.StartsWith("NOT_FOUND:", read.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR71_commit_snapshots_are_cached_by_project_and_commit()
    {
        var (projectId, root, sha) = await RepositoryAsync();
        Write(root, "src/app.ts", "export const v = 2;\n");
        await GitAsync(root, "commit", "-q", "-a", "--no-verify", "-m", "second");
        var second = await GitAsync(root, "rev-parse", "HEAD");

        using var a = (await _snapshots.ForCommitAsync(projectId, sha, null, Ct)).Snapshot!;
        using var b = (await _snapshots.ForCommitAsync(projectId, sha.ToUpperInvariant()[..12], null, Ct)).Snapshot!;
        using var c = (await _snapshots.ForCommitAsync(projectId, second, null, Ct)).Snapshot!;

        Assert.Equal(a.Root, b.Root);
        Assert.NotEqual(a.Root, c.Root);
        Assert.Equal("export const v = 1;\n", File.ReadAllText(Path.Combine(a.Root, "src", "app.ts")));
        Assert.Equal("export const v = 2;\n", File.ReadAllText(Path.Combine(c.Root, "src", "app.ts")));
    }

    [Fact]
    public async Task FR71_a_missing_or_malformed_commit_gives_a_reason_not_a_snapshot()
    {
        var (projectId, _, _) = await RepositoryAsync();

        var missing = await _snapshots.ForCommitAsync(projectId, new string('a', 40), null, Ct);
        var malformed = await _snapshots.ForCommitAsync(projectId, "--output=/tmp/x", null, Ct);
        var unprepared = await _snapshots.ForWorkingTreeAsync(Guid.CreateVersion7(), Ct);

        Assert.False(missing.Ok);
        Assert.Contains("not in the project's working copy", missing.Reason, StringComparison.Ordinal);
        Assert.False(malformed.Ok);
        Assert.False(unprepared.Ok);
    }

    [Fact]
    public async Task FR71_a_clean_working_tree_is_its_HEAD_snapshot_and_a_dirty_one_is_copied_and_deleted_on_dispose()
    {
        var (projectId, root, sha) = await RepositoryAsync();

        using (var clean = (await _snapshots.ForWorkingTreeAsync(projectId, Ct)).Snapshot!)
        using (var head = (await _snapshots.ForCommitAsync(projectId, sha, null, Ct)).Snapshot!)
        {
            Assert.Equal((head.Root, false), (clean.Root, clean.WorkingTree));
        }

        Write(root, "src/app.ts", "export const v = 'uncommitted';\n");
        Write(root, "src/new.ts", "new file\n");
        Write(root, ".aide/notes.md", "hidden\n");
        var outcome = await _snapshots.ForWorkingTreeAsync(projectId, Ct);
        Assert.True(outcome.Ok, outcome.Reason);
        var dirty = outcome.Snapshot!;
        var dirtyRoot = dirty.Root;

        Assert.Equal((sha, true), (dirty.Commit, dirty.WorkingTree));
        Assert.Equal("export const v = 'uncommitted';\n", File.ReadAllText(Path.Combine(dirtyRoot, "src", "app.ts")));
        Assert.True(File.Exists(Path.Combine(dirtyRoot, "src", "new.ts")));
        Assert.False(Directory.Exists(Path.Combine(dirtyRoot, ".git")));
        Assert.False(Directory.Exists(Path.Combine(dirtyRoot, ".aide")));

        dirty.Dispose();
        Assert.False(Directory.Exists(dirtyRoot));
    }

    [Fact]
    public async Task FR71_D_V2_9_a_missing_commit_is_fetched_with_per_command_credentials_that_never_reach_the_snapshot()
    {
        // The "remote": a bare repository on disk, reached with the same per-command GIT_CONFIG_* mechanism GitHub uses.
        var (sourceId, sourceRoot, sha) = await RepositoryAsync();
        var bare = Path.Combine(_workspaces, "remote-" + Guid.NewGuid().ToString("N") + ".git");
        await GitAsync(_workspaces, "clone", "-q", "--bare", "--", sourceRoot, bare);
        // Built at runtime so no "Basic <base64>" literal sits in the source for secret scanners to flag.
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes("secret-token-value"));
        var header = "Authorization: Basic " + basic;
        var remote = new GitRemote(
            new Uri(bare).AbsoluteUri,
            new Dictionary<string, string>
            {
                ["GIT_CONFIG_COUNT"] = "2",
                ["GIT_CONFIG_KEY_0"] = "http.extraHeader",
                ["GIT_CONFIG_VALUE_0"] = header,
                ["GIT_CONFIG_KEY_1"] = "protocol.file.allow",
                ["GIT_CONFIG_VALUE_1"] = "always",
            },
            [header]);
        var projectId = Guid.CreateVersion7();
        Assert.NotEqual(sourceId, projectId);

        var outcome = await _snapshots.ForCommitAsync(projectId, sha, remote, Ct);

        Assert.True(outcome.Ok, outcome.Reason);
        using var snapshot = outcome.Snapshot!;
        Assert.Equal("# Widgets\n", File.ReadAllText(Path.Combine(snapshot.Root, "README.md")));
        Assert.False(Directory.Exists(Path.Combine(snapshot.Root, ".git")));
        var config = await File.ReadAllTextAsync(Path.Combine(_workingCopies.PathFor(projectId), ".git", "config"), Ct);
        Assert.DoesNotContain(basic, config, StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(snapshot.Root, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(basic, await File.ReadAllTextAsync(file, Encoding.UTF8, Ct), StringComparison.Ordinal);
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
