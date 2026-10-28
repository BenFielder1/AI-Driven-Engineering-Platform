using System.Text.RegularExpressions;
using Aide.Api.Features.GitHub;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Domain.Stack;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.GitHub;
using Microsoft.Extensions.Options;

namespace Aide.Api.Features.Stack;

/// <summary>
/// FR-118 (<c>POST /api/stack/detect</c>): reads a repository's file list and the manifests
/// <see cref="StackDetector.ManifestsToRead"/> asks for at a ref, through the request's Git provider, and runs the pure
/// <see cref="StackDetector.Detect"/>. Nothing is stored. GitHub (<c>github_app</c> with the caller's connection,
/// <c>github_pat</c> with the request's token, if any) is read through the REST API; <c>local</c> reads a repository under
/// <c>Git:Local:ImportRoot</c> with git, without copying it.
/// </summary>
public sealed partial class StackDetectionService(
    GitHubConnections connections,
    GitHubApi api,
    IGitCli git,
    IOptions<LocalGitOptions> localGit,
    ILogger<StackDetectionService> logger)
{
    /// <summary>The most files listed (beyond it the result is <c>truncated</c>).</summary>
    public const int MaxFiles = GitHubApi.MaxTreeEntries;

    /// <summary>The VCS host the detector fixes the VCS layer to: GitHub for these providers (Local defaults to GitHub, R5).</summary>
    public const string VcsHost = "github";

    private sealed record RepoSnapshot(string Ref, string Commit, IReadOnlyList<string> Files, IReadOnlyDictionary<string, string> Manifests, bool Truncated);

    /// <summary>
    /// The detected stack. <c>VALIDATION</c> (malformed request), <c>GIT_NOT_CONNECTED</c> (<c>github_app</c> without a valid
    /// GitHub connection), <c>NOT_FOUND</c> (no such repository or ref, or the caller can't see it), <c>FORBIDDEN</c> or
    /// <c>UPSTREAM_ERROR</c> (GitHub refused or failed). GitHub rate limits propagate (429).
    /// </summary>
    public async Task<Result<StackDetectionDto>> DetectAsync(Guid userId, DetectStackGitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problems = new List<string>();
        if (request.InstanceId is not null)
        {
            problems.Add("git.instanceId: Only used for GitLab instances.");
        }

        if (request.Ref is { } gitRef && !IsSafeRef(gitRef.Trim()))
        {
            problems.Add("git.ref: Use a branch, tag or commit name.");
        }

        if (request.Token is not null && request.Provider != GitProvider.GithubPat)
        {
            problems.Add("git.token: Only used with github_pat.");
        }

        if (problems.Count > 0)
        {
            return Invalid(problems);
        }

        var wantedRef = string.IsNullOrWhiteSpace(request.Ref) ? null : request.Ref.Trim();
        var snapshot = request.Provider == GitProvider.Local
            ? await LocalAsync(request.Repo, wantedRef, cancellationToken)
            : await GitHubAsync(userId, request, wantedRef, cancellationToken);
        if (snapshot.IsFailure)
        {
            return Result.Failure<StackDetectionDto>(snapshot.Error!);
        }

        var read = snapshot.Value;
        var detection = StackDetector.Detect(read.Files, read.Manifests, VcsHost);
        LogDetected(logger, request.Provider, detection.Suggestions.Count, detection.Conflicts.Count, read.Manifests.Count);
        return Result.Success(new StackDetectionDto(
            read.Ref,
            read.Commit,
            [.. detection.Suggestions.Select(s => new StackDetectedChoiceDto(s.Choice, s.Option, s.Path, s.Mode, s.Evidence))],
            [.. detection.Conflicts.Select(c => new StackDetectionConflictDto(
                c.Choice, [.. c.Candidates.Select(x => new StackDetectionCandidateDto(x.Option, x.Path, x.Evidence))]))],
            read.Truncated));
    }

    // ---------- GitHub ----------

    private async Task<Result<RepoSnapshot>> GitHubAsync(Guid userId, DetectStackGitRequest request, string? wantedRef, CancellationToken cancellationToken)
    {
        var repo = request.Repo.Trim();
        var parsed = repo.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? ProjectSettingsRules.TryParseGitHubRepo(ProjectSettingsRules.NormalizeRepoUrl(GitProvider.GithubPat, repo), out var name)
            : ProjectSettingsRules.TryParseRepoFullName(repo, out name);
        if (!parsed)
        {
            return Invalid<RepoSnapshot>(["git.repo: Use owner/name, e.g. acme/todo-app."]);
        }

        string? token = null;
        if (request.Provider == GitProvider.GithubApp)
        {
            var session = await connections.SessionAsync(userId, cancellationToken);
            if (session.IsFailure)
            {
                return Result.Failure<RepoSnapshot>(NotConnected(session.Error!));
            }

            token = session.Value.Token;
        }
        else if (!string.IsNullOrWhiteSpace(request.Token))
        {
            token = request.Token.Trim();
        }

        try
        {
            var gitRef = wantedRef;
            if (gitRef is null)
            {
                var repository = await api.FindRepositoryAsync(token ?? string.Empty, name.Owner, name.Name, cancellationToken);
                if (repository is null)
                {
                    return RepoNotFound();
                }

                gitRef = repository.DefaultBranch;
            }

            var tree = await api.ReadTreeAsync(token, name.Owner, name.Name, gitRef, cancellationToken);
            if (tree is null)
            {
                return RepoNotFound();
            }

            var (blobs, cut) = RepoFiles.ByNormalisedPath(tree.Files.Select(f => (f.Path, f)), MaxFiles);
            var manifests = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in StackDetector.ManifestsToRead(blobs.Keys))
            {
                // A blob of unknown size isn't read: it could be anything up to GitHub's 100 MB.
                if (blobs.TryGetValue(path, out var file) && file.Size is { } size && size <= StackDetector.MaxManifestLength
                    && await api.ReadBlobTextAsync(token, name.Owner, name.Name, file.BlobSha, cancellationToken) is { } text)
                {
                    manifests[path] = text;
                }
            }

            return Result.Success(new RepoSnapshot(gitRef, tree.CommitSha, [.. blobs.Keys], manifests, tree.Truncated || cut));
        }
        catch (GitHubApiException ex) when (ex.Kind == GitHubFailureKind.Unauthorized)
        {
            if (request.Provider == GitProvider.GithubApp)
            {
                return Result.Failure<RepoSnapshot>(NotConnected(await connections.RejectedAsync(userId, cancellationToken)));
            }

            return Invalid<RepoSnapshot>(["git.token: GitHub refused the token."]);
        }
        catch (GitHubApiException ex)
        {
            LogGitHubFailed(logger, ex.Status, ex.Kind);
            return Result.Failure<RepoSnapshot>(ex.Kind switch
            {
                GitHubFailureKind.Forbidden => new DomainError(ErrorCodes.Forbidden, $"GitHub refused the request. {ex.Message}"),
                GitHubFailureKind.NotFound => RepoNotFound().Error!,
                _ => new DomainError(ErrorCodes.UpstreamError, $"GitHub failed or couldn't be reached. {ex.Message}"),
            });
        }
    }

    /// <summary>§6: a missing or rejected GitHub connection is <c>GIT_NOT_CONNECTED</c> on this endpoint; other errors pass through.</summary>
    private static DomainError NotConnected(DomainError error) =>
        error.Code == ErrorCodes.GitHubNotConnected ? error with { Code = ErrorCodes.GitNotConnected } : error;

    // ---------- Local ----------

    private async Task<Result<RepoSnapshot>> LocalAsync(string repo, string? wantedRef, CancellationToken cancellationToken)
    {
        var source = ImportRules.ResolveLocalSource(localGit.Value.ImportRoot, repo);
        if (source.IsFailure)
        {
            var error = source.Error!;
            return Result.Failure<RepoSnapshot>(error.Code == ErrorCodes.Validation
                ? error with { Details = [.. error.Details.Select(d => d.Replace("git.localSourcePath", "git.repo", StringComparison.Ordinal))] }
                : error);
        }

        var path = Path.GetFullPath(source.Value);

        // The directory itself must be a repository (a work tree or a bare one), like the Local import
        // (WorkingCopies.ImportLocalAsync). Its git directory is passed explicitly, so git never walks up to a parent
        // repository (e.g. the import root inside a checkout).
        var gitDir = Directory.Exists(Path.Combine(path, ".git"))
            ? Path.Combine(path, ".git")
            : File.Exists(Path.Combine(path, "HEAD")) && Directory.Exists(Path.Combine(path, "objects")) ? path : null;
        if (gitDir is null)
        {
            return RepoNotFound();
        }

        var safe = "safe.directory=" + path.Replace('\\', '/');
        Task<GitCommandResult> Git(params string[] arguments) => git.RunAsync(path, ["-c", safe, "--git-dir=" + gitDir, .. arguments], cancellationToken);
        try
        {
            var commit = await Git("rev-parse", "--verify", "--quiet", (wantedRef ?? "HEAD") + "^{commit}");
            if (!commit.Succeeded || commit.StandardOutput.Trim() is not { Length: > 0 } sha)
            {
                return RepoNotFound();
            }

            var gitRef = wantedRef;
            if (gitRef is null)
            {
                var branch = await Git("symbolic-ref", "--quiet", "--short", "HEAD");
                gitRef = branch.Succeeded && branch.StandardOutput.Trim() is { Length: > 0 } b ? b : "HEAD";
            }

            var listing = await Git("ls-tree", "-r", "-z", "--name-only", "--full-tree", sha);
            if (!listing.Succeeded)
            {
                return RepoNotFound();
            }

            var (listed, cut) = RepoFiles.ByNormalisedPath(listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => (p, p)), MaxFiles);
            var files = listed.Keys.ToList();
            var manifests = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var manifest in StackDetector.ManifestsToRead(files))
            {
                if (!listed.TryGetValue(manifest, out var original))
                {
                    continue;
                }

                // The size first, so a huge file is never read into memory.
                var size = await Git("cat-file", "-s", $"{sha}:{original}");
                if (!size.Succeeded || !long.TryParse(size.StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var bytes) || bytes > StackDetector.MaxManifestLength)
                {
                    continue;
                }

                var shown = await Git("cat-file", "blob", $"{sha}:{original}");
                if (shown.Succeeded && shown.StandardOutput.Length <= StackDetector.MaxManifestLength && !shown.StandardOutput.Contains('\0', StringComparison.Ordinal))
                {
                    manifests[manifest] = shown.StandardOutput;
                }
            }

            return Result.Success(new RepoSnapshot(gitRef, sha, files, manifests, cut));
        }
        catch (GitCommandException ex)
        {
            LogLocalFailed(logger, ex.GetType().Name);
            return Result.Failure<RepoSnapshot>(new DomainError(ErrorCodes.UpstreamError, "The repository couldn't be read."));
        }
    }

    // ---------- helpers ----------

    /// <summary>A branch, tag or commit name: no option-like or range syntax, no whitespace or control characters.</summary>
    private static bool IsSafeRef(string gitRef) => gitRef.Length > 0 && !gitRef.StartsWith('-') && !gitRef.Contains("..", StringComparison.Ordinal) && SafeRef().IsMatch(gitRef);

    private static Result<RepoSnapshot> RepoNotFound() =>
        Result.Failure<RepoSnapshot>(new DomainError(ErrorCodes.NotFound, "The repository or ref was not found, or you can't access it."));

    private static Result<StackDetectionDto> Invalid(IReadOnlyList<string> details) => Invalid<StackDetectionDto>(details);

    private static Result<T> Invalid<T>(IReadOnlyList<string> details) =>
        Result.Failure<T>(new DomainError(ErrorCodes.Validation, "The repository to detect is invalid.") { Details = details });

    [GeneratedRegex(@"^[A-Za-z0-9._/@{}^~+-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeRef();

    [LoggerMessage(Level = LogLevel.Information, Message = "Stack detected ({Provider}): {Suggestions} suggestion(s), {Conflicts} conflict(s), {Manifests} manifest(s) read")]
    private static partial void LogDetected(ILogger logger, GitProvider provider, int suggestions, int conflicts, int manifests);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stack detection: GitHub request failed with HTTP {Status} ({Kind})")]
    private static partial void LogGitHubFailed(ILogger logger, int? status, GitHubFailureKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stack detection: the local repository could not be read ({ExceptionType})")]
    private static partial void LogLocalFailed(ILogger logger, string exceptionType);
}
