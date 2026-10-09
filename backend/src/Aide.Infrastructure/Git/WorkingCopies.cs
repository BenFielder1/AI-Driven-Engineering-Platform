using System.Collections.Concurrent;
using System.Text;
using Aide.Domain.Execution;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.Git;

/// <summary>A file as committed: its repository path and content.</summary>
public sealed record CommittedFile(string Path, string Content);

/// <summary>A step's commit (FR-34): its SHA, whether it already existed (a redelivery), and the files it holds.</summary>
public sealed record StepCommitResult(string Sha, bool AlreadyCommitted, IReadOnlyList<CommittedFile> Files);

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

    private async Task<bool> RefExistsAsync(string root, string branch, CancellationToken cancellationToken) =>
        (await RunAsync(root, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken, allowFailure: true)).Succeeded;

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Committed step {StepId} of project {ProjectId} as {Sha} on {Branch}")]
    private static partial void LogCommitted(ILogger logger, Guid projectId, Guid stepId, string sha, string branch);

    [LoggerMessage(Level = LogLevel.Information, Message = "Step {StepId} of project {ProjectId} is already committed as {Sha}")]
    private static partial void LogAlreadyCommitted(ILogger logger, Guid projectId, Guid stepId, string sha);
}
