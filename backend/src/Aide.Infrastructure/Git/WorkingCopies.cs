using System.Collections.Concurrent;
using System.Text;
using Aide.Domain.Execution;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.Git;

/// <summary>
/// Who commits and tags in a working copy (author and committer). The default is AIDE (<see cref="GitCli.AuthorName"/>);
/// FR-82: GitHub App projects commit as the App bot (<c>&lt;slug&gt;[bot]</c> with its noreply email).
/// </summary>
public sealed record GitCommitIdentity(string Name, string Email);

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

/// <summary>
/// FR-42, FR-43: the outcome of merging the project branch into the default branch: merged (the default branch's new head,
/// which may be its old head when the branch was already merged), or not merged because of <see cref="Conflicts"/>.
/// </summary>
public sealed record MergeResult(bool Merged, string? Sha, IReadOnlyList<string> Conflicts)
{
    public static MergeResult Success(string sha) => new(true, sha, []);

    public static MergeResult Conflicted(IReadOnlyList<string> conflicts) => new(false, null, conflicts);
}

/// <summary>A release tag (FR-42) and the commit it points at (peeled).</summary>
public sealed record ReleaseTagRef(string Tag, string Commit);

/// <summary>
/// FR-73 (Local import): the repository copied into a project's working copy: its checked-out default branch, that branch's
/// head commit and every tag with its (peeled) commit.
/// </summary>
public sealed record ImportedRepository(string DefaultBranch, string HeadSha, IReadOnlyList<ReleaseTagRef> Tags);

/// <summary>A release tag looked up on a remote (FR-64), or the failed command.</summary>
public sealed record RemoteReleaseTags(bool Ok, IReadOnlyList<ReleaseTagRef> Tags, GitCommandResult? Failure);

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
    /// FR-73, §8 Testing (Local import): copies the Git repository at <paramref name="sourcePath"/> (a directory on the
    /// server, already checked to be under the import root) into the project's working copy with its history and tags
    /// (<c>git clone --no-hardlinks</c>), then removes the clone's remote so the working copy stands alone like any Local
    /// repository. The working copy must not exist yet. Throws <see cref="WorkingCopyException"/> when the source isn't a
    /// repository with a commit, <see cref="GitCommandException"/> when git fails.
    /// </summary>
    Task<ImportedRepository> ImportLocalAsync(Guid projectId, string sourcePath, CancellationToken cancellationToken);

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
    /// §4.11: the paths of the files in commit <paramref name="commit"/> (path order, at most <paramref name="maxEntries"/>,
    /// unsafe paths skipped), or <c>null</c> when there is no repository or the commit isn't in it.
    /// </summary>
    Task<IReadOnlyList<string>?> ListPathsAtCommitAsync(Guid projectId, string commit, int maxEntries, CancellationToken cancellationToken);

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

    /// <summary>
    /// FR-42 (Local): merges <paramref name="branch"/> into <paramref name="defaultBranch"/> with a merge commit
    /// (<c>--no-ff</c>, hooks off) titled <paramref name="message"/>. On a conflict the conflicting files are listed and the
    /// merge is aborted (FR-43), leaving the default branch unchanged. The project branch is checked out again either way.
    /// </summary>
    Task<MergeResult> MergeIntoDefaultAsync(Guid projectId, string defaultBranch, string branch, string message, CancellationToken cancellationToken);

    /// <summary>
    /// FR-43 (GitHub): the files that conflict when <paramref name="headRef"/> is merged into <paramref name="baseRef"/>, found
    /// with a trial merge that is aborted (nothing is committed); empty when they merge cleanly.
    /// </summary>
    Task<IReadOnlyList<string>> TrialMergeConflictsAsync(Guid projectId, string baseRef, string headRef, CancellationToken cancellationToken);

    /// <summary>The commit <paramref name="revision"/> names in the working copy, or <c>null</c> (no repository, unknown revision).</summary>
    Task<string?> ResolveCommitAsync(Guid projectId, string revision, CancellationToken cancellationToken);

    /// <summary>Whether commit <paramref name="ancestor"/> is <paramref name="descendant"/> or one of its ancestors (false if either is unknown).</summary>
    Task<bool> IsAncestorAsync(Guid projectId, string ancestor, string descendant, CancellationToken cancellationToken);

    /// <summary>
    /// FR-65 (Local merged by someone else): the commit on <paramref name="defaultRef"/>'s first-parent history that brought
    /// <paramref name="head"/> in — the oldest first-parent commit that contains it (the merge commit; <paramref name="head"/>
    /// itself after a fast-forward); <c>null</c> when <paramref name="defaultRef"/> doesn't contain it.
    /// </summary>
    Task<string?> FindMergeCommitAsync(Guid projectId, string defaultRef, string head, CancellationToken cancellationToken);

    /// <summary>The local tag <paramref name="tag"/> with its (peeled) commit; <c>null</c> when it or the repository doesn't exist.</summary>
    Task<ReleaseTagRef?> TagAsync(Guid projectId, string tag, CancellationToken cancellationToken);

    /// <summary>
    /// FR-42: creates the annotated release tag <paramref name="tag"/> on <paramref name="commit"/>. A local tag of that
    /// name pointing elsewhere (left by an attempt whose push failed) is replaced: only published tags are never moved.
    /// </summary>
    Task CreateTagAsync(Guid projectId, string tag, string commit, string message, CancellationToken cancellationToken);

    /// <summary>
    /// FR-64 (GitHub): the tag <paramref name="tag"/> on <paramref name="remote"/> (zero or one entry, peeled commit), or a
    /// failure (redacted git output). A tag found there is fetched, so its commit can be inspected locally.
    /// </summary>
    Task<RemoteReleaseTags> RemoteTagAsync(Guid projectId, string tag, GitRemote remote, CancellationToken cancellationToken);

    /// <summary>FR-42 (GitHub): pushes the release tag to <paramref name="remote"/>. The result is returned (redacted), not thrown.</summary>
    Task<GitCommandResult> PushTagAsync(Guid projectId, string tag, GitRemote remote, CancellationToken cancellationToken);

    /// <summary>
    /// FR-82: commits, merges and tags in the project's working copy are made as <paramref name="identity"/> from now on
    /// (<c>null</c>: AIDE). Set by the provider each time it prepares the working copy (in memory; prepared again after a
    /// restart before any commit).
    /// </summary>
    void UseCommitIdentity(Guid projectId, GitCommitIdentity? identity);
}

