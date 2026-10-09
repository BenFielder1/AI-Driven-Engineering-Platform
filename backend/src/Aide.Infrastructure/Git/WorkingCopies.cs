using System.Collections.Concurrent;
using System.Text;
using Aide.Domain.Execution;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.Git;

/// <summary>A file as committed: its repository path and content.</summary>
public sealed record CommittedFile(string Path, string Content);

/// <summary>A step's commit (FR-34): its SHA, whether it already existed (a redelivery), and the files it holds.</summary>
public sealed record StepCommitResult(string Sha, bool AlreadyCommitted, IReadOnlyList<CommittedFile> Files);

/// <summary>
/// A remote repository and how to reach it (FR-35): the clean URL (never holding credentials) and the per-command git
/// configuration (auth header, protocol allow-list) passed through the environment. <see cref="ToString"/> never shows the
/// configuration, and <see cref="Redact"/> removes the secrets from any text.
/// </summary>
public sealed class GitRemote
{
    public const string Name = "origin";

    private readonly IReadOnlyList<string> _secrets;

    public GitRemote(string url, IReadOnlyDictionary<string, string> configEnvironment, IEnumerable<string> secrets)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(configEnvironment);
        ArgumentNullException.ThrowIfNull(secrets);
        Url = url;
        ConfigEnvironment = configEnvironment;
        _secrets = [.. secrets.Where(v => !string.IsNullOrEmpty(v)).OrderByDescending(v => v.Length)];
    }

    public string Url { get; }

    public IReadOnlyDictionary<string, string> ConfigEnvironment { get; }

    /// <summary><paramref name="text"/> with every secret replaced by <c>***</c>.</summary>
    public string Redact(string? text)
    {
        var result = text ?? string.Empty;
        foreach (var secret in _secrets)
        {
            result = result.Replace(secret, "***", StringComparison.Ordinal);
        }

        return result;
    }

    public override string ToString() => Url;
}

/// <summary>A commit between the default branch and the project branch.</summary>
public sealed record ComparedCommit(string Sha, string Subject);

/// <summary>A file changed between the default branch and the project branch; <see cref="Status"/> is added, modified, deleted, renamed, copied or changed.</summary>
public sealed record ComparedFile(string Path, string Status);

/// <summary>FR-35 (Local "PR"): the project branch compared with the default branch (<c>base...head</c>).</summary>
public sealed record BranchComparison(string BaseSha, string HeadSha, IReadOnlyList<ComparedCommit> Commits, IReadOnlyList<ComparedFile> Files, bool Truncated);

/// <summary>A working copy can't be used safely (e.g. a path resolves outside it or through a link).</summary>
public sealed class WorkingCopyException : Exception
{
    public WorkingCopyException()
    {
    }

    public WorkingCopyException(string message)
        : base(message)
    {
    }

