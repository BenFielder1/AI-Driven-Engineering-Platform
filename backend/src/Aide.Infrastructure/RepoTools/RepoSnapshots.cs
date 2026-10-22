using System.Collections.Concurrent;
using System.Formats.Tar;
using Aide.Domain.Execution;
using Aide.Domain.RepoTools;
using Aide.Infrastructure.Git;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.RepoTools;

/// <summary>
/// A read-only, credential-free copy of a project's repository (FR-71, D-V2-9): plain files only, with no <c>.git</c>
/// (so no remote URL, config or credentials), no <c>.aide/</c>, no symbolic links. <see cref="Commit"/> is the commit it was
/// exported from; <see cref="WorkingTree"/> is set when it holds uncommitted changes of the working copy. Dispose it when the
/// run is done: cached snapshots are released for reuse, others are deleted.
/// </summary>
public sealed class RepoSnapshot : IDisposable
{
    private readonly Action? _release;
    private int _disposed;

    internal RepoSnapshot(Guid projectId, string root, string commit, bool workingTree, Action? release)
    {
        ProjectId = projectId;
        Root = root;
        Commit = commit;
        WorkingTree = workingTree;
        _release = release;
    }

    public Guid ProjectId { get; }

    /// <summary>The snapshot directory; pass it to <see cref="RepoToolSession"/>.</summary>
    public string Root { get; }

    /// <summary>The full commit SHA the snapshot was exported from (for a dirty working tree: its <c>HEAD</c>).</summary>
    public string Commit { get; }

    /// <summary>The snapshot includes uncommitted changes of the working copy.</summary>
    public bool WorkingTree { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _release?.Invoke();
        }
    }

    public override string ToString() => $"RepoSnapshot {{ ProjectId = {ProjectId}, Commit = {Commit}, WorkingTree = {WorkingTree} }}";
}

/// <summary>The outcome of asking for a snapshot: the snapshot, or a reason it can't be made (never holding a token or a server path).</summary>
public sealed record RepoSnapshotOutcome(RepoSnapshot? Snapshot, string? Reason)
{
    public bool Ok => Snapshot is not null;

    public static RepoSnapshotOutcome Failed(string reason) => new(null, reason);
}

/// <summary>
/// Credential-free repository snapshots for the repo tools (FR-71). Both read the project's working copy on the data volume,
/// which the Git provider prepares (<see cref="IGitProvider.PrepareWorkingCopyAsync"/>: GitHub clones/fetches with the
/// per-command auth header, Local is the repository itself); no credential is ever written to it or to the snapshot.
/// </summary>
public interface IRepoSnapshotProvider
{
    /// <summary>
    /// The repository at <paramref name="commitSha"/> (the version's base commit: refinement, spec, plan, Repository
    /// Analyst), exported with <c>git archive</c>. If the commit isn't in the working copy and <paramref name="remote"/> is
    /// given (GitHub), the working copy is cloned or fetched first with the remote's per-command credentials
    /// (<see cref="IWorkingCopies.SyncFromRemoteAsync"/>). Cached by (project, commit) until evicted.
    /// </summary>
    Task<RepoSnapshotOutcome> ForCommitAsync(Guid projectId, string commitSha, GitRemote? remote, CancellationToken cancellationToken);