internal sealed partial class WorkingCopies(IWorkspaceStore workspaces, IGitCli git, ILogger<WorkingCopies> logger) : IWorkingCopies
{
    public const string RulesPath = ".aide/rules.md";
    public const string InitialCommitMessage = "Initial commit";

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    /// <summary>FR-82: the commit identity per working-copy root (absent: AIDE).</summary>
    private readonly ConcurrentDictionary<string, GitCommitIdentity> _identities = new(StringComparer.Ordinal);

    public void UseCommitIdentity(Guid projectId, GitCommitIdentity? identity)
    {
        var root = PathFor(projectId);
        if (identity is null)
        {
            _identities.TryRemove(root, out _);
        }
        else
        {
            _identities[root] = identity;
        }
    }

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

    public async Task<ImportedRepository> ImportLocalAsync(Guid projectId, string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        return await WithLockAsync(projectId, async () =>
        {
            var source = Path.GetFullPath(sourcePath);
            var isWorkTree = Directory.Exists(Path.Combine(source, ".git"));
            var isBare = !isWorkTree && File.Exists(Path.Combine(source, "HEAD")) && Directory.Exists(Path.Combine(source, "objects"));
            if (!isWorkTree && !isBare)
            {
                throw new WorkingCopyException("The directory to import is not a Git repository.");
            }

            var path = PathFor(projectId);
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw new WorkingCopyException("The project's working copy already exists.");
            }

            Directory.CreateDirectory(workspaces.Root);
            var safeSource = source.Replace('\\', '/');
            await RunAsync(
                workspaces.Root,
                ["-c", "safe.directory=" + safeSource, "clone", "-q", "--no-hardlinks", "--origin", GitRemote.Name, "--", source, projectId.ToString("D")],
                cancellationToken);
            await RunAsync(path, ["remote", "remove", GitRemote.Name], cancellationToken);
            var branch = (await RunAsync(path, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken, allowFailure: true)).StandardOutput.Trim();
            var head = (await RunAsync(path, ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], cancellationToken, allowFailure: true)).StandardOutput.Trim();
            if (branch.Length == 0 || head.Length == 0)
            {
                throw new WorkingCopyException("The repository to import has no commit on its default branch.");
            }

            var listed = await RunAsync(path, ["for-each-ref", "refs/tags", "--format=%(refname:strip=2)%09%(*objectname)%09%(objectname)"], cancellationToken);
            var tags = new List<ReleaseTagRef>();
            foreach (var line in listed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length == 3 && parts[0].Length > 0)
                {
                    tags.Add(new ReleaseTagRef(parts[0], parts[1].Length > 0 ? parts[1] : parts[2]));
                }
            }