    public WorkingCopyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The project working copies on the data volume (§5: <c>{Workspaces:Root}/{projectId}</c>), whatever the provider (FR-31,
/// FR-34, FR-35). The provider puts the repository there (Local: <c>git init</c>; GitHub: a clone, phase 9); everything
/// after that — reading the declared files, writing and committing an accepted step — is the same local Git work.
/// Operations on one project are serialised (single API replica, §9). Paths are resolved strictly inside the working copy
/// and never through a symbolic link or junction.
/// </summary>
public interface IWorkingCopies
{
    string PathFor(Guid projectId);

    /// <summary>The working copy has a <c>.git</c> directory.</summary>
    bool IsRepository(Guid projectId);

    /// <summary>Creates the repository if absent: <c>git init</c> on <paramref name="defaultBranch"/> with an empty initial commit (Local provider).</summary>
    Task InitAsync(Guid projectId, string defaultBranch, CancellationToken cancellationToken);

    /// <summary>Creates <paramref name="branch"/> from <paramref name="defaultBranch"/> if it doesn't exist and checks it out.</summary>
    Task CheckoutBranchAsync(Guid projectId, string defaultBranch, string branch, CancellationToken cancellationToken);

    /// <summary>
    /// FR-35 (GitHub): clones <paramref name="remote"/> into the working copy if it isn't a repository yet; otherwise points
    /// <c>origin</c> at the remote's (clean) URL and fetches. The remote's credentials are used for this command only.
    /// </summary>
    Task SyncFromRemoteAsync(Guid projectId, GitRemote remote, CancellationToken cancellationToken);

    /// <summary>
    /// FR-35 (GitHub): checks out <paramref name="branch"/>; if it doesn't exist locally it is created from the remote project
    /// branch if one exists (an earlier push), else from the remote default branch. Never resets an existing local branch.
    /// </summary>
    Task CheckoutRemoteBranchAsync(Guid projectId, string defaultBranch, string branch, CancellationToken cancellationToken);

    /// <summary>FR-34: pushes <paramref name="branch"/> to the same branch on <paramref name="remote"/> (fast-forward only). The result is returned, not thrown.</summary>
    Task<GitCommandResult> PushBranchAsync(Guid projectId, string branch, GitRemote remote, CancellationToken cancellationToken);

    /// <summary>
    /// FR-35/FR-36: the commits on <paramref name="branch"/> not on <paramref name="baseRef"/> (newest first) and the files
    /// changed since their merge base, capped at <paramref name="maxCommits"/>/<paramref name="maxFiles"/>. <c>null</c> when
    /// there is no repository or either ref is missing.
    /// </summary>
    Task<BranchComparison?> CompareAsync(Guid projectId, string baseRef, string branch, int maxCommits, int maxFiles, CancellationToken cancellationToken);

    /// <summary>
    /// FR-38: the files committed on <paramref name="branch"/> (path and size, path order, at most
    /// <paramref name="maxEntries"/>; unsafe paths skipped), or <c>null</c> when there is no repository or no such branch.
    /// </summary>
    Task<IReadOnlyList<Domain.Review.RepositoryFile>?> ListFilesAsync(Guid projectId, string branch, int maxEntries, CancellationToken cancellationToken);

    /// <summary>
    /// FR-38: the committed content of <paramref name="paths"/> on <paramref name="branch"/> (not the working tree, so
    /// uncommitted leftovers are never reviewed). Missing and binary files (a NUL byte) are left out.
    /// </summary>
    Task<IReadOnlyList<Domain.Review.SourceFile>> ReadCommittedAsync(Guid projectId, string branch, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>FR-31: the current content of <paramref name="paths"/> in the working copy.</summary>
    Task<IReadOnlyList<WorkingFileContent>> ReadAsync(Guid projectId, IEnumerable<string> paths, CancellationToken cancellationToken);

    /// <summary>The repository's rules file (<c>.aide/rules.md</c>), or <c>null</c>.</summary>
    Task<string?> ReadRulesAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>D17: the commit on <paramref name="branch"/> that carries the step's trailer, or <c>null</c>.</summary>
    Task<StepCommitResult?> FindStepCommitAsync(Guid projectId, string branch, Guid stepId, CancellationToken cancellationToken);

    /// <summary>
    /// FR-34: writes <paramref name="files"/> and commits them on <paramref name="branch"/> with <paramref name="message"/>,
    /// unless the step's commit already exists (then that commit is returned, nothing is written).
    /// </summary>
    Task<StepCommitResult> CommitStepAsync(
        Guid projectId, string branch, Guid stepId, IReadOnlyList<ProducedFile> files, StepCommitMessage message, CancellationToken cancellationToken);
}

internal sealed partial class WorkingCopies(IWorkspaceStore workspaces, IGitCli git, ILogger<WorkingCopies> logger) : IWorkingCopies
{
    public const string RulesPath = ".aide/rules.md";
    public const string InitialCommitMessage = "Initial commit";

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    public string PathFor(Guid projectId) => workspaces.PathFor(projectId);

    public bool IsRepository(Guid projectId) => Directory.Exists(Path.Combine(PathFor(projectId), ".git"));

    public async Task InitAsync(Guid projectId, string defaultBranch, CancellationToken cancellationToken)
    {
        RequireBranchName(defaultBranch);
        await WithLockAsync(projectId, async () =>
        {
            var path = PathFor(projectId);
            var dotGit = Path.Combine(path, ".git");
            if (File.Exists(dotGit) || (Directory.Exists(dotGit) && IsLink(new DirectoryInfo(dotGit))))
            {
                throw new WorkingCopyException("The working copy's .git is not a plain directory.");
            }

            Directory.CreateDirectory(path);
            if (!Directory.Exists(dotGit))
            {
                await RunAsync(path, ["init", "-q", "-b", defaultBranch], cancellationToken);
                LogInitialised(logger, projectId, defaultBranch);
            }

            if (await RefExistsAsync(path, defaultBranch, cancellationToken))
            {
                return 0;
            }

            // The empty initial commit belongs only on a repository that has no commit yet, with HEAD still the unborn
            // default branch (right after `git init`, or an init interrupted before its commit). An existing history
            // without the default branch is never patched with an empty commit on whatever is checked out.
            var hasCommits = (await RunAsync(path, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken, allowFailure: true)).Succeeded;
            var head = await RunAsync(path, ["symbolic-ref", "--quiet", "HEAD"], cancellationToken, allowFailure: true);
            if (hasCommits || !head.Succeeded || head.StandardOutput.Trim() != $"refs/heads/{defaultBranch}")
            {
                throw new WorkingCopyException(
                    $"The working copy has history but no default branch '{defaultBranch}'. Create it, or correct the default branch in the project's Git settings.");
            }

            await RunAsync(path, ["commit", "-q", "--no-verify", "--allow-empty", "-m", InitialCommitMessage], cancellationToken);
            return 0;
        });
    }

    public async Task CheckoutBranchAsync(Guid projectId, string defaultBranch, string branch, CancellationToken cancellationToken)
    {
        RequireBranchName(defaultBranch);
        RequireBranchName(branch);
        await WithLockAsync(projectId, async () =>
        {
            await CheckoutCoreAsync(RequireRepository(projectId), defaultBranch, branch, cancellationToken);
            return 0;
        });
    }

    public async Task SyncFromRemoteAsync(Guid projectId, GitRemote remote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        await WithLockAsync(projectId, async () =>
        {
            var path = PathFor(projectId);
            var dotGit = Path.Combine(path, ".git");
            if (File.Exists(dotGit) || (Directory.Exists(dotGit) && IsLink(new DirectoryInfo(dotGit))))
            {
                throw new WorkingCopyException("The working copy's .git is not a plain directory.");
            }

            if (!Directory.Exists(dotGit))
            {
                if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                {
                    throw new WorkingCopyException("The working copy folder holds files but no repository; an admin needs to remove it.");
                }

                Directory.CreateDirectory(workspaces.Root);
                await RunRemoteAsync(
                    workspaces.Root,
                    ["clone", "-q", "--no-tags", "--origin", GitRemote.Name, "--", remote.Url, projectId.ToString("D")],
                    remote,
                    cancellationToken);
                LogCloned(logger, projectId);
                return 0;
            }

            var current = await RunAsync(path, ["remote", "get-url", GitRemote.Name], cancellationToken, allowFailure: true);
            if (!current.Succeeded)
            {
                await RunAsync(path, ["remote", "add", GitRemote.Name, remote.Url], cancellationToken);
            }
            else if (current.StandardOutput.Trim() != remote.Url)
            {
                await RunAsync(path, ["remote", "set-url", GitRemote.Name, remote.Url], cancellationToken);
            }

            await RunRemoteAsync(path, ["fetch", "-q", "--no-tags", "--prune", GitRemote.Name], remote, cancellationToken);
            return 0;
        });
    }

    public async Task CheckoutRemoteBranchAsync(Guid projectId, string defaultBranch, string branch, CancellationToken cancellationToken)
    {
        RequireBranchName(defaultBranch);
        RequireBranchName(branch);
        await WithLockAsync(projectId, async () =>
        {
            var root = RequireRepository(projectId);
            if (!await RefExistsAsync(root, branch, cancellationToken))
            {
                var remoteBranch = $"refs/remotes/{GitRemote.Name}/{branch}";
                var remoteDefault = $"refs/remotes/{GitRemote.Name}/{defaultBranch}";
                string start;
                if (await RefExistsAsync(root, remoteBranch, cancellationToken, qualified: true))
                {
                    start = remoteBranch;
                }
                else if (await RefExistsAsync(root, remoteDefault, cancellationToken, qualified: true))
                {
                    start = remoteDefault;
                }
                else
                {
                    throw new WorkingCopyException(
                        $"The default branch '{defaultBranch}' doesn't exist in the repository. Push an initial commit to it, or correct the default branch in the project's Git settings.");
                }

                await RunAsync(root, ["branch", "--no-track", branch, start], cancellationToken);
            }

            await DiscardLeftoversAsync(root, cancellationToken);
            var head = await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken, allowFailure: true);
            if (!head.Succeeded || head.StandardOutput.Trim() != branch)
            {
                await RunAsync(root, ["checkout", "-q", branch, "--"], cancellationToken);
            }

            return 0;
        });
    }

    public Task<GitCommandResult> PushBranchAsync(Guid projectId, string branch, GitRemote remote, CancellationToken cancellationToken)
    {
        RequireBranchName(branch);
        ArgumentNullException.ThrowIfNull(remote);
        return WithLockAsync(projectId, async () =>
        {
            var root = RequireRepository(projectId);
            var result = await git.RunAsync(
                root,
                ["push", "--porcelain", "--no-verify", GitRemote.Name, $"refs/heads/{branch}:refs/heads/{branch}"],
                remote.ConfigEnvironment,
                cancellationToken);
            if (result.Succeeded)
            {
                LogPushed(logger, projectId, branch);
            }

            return result with { StandardError = remote.Redact(result.StandardError), StandardOutput = remote.Redact(result.StandardOutput) };
        });
    }

    public Task<BranchComparison?> CompareAsync(Guid projectId, string baseRef, string branch, int maxCommits, int maxFiles, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseRef);
        RequireBranchName(branch);
        if (!IsRepository(projectId))
        {
            return Task.FromResult<BranchComparison?>(null);
        }

        return WithLockAsync<BranchComparison?>(projectId, async () =>
        {
            var root = PathFor(projectId);
            var head = await RunAsync(root, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}"], cancellationToken, allowFailure: true);
            var start = await RunAsync(root, ["rev-parse", "--verify", "--quiet", $"{baseRef}^{{commit}}"], cancellationToken, allowFailure: true);
            if (!head.Succeeded || !start.Succeeded)
            {
                return null;
            }

            var headSha = head.StandardOutput.Trim();
            var baseSha = start.StandardOutput.Trim();
            var log = await RunAsync(
                root,
                ["log", "-z", "--format=%H%x1f%s", $"--max-count={maxCommits + 1}", $"{baseSha}..{headSha}", "--"],
                cancellationToken);
            var commits = log.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim('\n').Split('\u001f', 2))
                .Where(parts => parts.Length == 2)
                .Select(parts => new ComparedCommit(parts[0], parts[1]))
                .ToList();

            var diff = await RunAsync(root, ["diff", "--name-status", "-z", "--no-renames", $"{baseSha}...{headSha}", "--"], cancellationToken);
            var fields = diff.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var files = new List<ComparedFile>();
            for (var i = 0; i + 1 < fields.Length; i += 2)
            {
                files.Add(new ComparedFile(fields[i + 1], FileStatus(fields[i])));
            }

            var truncated = commits.Count > maxCommits || files.Count > maxFiles;
            return new BranchComparison(baseSha, headSha, [.. commits.Take(maxCommits)], [.. files.Take(maxFiles)], truncated);
        });
    }