    /// <summary>
    /// The project branch's working tree as checked out in the working copy (execution, review): when it is clean, the
    /// snapshot of its <c>HEAD</c> commit (cached); otherwise a fresh copy of the tracked and untracked, non-ignored files
    /// (deleted on dispose).
    /// </summary>
    Task<RepoSnapshotOutcome> ForWorkingTreeAsync(Guid projectId, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IRepoSnapshotProvider"/> under <c>{temp}/aide-snapshots/{instance}/</c>. A commit snapshot is
/// <c>git archive</c> of the commit, extracted entry by entry: regular files and directories only (links and anything else
/// are skipped), every path checked with the §4.6 rules, <c>.aide/</c> and nested <c>.git</c> left out, and a size cap.
/// At most <see cref="MaxCached"/> commit snapshots are kept; the least recently used one not in use is deleted first, and
/// folders of earlier processes older than a day are removed at start.
/// </summary>
internal sealed partial class RepoSnapshotProvider : IRepoSnapshotProvider, IDisposable
{
    public const int MaxCached = 8;

    /// <summary>Bytes a snapshot may hold; files beyond it are left out.</summary>
    public const long MaxSnapshotBytes = 512L * 1024 * 1024;

    private readonly IWorkingCopies _workingCopies;
    private readonly IGitCli _git;
    private readonly TimeProvider _time;
    private readonly ILogger<RepoSnapshotProvider> _logger;
    private readonly string _root;
    private readonly ConcurrentDictionary<(Guid Project, string Commit), Cached> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RepoSnapshotProvider(IWorkingCopies workingCopies, IGitCli git, TimeProvider time, ILogger<RepoSnapshotProvider> logger)
        : this(workingCopies, git, time, logger, Path.Combine(Path.GetTempPath(), "aide-snapshots"))
    {
    }

    internal RepoSnapshotProvider(IWorkingCopies workingCopies, IGitCli git, TimeProvider time, ILogger<RepoSnapshotProvider> logger, string baseDirectory)
    {
        _workingCopies = workingCopies;
        _git = git;
        _time = time;
        _logger = logger;
        RemoveStale(baseDirectory);
        _root = Path.Combine(baseDirectory, Guid.CreateVersion7().ToString("N"));
    }

    private sealed class Cached(string path)
    {
        public string Path { get; } = path;

        public int Leases { get; set; }

        public long LastUsed { get; set; }
    }

    public async Task<RepoSnapshotOutcome> ForCommitAsync(Guid projectId, string commitSha, GitRemote? remote, CancellationToken cancellationToken)
    {
        if (!IsCommitId(commitSha))
        {
            return RepoSnapshotOutcome.Failed("The commit id isn't a hexadecimal Git object id.");
        }

        commitSha = commitSha.ToLowerInvariant();
        var full = await ResolveAsync(projectId, commitSha, cancellationToken);
        if (full is null && remote is not null)
        {
            try
            {
                await _workingCopies.SyncFromRemoteAsync(projectId, remote, cancellationToken);
            }
            catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException or UnauthorizedAccessException)
            {
                LogFetchFailed(_logger, projectId, remote.Redact(ex.Message));
                return RepoSnapshotOutcome.Failed("The repository could not be fetched for the snapshot.");
            }

            full = await ResolveAsync(projectId, commitSha, cancellationToken);
        }

        return full is null
            ? RepoSnapshotOutcome.Failed($"Commit {commitSha[..Math.Min(12, commitSha.Length)]} is not in the project's working copy; prepare the working copy first.")
            : await CommitSnapshotAsync(projectId, full, cancellationToken);
    }

    public async Task<RepoSnapshotOutcome> ForWorkingTreeAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (!_workingCopies.IsRepository(projectId))
        {
            return RepoSnapshotOutcome.Failed("The project's working copy has not been prepared.");
        }

        var root = _workingCopies.PathFor(projectId);
        var resolved = await _git.RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], cancellationToken);
        var head = resolved.Succeeded ? resolved.StandardOutput.Trim() : null;
        if (head is null || !IsCommitId(head))
        {
            return RepoSnapshotOutcome.Failed("The project's working copy has no commit yet.");
        }

        var status = await _git.RunAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        if (!status.Succeeded)
        {
            return RepoSnapshotOutcome.Failed("The working copy's status could not be read.");
        }

        if (status.StandardOutput.Length == 0)
        {
            return await CommitSnapshotAsync(projectId, head, cancellationToken);
        }

        var files = await _git.RunAsync(root, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], cancellationToken);
        if (!files.Succeeded)
        {
            return RepoSnapshotOutcome.Failed("The working copy's files could not be listed.");
        }

        var target = Path.Combine(_root, "wt-" + Guid.CreateVersion7().ToString("N"));
        try
        {
            Directory.CreateDirectory(target);
            long total = 0;
            foreach (var path in files.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = PathRules.Normalize(path);
                if (RepoToolPaths.IsHidden(normalized))
                {
                    continue;
                }

                string source;
                try
                {
                    source = WorkingCopies.Resolve(root, normalized, allowProtected: true);
                }
                catch (WorkingCopyException)
                {
                    continue;
                }

                var info = new FileInfo(source);
                if (!info.Exists || total + info.Length > MaxSnapshotBytes)
                {
                    continue;
                }

                total += info.Length;
                var destination = Path.Combine(target, normalized.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                File.SetAttributes(destination, FileAttributes.Normal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogExportFailed(_logger, projectId, ex.GetType().Name);
            TryDelete(target);
            return RepoSnapshotOutcome.Failed("The working tree could not be copied for the snapshot.");
        }
        catch (OperationCanceledException)
        {
            // A cancelled copy leaves no partial wt-* folder behind.
            TryDelete(target);
            throw;
        }

        return new RepoSnapshotOutcome(new RepoSnapshot(projectId, target, head, workingTree: true, () => TryDelete(target)), null);
    }

    public void Dispose()
    {
        _gate.Dispose();
        TryDelete(_root);
    }

    private async Task<string?> ResolveAsync(Guid projectId, string revision, CancellationToken cancellationToken)
    {
        try
        {
            return await _workingCopies.ResolveCommitAsync(projectId, revision, cancellationToken);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException)
        {
            return null;
        }
    }

    private async Task<RepoSnapshotOutcome> CommitSnapshotAsync(Guid projectId, string commit, CancellationToken cancellationToken)
    {
        var key = (projectId, commit);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(key, out var hit) && Directory.Exists(hit.Path))
            {
                return Lease(projectId, commit, hit);
            }

            var target = Path.Combine(_root, $"{projectId:N}-{commit}");
            TryDelete(target);
            var exported = await ExportAsync(projectId, commit, target, cancellationToken);
            if (exported is not null)
            {
                TryDelete(target);
                return RepoSnapshotOutcome.Failed(exported);
            }

            // Leased before eviction, so the new snapshot is never the one evicted to make room.
            var cached = new Cached(target) { LastUsed = _time.GetTimestamp() };
            _cache[key] = cached;
            var leased = Lease(projectId, commit, cached);
            Evict();
            LogCreated(_logger, projectId, commit);
            return leased;
        }
        finally
        {
            _gate.Release();
        }
    }

    private RepoSnapshotOutcome Lease(Guid projectId, string commit, Cached cached)
    {
        lock (cached)
        {
            cached.Leases++;
            cached.LastUsed = _time.GetTimestamp();
        }

        return new RepoSnapshotOutcome(
            new RepoSnapshot(projectId, cached.Path, commit, workingTree: false, () =>
            {
                lock (cached)
                {
                    cached.Leases--;
                    cached.LastUsed = _time.GetTimestamp();
                }
            }),
            null);
    }

    /// <summary>Deletes the least recently used snapshots not in use beyond <see cref="MaxCached"/>. Called under the gate.</summary>
    private void Evict()
    {
        var excess = _cache.Count - MaxCached;
        foreach (var (key, entry) in _cache.OrderBy(e => e.Value.LastUsed).ToList())
        {
            if (excess <= 0)
            {
                return;
            }

            lock (entry)
            {
                if (entry.Leases > 0)
                {
                    continue;
                }

                _cache.TryRemove(key, out _);
            }

            TryDelete(entry.Path);
            excess--;
        }
    }

    /// <summary><c>git archive</c> of <paramref name="commit"/> into a tar file, then a checked extraction. Returns a failure reason, or <c>null</c>.</summary>
    private async Task<string?> ExportAsync(Guid projectId, string commit, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, $"{Guid.CreateVersion7():N}.tar");
        try
        {
            var result = await _git.RunAsync(
                _workingCopies.PathFor(projectId),
                ["archive", "--format=tar", "-o", archive, commit],
                cancellationToken);
            if (!result.Succeeded)
            {
                LogExportFailed(_logger, projectId, "git archive exit " + result.ExitCode);
                return "The commit could not be exported for the snapshot.";
            }

            Directory.CreateDirectory(target);
            await ExtractAsync(archive, target, cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is GitCommandException or IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            LogExportFailed(_logger, projectId, ex.GetType().Name);
            return "The commit could not be exported for the snapshot.";
        }
        finally
        {
            TryDeleteFile(archive);
        }
    }

    /// <summary>Extracts regular files and directories only, each path checked with the §4.6 rules and hidden paths skipped.</summary>
    internal static async Task ExtractAsync(string archive, string target, CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        await using var stream = File.OpenRead(archive);
        await using var reader = new TarReader(stream);
        long total = 0;
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            var isFile = entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile;
            var isDirectory = entry.EntryType == TarEntryType.Directory;
            if (!isFile && !isDirectory)
            {
                // Symbolic and hard links, devices, FIFOs, the pax global header: never extracted.
                continue;
            }

            var normalized = PathRules.Normalize(entry.Name);
            if (normalized.Length == 0 || PathRules.IsUnsafe(normalized) || RepoToolPaths.IsHidden(normalized))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            if (entry.DataStream is null || total + entry.Length > MaxSnapshotBytes)
            {
                continue;
            }

            total += entry.Length;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await entry.DataStream.CopyToAsync(output, cancellationToken);
        }
    }

    internal static bool IsCommitId(string? value) =>
        value is { Length: >= 7 and <= 64 } && value.All(char.IsAsciiHexDigit);

    private void RemoveStale(string baseDirectory)
    {
        try
        {
            if (!Directory.Exists(baseDirectory))
            {
                return;
            }

            var cutoff = _time.GetUtcNow().UtcDateTime.AddDays(-1);
            foreach (var directory in Directory.EnumerateDirectories(baseDirectory))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                {
                    TryDelete(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: stale snapshots only take disk space.
        }
    }

    private static void TryDelete(string path)
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
            // Best effort: removed with the instance folder or at the next start.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the repo tool snapshot of project {ProjectId} at {Commit}")]
    private static partial void LogCreated(ILogger logger, Guid projectId, string commit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not export the repo tool snapshot of project {ProjectId}: {Reason}")]
    private static partial void LogExportFailed(ILogger logger, Guid projectId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not fetch project {ProjectId} for a repo tool snapshot: {Reason}")]
    private static partial void LogFetchFailed(ILogger logger, Guid projectId, string reason);
}