            LogImported(logger, projectId, tags.Count);
            return new ImportedRepository(branch, head, tags);
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

    public Task<IReadOnlyList<string>?> ListPathsAtCommitAsync(Guid projectId, string commit, int maxEntries, CancellationToken cancellationToken)
    {
        RequireRevision(commit);
        if (!IsRepository(projectId))
        {
            return Task.FromResult<IReadOnlyList<string>?>(null);
        }

        return WithLockAsync<IReadOnlyList<string>?>(projectId, async () =>
        {
            var root = PathFor(projectId);
            if (await ResolveCoreAsync(root, commit, cancellationToken) is not { } resolved)
            {
                return null;
            }

            var listing = await RunAsync(root, ["ls-tree", "-r", "-z", "--name-only", "--full-tree", resolved], cancellationToken);
            return
            [
                .. listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Where(p => !PathRules.IsUnsafe(PathRules.Normalize(p)))
                    .Take(maxEntries),
            ];
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

    public Task<MergeResult> MergeIntoDefaultAsync(Guid projectId, string defaultBranch, string branch, string message, CancellationToken cancellationToken)
    {
        RequireBranchName(defaultBranch);
        RequireBranchName(branch);
        ArgumentException.ThrowIfNullOrEmpty(message);
        return WithLockAsync(projectId, async () =>
        {
            var root = RequireRepository(projectId);
            foreach (var name in new[] { defaultBranch, branch })
            {
                if (!await RefExistsAsync(root, name, cancellationToken))
                {
                    throw new WorkingCopyException($"The branch '{name}' doesn't exist in the working copy.");
                }
            }

            await DiscardLeftoversAsync(root, cancellationToken);
            await RunAsync(root, ["checkout", "-q", defaultBranch, "--"], cancellationToken);
            try
            {
                var merge = await RunAsync(
                    root, ["merge", "--no-ff", "--no-edit", "--no-verify", "-m", message, $"refs/heads/{branch}"], cancellationToken, allowFailure: true);
                if (!merge.Succeeded)
                {
                    var conflicts = await UnmergedAsync(root, cancellationToken);
                    await RunAsync(root, ["merge", "--abort"], cancellationToken, allowFailure: true);
                    if (conflicts.Count == 0)
                    {
                        var error = merge.StandardError.Trim();
                        throw new GitCommandException($"git merge failed (exit {merge.ExitCode}): {(error.Length > 500 ? error[..500] : error)}");
                    }

                    LogMergeConflict(logger, projectId, conflicts.Count);
                    return MergeResult.Conflicted(conflicts);
                }

                var sha = (await RunAsync(root, ["rev-parse", "HEAD"], cancellationToken)).StandardOutput.Trim();
                LogMerged(logger, projectId, branch, defaultBranch, sha);
                return MergeResult.Success(sha);
            }
            finally
            {
                // Whatever happened, leave a clean working copy on the project branch (execution and review read it).
                await DiscardLeftoversAsync(root, CancellationToken.None);
                await RunAsync(root, ["checkout", "-q", branch, "--"], CancellationToken.None, allowFailure: true);
            }
        });
    }

    public Task<IReadOnlyList<string>> TrialMergeConflictsAsync(Guid projectId, string baseRef, string headRef, CancellationToken cancellationToken)
    {
        RequireRevision(baseRef);
        RequireRevision(headRef);
        return WithLockAsync<IReadOnlyList<string>>(projectId, async () =>
        {
            var root = RequireRepository(projectId);
            var previous = await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken, allowFailure: true);
            await DiscardLeftoversAsync(root, cancellationToken);
            await RunAsync(root, ["checkout", "-q", "--detach", baseRef, "--"], cancellationToken);
            try
            {
                var merge = await RunAsync(root, ["merge", "--no-commit", "--no-ff", "--no-verify", headRef], cancellationToken, allowFailure: true);
                IReadOnlyList<string> conflicts = merge.Succeeded ? [] : await UnmergedAsync(root, cancellationToken);
                await RunAsync(root, ["merge", "--abort"], cancellationToken, allowFailure: true);
                return conflicts;
            }
            finally
            {
                await DiscardLeftoversAsync(root, CancellationToken.None);
                if (previous.Succeeded)
                {
                    await RunAsync(root, ["checkout", "-q", previous.StandardOutput.Trim(), "--"], CancellationToken.None, allowFailure: true);
                }
            }
        });
    }

    public Task<string?> ResolveCommitAsync(Guid projectId, string revision, CancellationToken cancellationToken)
    {
        RequireRevision(revision);
        if (!IsRepository(projectId))
        {
            return Task.FromResult<string?>(null);
        }

        return WithLockAsync(projectId, () => ResolveCoreAsync(PathFor(projectId), revision, cancellationToken));
    }

    public Task<bool> IsAncestorAsync(Guid projectId, string ancestor, string descendant, CancellationToken cancellationToken)
    {
        RequireRevision(ancestor);
        RequireRevision(descendant);
        if (!IsRepository(projectId))
        {
            return Task.FromResult(false);
        }

        return WithLockAsync(projectId, async () =>
            (await RunAsync(PathFor(projectId), ["merge-base", "--is-ancestor", ancestor, descendant], cancellationToken, allowFailure: true)).Succeeded);
    }

    /// <summary>The longest first-parent history searched for the merge commit.</summary>
    public const int MaxFirstParentCommits = 20_000;

    public Task<string?> FindMergeCommitAsync(Guid projectId, string defaultRef, string head, CancellationToken cancellationToken)
    {
        RequireRevision(defaultRef);
        RequireRevision(head);
        if (!IsRepository(projectId))
        {
            return Task.FromResult<string?>(null);
        }

        return WithLockAsync(projectId, async () =>
        {
            var root = PathFor(projectId);
            if (await ResolveCoreAsync(root, head, cancellationToken) is not { } headSha
                || !(await RunAsync(root, ["merge-base", "--is-ancestor", headSha, defaultRef], cancellationToken, allowFailure: true)).Succeeded)
            {
                return null;
            }

            // The first-parent chain of the default branch down to (not into) the head's history, newest first. Containing the
            // head is monotonic along it, so a binary search finds the oldest commit that contains it: the merge.
            var listed = await RunAsync(
                root,
                ["rev-list", "--first-parent", $"--max-count={MaxFirstParentCommits.ToString(System.Globalization.CultureInfo.InvariantCulture)}", defaultRef, $"^{headSha}"],
                cancellationToken);
            var chain = listed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (chain.Length == 0)
            {
                return headSha; // the default branch is at the head (fast-forward)
            }

            int low = 0, high = chain.Length - 1, oldest = -1;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                if ((await RunAsync(root, ["merge-base", "--is-ancestor", headSha, chain[middle]], cancellationToken, allowFailure: true)).Succeeded)
                {
                    oldest = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (oldest < 0)
            {
                return headSha;
            }

            // A fast-forward put the head itself on the chain: its first child there contains it through its first parent.
            var firstParent = await ResolveCoreAsync(root, $"{chain[oldest]}^1", cancellationToken);
            return firstParent == headSha ? headSha : chain[oldest];
        });
    }

    public Task<ReleaseTagRef?> TagAsync(Guid projectId, string tag, CancellationToken cancellationToken)
    {
        RequireTagName(tag);
        if (!IsRepository(projectId))
        {
            return Task.FromResult<ReleaseTagRef?>(null);
        }

        return WithLockAsync<ReleaseTagRef?>(projectId, async () =>
            await ResolveCoreAsync(PathFor(projectId), $"refs/tags/{tag}", cancellationToken) is { } commit ? new ReleaseTagRef(tag, commit) : null);
    }

    public Task CreateTagAsync(Guid projectId, string tag, string commit, string message, CancellationToken cancellationToken)
    {
        RequireTagName(tag);
        RequireRevision(commit);
        ArgumentException.ThrowIfNullOrEmpty(message);
        return WithLockAsync(projectId, async () =>
        {
            var root = RequireRepository(projectId);
            var existing = await ResolveCoreAsync(root, $"refs/tags/{tag}", cancellationToken);
            if (existing is not null)
            {
                if (existing == await ResolveCoreAsync(root, commit, cancellationToken))
                {
                    return 0;
                }

                await RunAsync(root, ["tag", "-d", tag], cancellationToken);
            }

            await RunAsync(root, ["tag", "-a", tag, "-m", message, commit], cancellationToken);
            LogTagged(logger, projectId, tag, commit);
            return 0;
        });
    }

    public Task<RemoteReleaseTags> RemoteTagAsync(Guid projectId, string tag, GitRemote remote, CancellationToken cancellationToken)
    {
        RequireTagName(tag);
        ArgumentNullException.ThrowIfNull(remote);
        return WithLockAsync(projectId, async () =>
        {
            var root = RequireRepository(projectId);
            var listed = await git.RunAsync(root, ["ls-remote", "--tags", GitRemote.Name, $"refs/tags/{tag}"], remote.ConfigEnvironment, cancellationToken);
            if (!listed.Succeeded)
            {
                return new RemoteReleaseTags(false, [], Redacted(listed, remote));
            }

            // "<sha>\trefs/tags/<tag>" and, for an annotated tag, "<commit>\trefs/tags/<tag>^{}" (the peeled commit).
            string? direct = null;
            string? peeled = null;
            foreach (var line in listed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                if (parts[1] == $"refs/tags/{tag}")
                {
                    direct = parts[0];
                }
                else if (parts[1] == $"refs/tags/{tag}^{{}}")
                {
                    peeled = parts[0];
                }
            }

            if (direct is null)
            {
                return new RemoteReleaseTags(true, [], null);
            }

            var fetched = await git.RunAsync(
                root, ["fetch", "-q", "--no-tags", GitRemote.Name, $"+refs/tags/{tag}:refs/tags/{tag}"], remote.ConfigEnvironment, cancellationToken);
            return fetched.Succeeded
                ? new RemoteReleaseTags(true, [new ReleaseTagRef(tag, peeled ?? direct)], null)
                : new RemoteReleaseTags(false, [], Redacted(fetched, remote));
        });
    }

    public Task<GitCommandResult> PushTagAsync(Guid projectId, string tag, GitRemote remote, CancellationToken cancellationToken)
    {
        RequireTagName(tag);
        ArgumentNullException.ThrowIfNull(remote);
        return WithLockAsync(projectId, async () =>
        {
            var root = RequireRepository(projectId);
            var result = await git.RunAsync(
                root, ["push", "--porcelain", "--no-verify", GitRemote.Name, $"refs/tags/{tag}:refs/tags/{tag}"], remote.ConfigEnvironment, cancellationToken);
            if (result.Succeeded)
            {
                LogTagPushed(logger, projectId, tag);
            }

            return Redacted(result, remote);
        });
    }

    private static GitCommandResult Redacted(GitCommandResult result, GitRemote remote) =>
        result with { StandardError = remote.Redact(result.StandardError), StandardOutput = remote.Redact(result.StandardOutput) };

    private async Task<string?> ResolveCoreAsync(string root, string revision, CancellationToken cancellationToken)
    {
        var resolved = await RunAsync(root, ["rev-parse", "--verify", "--quiet", $"{revision}^{{commit}}"], cancellationToken, allowFailure: true);
        return resolved.Succeeded ? resolved.StandardOutput.Trim() : null;
    }

    /// <summary>The unmerged (conflicting) paths of the merge in progress, in git's order.</summary>
    private async Task<IReadOnlyList<string>> UnmergedAsync(string root, CancellationToken cancellationToken)
    {
        var unmerged = await RunAsync(root, ["diff", "--name-only", "--diff-filter=U", "-z"], cancellationToken, allowFailure: true);
        return [.. unmerged.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)];
    }

    private static void RequireTagName(string tag)
    {
        if (!Domain.Releases.ReleaseRules.IsReleaseTag(tag))
        {
            throw new WorkingCopyException("The tag name is not a release tag.");
        }
    }

    /// <summary>A revision passed to git: a commit id or a fully qualified ref, never something git could read as an option.</summary>
    private static void RequireRevision(string revision)
    {
        ArgumentException.ThrowIfNullOrEmpty(revision);
        var commitId = revision.Length is >= 7 and <= 64 && revision.All(char.IsAsciiHexDigitLower);
        var qualifiedRef = revision.StartsWith("refs/", StringComparison.Ordinal)
            && !revision.Contains("..", StringComparison.Ordinal)
            && revision.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.');
        if (!commitId && !qualifiedRef)
        {
            throw new WorkingCopyException("The revision is not a commit id or a qualified ref.");
        }
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
        var result = _identities.TryGetValue(root, out var identity)
            ? await git.RunAsync(root, arguments, IdentityEnvironment(identity), cancellationToken)
            : await git.RunAsync(root, arguments, cancellationToken);
        if (!result.Succeeded && !allowFailure)
        {
            var error = result.StandardError.Trim();
            throw new GitCommandException($"git {arguments[0]} failed (exit {result.ExitCode}): {(error.Length > 500 ? error[..500] : error)}");
        }

        return result;
    }

    /// <summary>The author and committer of commits, merges and tags (git's identity variables win over <c>user.name</c>).</summary>
    private static Dictionary<string, string> IdentityEnvironment(GitCommitIdentity identity) => new(StringComparer.Ordinal)
    {
        ["GIT_AUTHOR_NAME"] = identity.Name,
        ["GIT_AUTHOR_EMAIL"] = identity.Email,
        ["GIT_COMMITTER_NAME"] = identity.Name,
        ["GIT_COMMITTER_EMAIL"] = identity.Email,
    };

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported a local repository with {TagCount} tags into the working copy of project {ProjectId}")]
    private static partial void LogImported(ILogger logger, Guid projectId, int tagCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cloned the repository of project {ProjectId} into its working copy")]
    private static partial void LogCloned(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pushed branch {Branch} of project {ProjectId}")]
    private static partial void LogPushed(ILogger logger, Guid projectId, string branch);

    [LoggerMessage(Level = LogLevel.Information, Message = "Committed step {StepId} of project {ProjectId} as {Sha} on {Branch}")]
    private static partial void LogCommitted(ILogger logger, Guid projectId, Guid stepId, string sha, string branch);

    [LoggerMessage(Level = LogLevel.Information, Message = "Step {StepId} of project {ProjectId} is already committed as {Sha}")]
    private static partial void LogAlreadyCommitted(ILogger logger, Guid projectId, Guid stepId, string sha);

    [LoggerMessage(Level = LogLevel.Information, Message = "Merged {Branch} into {DefaultBranch} for project {ProjectId} as {Sha}")]
    private static partial void LogMerged(ILogger logger, Guid projectId, string branch, string defaultBranch, string sha);

    [LoggerMessage(Level = LogLevel.Information, Message = "Merging the project branch of project {ProjectId} conflicts in {Count} files; the merge was aborted")]
    private static partial void LogMergeConflict(ILogger logger, Guid projectId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tagged {Tag} on {Commit} for project {ProjectId}")]
    private static partial void LogTagged(ILogger logger, Guid projectId, string tag, string commit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pushed tag {Tag} of project {ProjectId}")]
    private static partial void LogTagPushed(ILogger logger, Guid projectId, string tag);
}