    private static string FileStatus(string code) => code.Length == 0 ? "changed" : code[0] switch
    {
        'A' => "added",
        'M' => "modified",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        _ => "changed",
    };

    /// <summary>A command that talks to the remote; git's error text is redacted before it can reach an exception or a log.</summary>
    private async Task RunRemoteAsync(string directory, IReadOnlyList<string> arguments, GitRemote remote, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(directory, arguments, remote.ConfigEnvironment, cancellationToken);
        if (!result.Succeeded)
        {
            var error = remote.Redact(result.StandardError.Trim());
            throw new GitCommandException($"git {arguments[0]} failed (exit {result.ExitCode}): {(error.Length > 500 ? error[..500] : error)}");
        }
    }

    public Task<IReadOnlyList<Domain.Review.RepositoryFile>?> ListFilesAsync(Guid projectId, string branch, int maxEntries, CancellationToken cancellationToken)
    {
        RequireBranchName(branch);
        if (!IsRepository(projectId))
        {
            return Task.FromResult<IReadOnlyList<Domain.Review.RepositoryFile>?>(null);
        }

        return WithLockAsync<IReadOnlyList<Domain.Review.RepositoryFile>?>(projectId, async () =>
        {
            var root = PathFor(projectId);
            if (!await RefExistsAsync(root, branch, cancellationToken))
            {
                return null;
            }

            var listing = await RunAsync(root, ["ls-tree", "-r", "-z", "--long", "--full-tree", $"refs/heads/{branch}"], cancellationToken);
            var files = new List<Domain.Review.RepositoryFile>();
            foreach (var entry in listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                // "<mode> <type> <object> <size>\t<path>"
                var tab = entry.IndexOf('\t', StringComparison.Ordinal);
                if (tab < 0)
                {
                    continue;
                }

                var meta = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var path = entry[(tab + 1)..];
                if (meta.Length < 4 || meta[1] != "blob" || !long.TryParse(meta[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size)
                    || PathRules.IsUnsafe(PathRules.Normalize(path)))
                {
                    continue;
                }

                files.Add(new Domain.Review.RepositoryFile(path, size));
                if (files.Count >= maxEntries)
                {
                    break;
                }
            }

            return files;
        });
    }

    public Task<IReadOnlyList<Domain.Review.SourceFile>> ReadCommittedAsync(Guid projectId, string branch, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        RequireBranchName(branch);
        ArgumentNullException.ThrowIfNull(paths);
        return WithLockAsync<IReadOnlyList<Domain.Review.SourceFile>>(projectId, async () =>
        {
            var root = RequireRepository(projectId);
            var files = new List<Domain.Review.SourceFile>();
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                if (PathRules.IsUnsafe(PathRules.Normalize(path)))
                {
                    continue;
                }

                var shown = await RunAsync(root, ["cat-file", "blob", $"refs/heads/{branch}:{path}"], cancellationToken, allowFailure: true);
                if (shown.Succeeded && shown.StandardOutput.IndexOf('\0', StringComparison.Ordinal) < 0)
                {
                    files.Add(new Domain.Review.SourceFile(path, shown.StandardOutput));
                }
            }

            return files;
        });
    }

    public async Task<IReadOnlyList<WorkingFileContent>> ReadAsync(Guid projectId, IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var root = PathFor(projectId);
        var result = new List<WorkingFileContent>();
        foreach (var path in paths.Select(PathRules.Normalize).Distinct(StringComparer.Ordinal))
        {
            result.Add(await ReadOneAsync(root, path, cancellationToken));
        }

        return result;
    }

    public async Task<string?> ReadRulesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var file = await ReadOneAsync(PathFor(projectId), RulesPath, cancellationToken, allowProtected: true);
        return file.State == WorkingFileState.Present ? file.Content : null;
    }

    public Task<StepCommitResult?> FindStepCommitAsync(Guid projectId, string branch, Guid stepId, CancellationToken cancellationToken)
    {
        RequireBranchName(branch);
        return WithLockAsync(projectId, () => FindCoreAsync(RequireRepository(projectId), branch, stepId, cancellationToken));
    }

    public Task<StepCommitResult> CommitStepAsync(
        Guid projectId, string branch, Guid stepId, IReadOnlyList<ProducedFile> files, StepCommitMessage message, CancellationToken cancellationToken)
    {
        RequireBranchName(branch);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(message);
        return WithLockAsync(projectId, async () =>
        {
            var root = RequireRepository(projectId);
            if (await FindCoreAsync(root, branch, stepId, cancellationToken) is { } existing)
            {
                LogAlreadyCommitted(logger, projectId, stepId, existing.Sha);
                return existing;
            }

            await DiscardLeftoversAsync(root, cancellationToken);
            var head = await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken, allowFailure: true);
            if (!head.Succeeded || head.StandardOutput.Trim() != branch)
            {
                await RunAsync(root, ["checkout", "-q", branch, "--"], cancellationToken);
            }

            var written = new List<CommittedFile>();
            foreach (var file in files)
            {
                var relative = PathRules.Normalize(file.Path);
                var full = Resolve(root, relative, allowProtected: true);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, file.Content, new UTF8Encoding(false), cancellationToken);
                written.Add(new CommittedFile(relative, file.Content));
            }

            if (written.Count > 0)
            {
                await RunAsync(root, ["--literal-pathspecs", "add", "--", .. written.Select(f => f.Path)], cancellationToken);
            }

            await RunAsync(root, ["commit", "-q", "--no-verify", "--allow-empty", "-m", message.Subject, "-m", message.Body], cancellationToken);
            var sha = (await RunAsync(root, ["rev-parse", "HEAD"], cancellationToken)).StandardOutput.Trim();
            LogCommitted(logger, projectId, stepId, sha, branch);
            return new StepCommitResult(sha, false, written);
        });
    }

    private async Task<StepCommitResult?> FindCoreAsync(string root, string branch, Guid stepId, CancellationToken cancellationToken)
    {
        if (!await RefExistsAsync(root, branch, cancellationToken))
        {
            return null;
        }

        var log = await RunAsync(
            root,
            ["log", $"refs/heads/{branch}", "--fixed-strings", $"--grep={Domain.Execution.CodeGeneration.StepTrailerLine(stepId)}", "--format=%H", "--"],
            cancellationToken);
        var sha = log.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (sha is null)
        {
            return null;
        }

        var names = await RunAsync(root, ["diff-tree", "--no-commit-id", "--name-only", "-r", "-z", "--root", sha], cancellationToken);
        var files = new List<CommittedFile>();
        foreach (var path in names.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var shown = await RunAsync(root, ["show", $"{sha}:{path}"], cancellationToken, allowFailure: true);
            if (shown.Succeeded)
            {
                files.Add(new CommittedFile(path, shown.StandardOutput));
            }
        }

        return new StepCommitResult(sha, true, files);
    }

    private async Task CheckoutCoreAsync(string root, string defaultBranch, string branch, CancellationToken cancellationToken)
    {
        if (!await RefExistsAsync(root, branch, cancellationToken))
        {
            if (!await RefExistsAsync(root, defaultBranch, cancellationToken))
            {
                throw new WorkingCopyException($"The default branch '{defaultBranch}' doesn't exist in the working copy.");
            }

            await RunAsync(root, ["branch", branch, $"refs/heads/{defaultBranch}"], cancellationToken);
        }

        await DiscardLeftoversAsync(root, cancellationToken);
        var head = await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken, allowFailure: true);
        if (!head.Succeeded || head.StandardOutput.Trim() != branch)
        {
            await RunAsync(root, ["checkout", "-q", branch, "--"], cancellationToken);
        }
    }

    /// <summary>
    /// Drops uncommitted changes and untracked files (e.g. left by a step that crashed between writing and committing), so
    /// they never reach a prompt or a commit. Runs under the project lock, through the hardened git command (no hooks).
    /// </summary>
    private async Task DiscardLeftoversAsync(string root, CancellationToken cancellationToken)
    {
        if (!(await RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken, allowFailure: true)).Succeeded)
        {
            return;
        }

        await RunAsync(root, ["reset", "--hard", "-q"], cancellationToken);
        await RunAsync(root, ["clean", "-fdq"], cancellationToken);
    }

    private async Task<bool> RefExistsAsync(string root, string branch, CancellationToken cancellationToken, bool qualified = false) =>
        (await RunAsync(root, ["rev-parse", "--verify", "--quiet", qualified ? branch : $"refs/heads/{branch}"], cancellationToken, allowFailure: true)).Succeeded;

    private static async Task<WorkingFileContent> ReadOneAsync(string root, string path, CancellationToken cancellationToken, bool allowProtected = true)
    {
        string full;
        try
        {
            full = Resolve(root, path, allowProtected);
        }
        catch (WorkingCopyException)
        {
            return new WorkingFileContent(path, WorkingFileState.Missing);
        }

        var info = new FileInfo(full);
        if (!info.Exists)
        {
            return new WorkingFileContent(path, WorkingFileState.Missing);
        }

        if (info.Length > Domain.Execution.CodeGeneration.MaxExistingFileBytes)
        {
            return new WorkingFileContent(path, WorkingFileState.TooLarge, Length: info.Length);
        }

        var bytes = await File.ReadAllBytesAsync(full, cancellationToken);
        return Array.IndexOf(bytes, (byte)0) >= 0
            ? new WorkingFileContent(path, WorkingFileState.Binary, Length: bytes.Length)
            : new WorkingFileContent(path, WorkingFileState.Present, new UTF8Encoding(false).GetString(bytes), bytes.Length);
    }

    /// <summary>
    /// The full path of a repository-relative path: refused when unsafe (<see cref="PathRules.IsUnsafe"/>, which includes
    /// any <c>.git</c> segment), outside the working copy, or reached through a symbolic link or junction.
    /// </summary>
    internal static string Resolve(string root, string relative, bool allowProtected)
    {
        var normalized = PathRules.Normalize(relative);
        if (PathRules.IsUnsafe(normalized) || (!allowProtected && PathRules.IsProtected(normalized)))
        {
            throw new WorkingCopyException($"'{normalized}' is not a safe repository path.");
        }

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnder(full, rootFull))
        {
            throw new WorkingCopyException($"'{normalized}' resolves outside the working copy.");
        }

        // Defence in depth behind PathRules: whatever aliasing the file system applies (trailing dots or spaces, case),
        // the resolved path must not be the repository's .git or anything inside it.
        var dotGit = Path.Combine(rootFull, ".git");
        if (string.Equals(full, dotGit, PathComparison) || IsUnder(full, dotGit))
        {
            throw new WorkingCopyException($"'{normalized}' resolves into the repository's .git directory.");
        }

        var current = rootFull;
        foreach (var segment in normalized.Split('/'))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.Exists && IsLink(entry))
            {
                throw new WorkingCopyException($"'{normalized}' goes through a link.");
            }
        }

        return full;
    }

    /// <summary>Windows and macOS file systems are case-insensitive by default, so paths there compare ignoring case.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsUnder(string full, string directory) =>
        full.StartsWith(directory + Path.DirectorySeparatorChar, PathComparison);

    private static bool IsLink(FileSystemInfo entry) =>
        entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private string RequireRepository(Guid projectId)
    {
        if (!IsRepository(projectId))
        {
            throw new WorkingCopyException("The working copy has not been prepared.");
        }

        return PathFor(projectId);
    }

    private static void RequireBranchName(string branch)
    {
        if (!Domain.Projects.ProjectSettingsRules.IsValidBranchName(branch))
        {
            throw new WorkingCopyException("The branch name is not a valid Git branch name.");
        }
    }

    private async Task<GitCommandResult> RunAsync(string root, IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool allowFailure = false)
    {
        var result = await git.RunAsync(root, arguments, cancellationToken);
        if (!result.Succeeded && !allowFailure)
        {
            var error = result.StandardError.Trim();
            throw new GitCommandException($"git {arguments[0]} failed (exit {result.ExitCode}): {(error.Length > 500 ? error[..500] : error)}");
        }

        return result;
    }

    private static async Task<T> WithLockAsync<T>(Guid projectId, Func<Task<T>> action)
    {
        var gate = Locks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Initialised the local repository of project {ProjectId} on {DefaultBranch}")]
    private static partial void LogInitialised(ILogger logger, Guid projectId, string defaultBranch);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cloned the repository of project {ProjectId} into its working copy")]
    private static partial void LogCloned(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pushed branch {Branch} of project {ProjectId}")]
    private static partial void LogPushed(ILogger logger, Guid projectId, string branch);

    [LoggerMessage(Level = LogLevel.Information, Message = "Committed step {StepId} of project {ProjectId} as {Sha} on {Branch}")]
    private static partial void LogCommitted(ILogger logger, Guid projectId, Guid stepId, string sha, string branch);

    [LoggerMessage(Level = LogLevel.Information, Message = "Step {StepId} of project {ProjectId} is already committed as {Sha}")]
    private static partial void LogAlreadyCommitted(ILogger logger, Guid projectId, Guid stepId, string sha);
}
