using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Domain.Releases;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Git;

/// <summary>
/// GitHub via its REST API (FR-10, FR-36), through the named HttpClient <see cref="HttpClientName"/> whose base address
/// is <c>GitHub:ApiBaseUrl</c> (default https://api.github.com/; set it for GitHub Enterprise or tests), and git over
/// HTTPS with the project token (FR-34, FR-35) through <see cref="GitHubRemotes"/>.
/// </summary>
internal sealed partial class GitHubGitProvider(
    IHttpClientFactory clients,
    IWorkingCopies workingCopies,
    IWorkspaceStore workspaces,
    IOptions<GitHubGitOptions> options,
    GitHub.GitHubApi api,
    ILogger<GitHubGitProvider> logger) : IGitProvider
{
    public const string HttpClientName = "github";
    public const string ApiBaseUrlKey = "GitHub:ApiBaseUrl";
    public const string DefaultApiBaseUrl = "https://api.github.com/";
    public const string ApiVersion = "2022-11-28";

    private const int MaxReasonLength = 300;

    public GitProvider Kind => GitProvider.GithubPat;

    public async Task<GitTestResult> TestConnectionAsync(GitConnectionSettings settings, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!ProjectSettingsRules.TryParseGitHubRepo(settings.RepoUrl, out var repo))
        {
            return GitTestResult.Failed("The repository URL isn't a GitHub repository URL (https://github.com/owner/repo).");
        }

        var client = clients.CreateClient(HttpClientName);
        var repoPath = $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}";

        bool writable;
        using (var response = await SendAsync(client, repoPath, token, cancellationToken))
        {
            if (response.Error is { } unreachable)
            {
                return GitTestResult.Failed(unreachable);
            }

            if (!response.Message!.IsSuccessStatusCode)
            {
                var reason = await DescribeFailureAsync(response.Message, cancellationToken);
                LogTestFailed(logger, settings.ProjectId, (int)response.Message.StatusCode);
                return GitTestResult.Failed(reason);
            }

            writable = await CanPushAsync(response.Message, cancellationToken);
        }

        bool branchExists;
        var branchPath = $"{repoPath}/branches/{Uri.EscapeDataString(settings.DefaultBranch)}";
        using (var response = await SendAsync(client, branchPath, token, cancellationToken))
        {
            if (response.Error is { } unreachable)
            {
                return GitTestResult.Failed(unreachable, reachable: true, writable: writable);
            }

            if (response.Message!.StatusCode == HttpStatusCode.NotFound)
            {
                branchExists = false;
            }
            else if (!response.Message.IsSuccessStatusCode)
            {
                var reason = await DescribeFailureAsync(response.Message, cancellationToken);
                return GitTestResult.Failed(reason, reachable: true, writable: writable);
            }
            else
            {
                branchExists = true;
            }
        }

        string? failure = null;
        if (!writable)
        {
            failure = string.IsNullOrEmpty(token)
                ? "No access token is saved, so AIDE can't push to the repository. Save a token with Contents: read and write."
                : "The token can't push to this repository. It needs Contents: read and write (or the repo scope).";
        }
        else if (!branchExists)
        {
            failure = $"The default branch '{settings.DefaultBranch}' doesn't exist in {repo.Owner}/{repo.Name}.";
        }

        LogTested(logger, settings.ProjectId, writable, branchExists);
        return new GitTestResult(failure is null, Reachable: true, writable, branchExists, failure);
    }

    /// <summary>
    /// FR-35: clones the repository over HTTPS with the project token into the working copy (or fetches when it is already
    /// there), then checks out the project branch, created from the remote project branch if an earlier run pushed it, else
    /// from the remote default branch.
    /// </summary>
    public async Task<GitOutcome> PrepareWorkingCopyAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Remote(settings, token) is not { } remote)
        {
            return MissingRemote(settings, token);
        }

        try
        {
            await workingCopies.SyncFromRemoteAsync(settings.ProjectId, remote, cancellationToken);
            await workingCopies.CheckoutRemoteBranchAsync(settings.ProjectId, settings.DefaultBranch, branchName, cancellationToken);
            return GitOutcome.Success;
        }
        catch (GitCommandException ex)
        {
            var reason = Scrub(remote, settings.ProjectId, ex.Message);
            LogGitFailed(logger, settings.ProjectId, "prepare", reason);
            return GitOutcome.Failed(reason, GitHubFailures.IsTransientGitError(reason));
        }
        catch (WorkingCopyException ex)
        {
            var reason = Scrub(remote, settings.ProjectId, ex.Message);
            LogGitFailed(logger, settings.ProjectId, "prepare", reason);
            return GitOutcome.Failed(reason, transient: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogGitFailed(logger, settings.ProjectId, "prepare", ex.GetType().Name);
            return GitOutcome.Failed("The working copy could not be written on the AIDE server. Ask an admin to check the server log and the Workspaces:Root volume.");
        }
    }

    /// <summary>FR-34: pushes the project branch (fast-forward only) over HTTPS with the project token.</summary>
    public async Task<GitOutcome> PublishBranchAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Remote(settings, token) is not { } remote)
        {
            return MissingRemote(settings, token);
        }

        try
        {
            var result = await workingCopies.PushBranchAsync(settings.ProjectId, branchName, remote, cancellationToken);
            if (result.Succeeded)
            {
                return GitOutcome.Success;
            }

            var reason = Scrub(remote, settings.ProjectId, $"git push failed (exit {result.ExitCode}): {result.StandardError.Trim()}");
            LogGitFailed(logger, settings.ProjectId, "push", reason);
            return GitOutcome.Failed(reason, GitHubFailures.IsTransientGitError(reason));
        }
        catch (GitCommandException ex)
        {
            var reason = Scrub(remote, settings.ProjectId, ex.Message);
            LogGitFailed(logger, settings.ProjectId, "push", reason);
            return GitOutcome.Failed(reason, GitHubFailures.IsTransientGitError(reason));
        }
        catch (WorkingCopyException ex)
        {
            return GitOutcome.Failed(Scrub(remote, settings.ProjectId, ex.Message), transient: false);
        }
    }

    /// <summary>
    /// FR-36: reuses the open PR from the project branch to the default branch if there is one; otherwise creates it. A
    /// create that races another (422 "already exists") looks the PR up again, so exactly one PR exists.
    /// </summary>
    public async Task<PullRequestOutcome> OpenPullRequestAsync(GitConnectionSettings settings, PullRequestRequest request, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        if (!ProjectSettingsRules.TryParseGitHubRepo(settings.RepoUrl, out var repo))
        {
            return PullRequestOutcome.Failed("The repository URL isn't a GitHub repository URL (https://github.com/owner/repo).", transient: false);
        }

        if (string.IsNullOrEmpty(token))
        {
            return PullRequestOutcome.Failed("No access token is saved, so AIDE can't open the pull request. Save a token with Contents and Pull requests: read and write.", transient: false);
        }

        var client = clients.CreateClient(HttpClientName);
        var pulls = $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/pulls";
        var existing = await FindOpenPullRequestAsync(client, pulls, repo.Owner, request.HeadBranch, settings.DefaultBranch, token, cancellationToken);
        if (existing is not null)
        {
            if (!existing.Ok)
            {
                return existing;
            }

            LogPullRequestReused(logger, settings.ProjectId, existing.Number);
            return await UpdatePullRequestAsync(client, pulls, existing, request, settings.ProjectId, token, cancellationToken);
        }

        var body = new
        {
            title = request.Title,
            head = request.HeadBranch,
            @base = settings.DefaultBranch,
            body = request.Body,
            maintainer_can_modify = false,
        };
        using var sent = await SendAsync(client, HttpMethod.Post, pulls, token, cancellationToken, JsonContent.Create(body));
        if (sent.Error is { } unreachable)
        {
            return PullRequestOutcome.Failed(unreachable, transient: true);
        }

        var response = sent.Message!;
        if (response.StatusCode == HttpStatusCode.Created)
        {
            var created = await ReadPullRequestAsync(response, cancellationToken);
            LogPullRequestOpened(logger, settings.ProjectId, created.Number);
            return created;
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            // Another create won the race (or GitHub already has one): use it. Anything else is GitHub's validation message.
            var raced = await FindOpenPullRequestAsync(client, pulls, repo.Owner, request.HeadBranch, settings.DefaultBranch, token, cancellationToken);
            if (raced is not null)
            {
                return raced;
            }
        }

        var reason = await DescribeFailureAsync(response, cancellationToken);
        LogPullRequestFailed(logger, settings.ProjectId, (int)response.StatusCode);
        return PullRequestOutcome.Failed(reason, GitHubFailures.IsTransientStatus(response.StatusCode, IsRateLimited(response)));
    }

    public string ComparisonBaseRef(GitConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return $"refs/remotes/{GitRemote.Name}/{settings.DefaultBranch}";
    }

    /// <summary>
    /// FR-42 amended by FR-64, FR-43: reads the version's PR; the release tag already on GitHub that contains the branch (or
    /// the PR's merge) is a no-op; the tag on another commit fails the release with <c>TAG_EXISTS</c> before anything is
    /// merged (tags are never moved). Otherwise merges the PR through the API (<c>GitHub:ReleaseMergeMethod</c>,
    /// pinned to the fetched branch head, which must be the PR head GitHub reports), fetches the merge commit, tags it (annotated) in the working copy and pushes the
    /// tag with the token in the per-command header. A PR GitHub can't merge because of conflicts is listed with a trial
    /// merge in the working copy (<c>MERGE_CONFLICT</c>). The working copy was fetched by the caller's prepare.
    /// </summary>
    public async Task<ReleaseOutcome> ReleaseAsync(GitConnectionSettings settings, ReleaseRequest request, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        if (Remote(settings, token) is not { } remote || !ProjectSettingsRules.TryParseGitHubRepo(settings.RepoUrl, out var repo))
        {
            var missing = MissingRemote(settings, token);
            return ReleaseOutcome.Failed(ErrorCodes.GitError, missing.Reason!, missing.Transient);
        }

        if (request.PullRequestNumber is not { } number)
        {
            return ReleaseOutcome.Failed(
                ErrorCodes.GitError,
                "No pull request is recorded for the project branch, so there is nothing to merge. Open it again from the project's Git section, then retry the release.",
                transient: false);
        }

        var id = settings.ProjectId;
        try
        {
            var remoteHead = $"refs/remotes/{GitRemote.Name}/{request.Branch}";
            var head = await workingCopies.ResolveCommitAsync(id, remoteHead, cancellationToken)
                ?? await workingCopies.ResolveCommitAsync(id, $"refs/heads/{request.Branch}", cancellationToken);
            if (head is null)
            {
                return ReleaseOutcome.Failed(ErrorCodes.GitError, $"The project branch '{request.Branch}' doesn't exist in {repo.Owner}/{repo.Name}.", transient: false);
            }

            var client = clients.CreateClient(HttpClientName);
            var pull = $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}";
            var pr = await ReadPullAsync(client, pull, token!, cancellationToken);
            if (pr.Failure is { } unreadable)
            {
                return unreadable;
            }

            var listed = await workingCopies.RemoteTagAsync(id, request.Tag, remote, cancellationToken);
            if (!listed.Ok)
            {
                var reason = Scrub(remote, id, $"Reading the release tag {request.Tag} failed: {listed.Failure!.StandardError.Trim()}");
                LogGitFailed(logger, id, "ls-remote", reason);
                return ReleaseOutcome.Failed(ErrorCodes.GitError, reason, GitHubFailures.IsTransientGitError(reason));
            }

            // FR-64: a tag pointing at the PR's merge commit is the release already made (reused); a tag anywhere else — even
            // a later commit that contains the merge — is TAG_EXISTS before anything is merged or recorded (tags never move).
            // ls-remote may list an annotated tag's object rather than its commit: peel it in the working copy (it was fetched).
            ExistingReleaseTag? existing = null;
            string? taggedCommit = null;
            if (listed.Tags.Count > 0 && listed.Tags[0] is { } found)
            {
                taggedCommit = await workingCopies.ResolveCommitAsync(id, found.Commit, cancellationToken) ?? found.Commit;
                var atMerge = pr.Merged && pr.MergeCommitSha is { } prMerge && IsCommitId(prMerge) && taggedCommit == prMerge;
                existing = new ExistingReleaseTag(found.Tag, atMerge);
            }

            var decision = ReleaseRules.DecideTag(request.Tag, existing);
            if (decision.IsFailure)
            {
                return ReleaseOutcome.Failed(decision.Error!.Code, decision.Error.Message, transient: false);
            }

            if (decision.Value)
            {
                return ReleaseOutcome.Released(taggedCommit!, request.Tag, alreadyTagged: true);
            }

            var tagName = request.Tag;
            string mergeSha;
            var mergedExternally = false;
            if (pr.Merged)
            {
                if (pr.MergeCommitSha is not { } merged || !IsCommitId(merged))
                {
                    return ReleaseOutcome.Failed(ErrorCodes.GitError, $"GitHub reports pull request #{number} as merged but returned no merge commit.", transient: true);
                }

                mergeSha = merged;
                mergedExternally = true;
            }
            else
            {
                if (pr.State == "closed")
                {
                    return ReleaseOutcome.Failed(
                        ErrorCodes.PrClosed, $"Pull request #{number} was closed without merging. Reopen it on GitHub, then retry the release.", transient: false);
                }

                // FR-65: a draft PR can't be merged or reviewed: mark it ready for review (both strategies).
                if (pr.Draft && pr.NodeId is { Length: > 0 } nodeId)
                {
                    var marked = await api.MarkReadyForReviewAsync(token!, nodeId, cancellationToken);
                    LogMarkedReady(logger, id, number, marked);
                }

                if (!request.Merge)
                {
                    return ReleaseOutcome.Awaiting(
                        $"The merge strategy is manual: AIDE never merges. Pull request #{number} is ready for review; merge it on GitHub.", refused: false);
                }

                if (pr.Mergeable == false)
                {
                    return await ConflictAsync(settings, request, head, cancellationToken)
                        ?? ReleaseOutcome.Failed(ErrorCodes.GitError, $"GitHub can't merge pull request #{number} (mergeable: false).", transient: false);
                }

                // The merge is pinned to the head the tag check above used: if GitHub's PR head differs (the branch moved, or
                // the fetch is behind), merging would release commits nobody checked. The next attempt fetches again.
                if (pr.HeadSha != head)
                {
                    return ReleaseOutcome.Failed(
                        ErrorCodes.GitError,
                        $"Pull request #{number} points at a different commit than the fetched project branch ({head[..12]}); the release retries with a fresh fetch.",
                        transient: true);
                }

                var merge = await MergePullAsync(client, pull, number, ReleaseRules.MergeTitle(request.ProjectName, tagName), head, token!, cancellationToken);
                if (merge.Sha is not { } sha)
                {
                    if (merge.NotMergeable)
                    {
                        // FR-65: refused without conflicts (branch protection, reviews, checks): a merge on GitHub is needed.
                        return await ConflictAsync(settings, request, head, cancellationToken)
                            ?? ReleaseOutcome.Awaiting(merge.Failure!.Reason!, refused: true);
                    }

                    if (merge.Refused)
                    {
                        return ReleaseOutcome.Awaiting(merge.Failure!.Reason!, refused: true);
                    }

                    return merge.Failure!;
                }

                mergeSha = sha;
                LogMerged(logger, id, number, sha);
            }

            if (request.OnMerged is { } onMerged)
            {
                await onMerged(mergeSha, cancellationToken);
            }

            // The merge commit is on GitHub: fetch it, tag it in the working copy, push the tag.
            await workingCopies.SyncFromRemoteAsync(id, remote, cancellationToken);
            if (await workingCopies.ResolveCommitAsync(id, mergeSha, cancellationToken) is null)
            {
                return ReleaseOutcome.Failed(ErrorCodes.GitError, $"The merge commit {mergeSha[..12]} isn't visible in the repository yet.", transient: true);
            }

            await workingCopies.CreateTagAsync(id, tagName, mergeSha, ReleaseRules.TagMessage(request.ProjectName, tagName, request.Version), cancellationToken);
            var pushed = await workingCopies.PushTagAsync(id, tagName, remote, cancellationToken);
            if (!pushed.Succeeded)
            {
                var reason = Scrub(remote, id, $"git push of tag {tagName} failed (exit {pushed.ExitCode}): {pushed.StandardError.Trim()}");
                LogGitFailed(logger, id, "push tag", reason);

                // A tag pushed meanwhile ("already exists") is checked by the next attempt; other rejections are permanent.
                var transient = reason.Contains("already exists", StringComparison.OrdinalIgnoreCase) || GitHubFailures.IsTransientGitError(reason);
                return ReleaseOutcome.Failed(ErrorCodes.GitError, reason, transient);
            }

            return ReleaseOutcome.Released(mergeSha, tagName, alreadyTagged: false) with { MergedExternally = mergedExternally };
        }
        catch (GitCommandException ex)
        {
            var reason = Scrub(remote, id, ex.Message);
            LogGitFailed(logger, id, "release", reason);
            return ReleaseOutcome.Failed(ErrorCodes.GitError, reason, GitHubFailures.IsTransientGitError(reason));
        }
        catch (WorkingCopyException ex)
        {
            return ReleaseOutcome.Failed(ErrorCodes.GitError, Scrub(remote, id, ex.Message), transient: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogGitFailed(logger, id, "release", ex.GetType().Name);
            return ReleaseOutcome.Failed(ErrorCodes.GitError, "The working copy could not be written on the AIDE server. Ask an admin to check the server log.", transient: true);
        }
    }

    /// <summary>FR-61: looks the tag up through the API (<c>GET /repos/{owner}/{repo}/git/ref/tags/{tag}</c>; 404 = no such tag).</summary>
    public async Task<TagLookup> FindTagAsync(GitConnectionSettings settings, string tag, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(tag);
        if (!ProjectSettingsRules.TryParseGitHubRepo(settings.RepoUrl, out var repo))
        {
            return TagLookup.Failed("The repository URL isn't a GitHub repository URL (https://github.com/owner/repo).", transient: false);
        }

        var refPath = string.Join('/', tag.Split('/').Select(Uri.EscapeDataString));
        var client = clients.CreateClient(HttpClientName);
        using var sent = await SendAsync(client, $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/git/ref/tags/{refPath}", token, cancellationToken);
        if (sent.Error is { } unreachable)
        {
            return TagLookup.Failed(unreachable, transient: true);
        }

        var response = sent.Message!;
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return TagLookup.Missing;
        }

        if (!response.IsSuccessStatusCode)
        {
            return TagLookup.Failed(await DescribeFailureAsync(response, cancellationToken), GitHubFailures.IsTransientStatus(response.StatusCode, IsRateLimited(response)));
        }

        // Only an object naming exactly this ref is the tag (older API versions answered prefix matches with an array).
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("ref", out var found)
                && found.ValueKind == JsonValueKind.String
                && found.GetString() == $"refs/tags/{tag}"
                ? TagLookup.Found
                : TagLookup.Missing;
        }
        catch (JsonException)
        {
            return TagLookup.Failed("GitHub returned an unreadable tag reference.", transient: true);
        }
    }

    /// <summary>FR-43: the conflicting files from a trial merge of the remote project branch into the remote default branch; <c>null</c> when it merges cleanly.</summary>
    private async Task<ReleaseOutcome?> ConflictAsync(GitConnectionSettings settings, ReleaseRequest request, string head, CancellationToken cancellationToken)
    {
        var files = await workingCopies.TrialMergeConflictsAsync(settings.ProjectId, ComparisonBaseRef(settings), head, cancellationToken);
        return files.Count == 0
            ? null
            : ReleaseOutcome.Conflicted(
                files,
                $"The pull request from {request.Branch} conflicts with {settings.DefaultBranch} in {files.Count} file(s). Resolve them on the project branch, then retry the release.");
    }

    private static async Task<PullState> ReadPullAsync(HttpClient client, string pull, string token, CancellationToken cancellationToken)
    {
        using var sent = await SendAsync(client, HttpMethod.Get, pull, token, cancellationToken);
        if (sent.Error is { } unreachable)
        {
            return PullState.Failed(ReleaseOutcome.Failed(ErrorCodes.GitError, unreachable, transient: true));
        }

        var response = sent.Message!;
        if (!response.IsSuccessStatusCode)
        {
            var reason = await DescribeFailureAsync(response, cancellationToken);
            return PullState.Failed(ReleaseOutcome.Failed(ErrorCodes.GitError, reason, GitHubFailures.IsTransientStatus(response.StatusCode, IsRateLimited(response))));
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return PullState.Failed(ReleaseOutcome.Failed(ErrorCodes.GitError, "GitHub returned an unreadable pull request.", transient: true));
            }

            return new PullState(
                Text(root, "state"),
                root.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True,
                root.TryGetProperty("mergeable", out var mergeable) && mergeable.ValueKind is JsonValueKind.True or JsonValueKind.False ? mergeable.GetBoolean() : null,
                Text(root, "merge_commit_sha"),
                root.TryGetProperty("head", out var prHead) && prHead.ValueKind == JsonValueKind.Object ? Text(prHead, "sha") : null,
                null)
            {
                Draft = root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
                NodeId = Text(root, "node_id"),
            };
        }
        catch (JsonException)
        {
            return PullState.Failed(ReleaseOutcome.Failed(ErrorCodes.GitError, "GitHub returned an unreadable pull request.", transient: true));
        }
    }

    private async Task<MergeAttempt> MergePullAsync(
        HttpClient client, string pull, int number, string mergeTitle, string headSha, string token, CancellationToken cancellationToken)
    {
        // Pinned: GitHub refuses (409) if the branch moved since it was fetched; the job retries and fetches it again.
        var body = new Dictionary<string, string> { ["merge_method"] = options.Value.ReleaseMergeMethod, ["commit_title"] = mergeTitle, ["sha"] = headSha };

        using var sent = await SendAsync(client, HttpMethod.Put, $"{pull}/merge", token, cancellationToken, JsonContent.Create(body));
        if (sent.Error is { } unreachable)
        {
            return new MergeAttempt(null, false, ReleaseOutcome.Failed(ErrorCodes.GitError, unreachable, transient: true));
        }

        var response = sent.Message!;
        if (response.IsSuccessStatusCode)
        {
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (json.RootElement.ValueKind == JsonValueKind.Object && Text(json.RootElement, "sha") is { } sha && IsCommitId(sha))
                {
                    return new MergeAttempt(sha, false, null);
                }
            }
            catch (JsonException)
            {
            }

            // Merged, but the reply is unreadable: the next attempt reads the PR as merged and uses its merge commit.
            return new MergeAttempt(null, false, ReleaseOutcome.Failed(ErrorCodes.GitError, $"GitHub merged pull request #{number} but returned an unreadable reply.", transient: true));
        }

        var reason = await DescribeFailureAsync(response, cancellationToken);
        LogMergeFailed(logger, number, (int)response.StatusCode);

        // §4.14: a rate-limited (or secondary rate-limited) 403 is not a refusal: the job waits for the reset and merges then.
        if (response.StatusCode == HttpStatusCode.Forbidden
            && (IsRateLimited(response) || reason.Contains("rate limit", StringComparison.OrdinalIgnoreCase)))
        {
            var now = TimeProvider.System.GetUtcNow();
            throw new GitHub.GitHubRateLimitedException(GitHub.GitHubApi.RateLimitResetAt(response, now) ?? now + TimeSpan.FromMinutes(1));
        }

        // 405: not mergeable (conflicts, or branch protection: required reviews/checks); 409: the head moved since it was
        // fetched (retry); 403/422: AIDE may not merge (permission, merge method) — FR-65 awaits a merge on GitHub.
        return response.StatusCode switch
        {
            HttpStatusCode.MethodNotAllowed => new MergeAttempt(null, true, ReleaseOutcome.Failed(ErrorCodes.GitError, reason, transient: false)),
            HttpStatusCode.Conflict => new MergeAttempt(null, false, ReleaseOutcome.Failed(ErrorCodes.GitError, reason, transient: true)),
            HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity =>
                new MergeAttempt(null, false, ReleaseOutcome.Failed(ErrorCodes.GitError, reason, transient: false)) { Refused = true },
            _ => new MergeAttempt(null, false, ReleaseOutcome.Failed(ErrorCodes.GitError, reason, GitHubFailures.IsTransientStatus(response.StatusCode, IsRateLimited(response)))),
        };
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsCommitId(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigitLower);

    /// <summary>The project PR as the release needs it, or the failure reading it.</summary>
    private sealed record PullState(string? State, bool Merged, bool? Mergeable, string? MergeCommitSha, string? HeadSha, ReleaseOutcome? Failure)
    {
        public bool Draft { get; init; }

        public string? NodeId { get; init; }

        public static PullState Failed(ReleaseOutcome failure) => new(null, false, null, null, null, failure);
    }

    /// <summary>
    /// The merge API's answer: the merge commit, or whether GitHub found the PR not mergeable (405), or refused the merge
    /// for missing permission or an invalid request (403 without a rate limit, 422) — FR-65 awaiting merge — or the failure.
    /// </summary>
    private sealed record MergeAttempt(string? Sha, bool NotMergeable, ReleaseOutcome? Failure)
    {
        public bool Refused { get; init; }
    }

    /// <summary>
    /// Brings a reused open PR's title and description up to date (they describe the plan that just completed). A transient
    /// failure retries the job; a permanent one (e.g. the token may not edit it) keeps the PR as it is.
    /// </summary>
    private async Task<PullRequestOutcome> UpdatePullRequestAsync(
        HttpClient client, string pulls, PullRequestOutcome reused, PullRequestRequest request, Guid projectId, string token, CancellationToken cancellationToken)
    {
        if (reused.Number is not { } number)
        {
            return reused;
        }

        var path = $"{pulls}/{number.ToString(CultureInfo.InvariantCulture)}";
        using var sent = await SendAsync(client, HttpMethod.Patch, path, token, cancellationToken, JsonContent.Create(new { title = request.Title, body = request.Body }));
        if (sent.Error is { } unreachable)
        {
            return PullRequestOutcome.Failed(unreachable, transient: true);
        }

        var response = sent.Message!;
        if (response.IsSuccessStatusCode)
        {
            return reused;
        }

        if (GitHubFailures.IsTransientStatus(response.StatusCode, IsRateLimited(response)))
        {
            return PullRequestOutcome.Failed(await DescribeFailureAsync(response, cancellationToken), transient: true);
        }

        LogPullRequestUpdateFailed(logger, projectId, number, (int)response.StatusCode);
        return reused;
    }

    /// <summary>The open PR for <paramref name="head"/> → <paramref name="defaultBranch"/>, a failure, or <c>null</c> when there is none.</summary>
    private static async Task<PullRequestOutcome?> FindOpenPullRequestAsync(
        HttpClient client, string pulls, string owner, string head, string defaultBranch, string token, CancellationToken cancellationToken)
    {
        var query = $"{pulls}?state=open&head={Uri.EscapeDataString($"{owner}:{head}")}&base={Uri.EscapeDataString(defaultBranch)}&per_page=1";
        using var sent = await SendAsync(client, HttpMethod.Get, query, token, cancellationToken);
        if (sent.Error is { } unreachable)
        {
            return PullRequestOutcome.Failed(unreachable, transient: true);
        }

        var response = sent.Message!;
        if (!response.IsSuccessStatusCode)
        {
            var reason = await DescribeFailureAsync(response, cancellationToken);
            return PullRequestOutcome.Failed(reason, GitHubFailures.IsTransientStatus(response.StatusCode, IsRateLimited(response)));
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var (number, url) = PullRequestFields(json.RootElement[0]);
            return PullRequestOutcome.Opened(number, url, reused: true);
        }
        catch (JsonException)
        {
            return PullRequestOutcome.Failed("GitHub returned an unreadable pull request list.", transient: true);
        }
    }

    private static async Task<PullRequestOutcome> ReadPullRequestAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var (number, url) = PullRequestFields(json.RootElement);
            return PullRequestOutcome.Opened(number, url, reused: false);
        }
        catch (JsonException)
        {
            // Created, but unreadable: the next attempt finds it as the open PR for the branch.
            return PullRequestOutcome.Failed("GitHub created the pull request but returned an unreadable reply.", transient: true);
        }
    }

    /// <summary>The PR number and its https web URL (anything else is dropped; at most 500 characters).</summary>
    private static (int? Number, string? Url) PullRequestFields(JsonElement pr)
    {
        if (pr.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        int? number = pr.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var value) ? value : null;
        string? url = pr.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String
            && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.OriginalString.Length <= 500
            ? uri.OriginalString
            : null;
        return (number, url);
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.Headers.RetryAfter is not null
        || (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.Any(v => v.Trim() == "0"));

    private GitRemote? Remote(GitConnectionSettings settings, string? token) =>
        !string.IsNullOrEmpty(token) && ProjectSettingsRules.TryParseGitHubRepo(settings.RepoUrl, out var repo)
            ? GitHubRemotes.Create(options.Value, repo, token)
            : null;

    private static GitOutcome MissingRemote(GitConnectionSettings settings, string? token) =>
        !ProjectSettingsRules.TryParseGitHubRepo(settings.RepoUrl, out _)
            ? GitOutcome.Failed("The repository URL isn't a GitHub repository URL (https://github.com/owner/repo).", transient: false)
            : GitOutcome.Failed(
                string.IsNullOrEmpty(token)
                    ? "No access token is saved, so AIDE can't clone or push. Save a token with Contents and Pull requests: read and write."
                    : "The repository can't be reached.",
                transient: false);

    /// <summary>Removes the token (in any form) and the server's working-copy paths from text shown to users or logged.</summary>
    private string Scrub(GitRemote remote, Guid projectId, string text)
    {
        var cleaned = remote.Redact(text);
        foreach (var path in new[] { workspaces.PathFor(projectId), workspaces.Root })
        {
            cleaned = cleaned.Replace(path, "<working copy>", StringComparison.OrdinalIgnoreCase)
                .Replace(path.Replace('\\', '/'), "<working copy>", StringComparison.OrdinalIgnoreCase);
        }

        return Clean(cleaned);
    }

    private static Task<Sent> SendAsync(HttpClient client, string path, string? token, CancellationToken cancellationToken) =>
        SendAsync(client, HttpMethod.Get, path, token, cancellationToken);

    private static async Task<Sent> SendAsync(HttpClient client, HttpMethod method, string path, string? token, CancellationToken cancellationToken, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return new Sent(null, $"Could not reach GitHub: {Clean(ex.Message)}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Sent(null, "Could not reach GitHub: the request timed out.");
        }

        // §4.14, NFR-REL: a rate limit isn't a failure: request paths answer 429 GITHUB_RATE_LIMITED, jobs wait for the reset.
        if (GitHub.GitHubApi.RateLimitResetAt(response, TimeProvider.System.GetUtcNow()) is { } resetAt)
        {
            response.Dispose();
            throw new GitHub.GitHubRateLimitedException(resetAt);
        }

        return new Sent(response, null);
    }

    private static async Task<bool> CanPushAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return json.RootElement.TryGetProperty("permissions", out var permissions)
                && permissions.ValueKind == JsonValueKind.Object
                && permissions.TryGetProperty("push", out var push)
                && push.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>"GitHub: {message} (HTTP {status})" from GitHub's error body, plus a hint for the usual cases.</summary>
    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? message = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.String)
            {
                message = Clean(m.GetString());
            }
        }
        catch (JsonException)
        {
        }

        var status = (int)response.StatusCode;
        var reason = $"GitHub: {message ?? response.ReasonPhrase ?? "request failed"} (HTTP {status})";
        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " The token is invalid or expired.",
            HttpStatusCode.NotFound => " The repository doesn't exist, or the token can't access it.",
            HttpStatusCode.Forbidden => " The token lacks access, or the API rate limit was hit.",
            _ => string.Empty,
        };
        return reason + hint;
    }

    private static string Clean(string? text)
    {
        var cleaned = new string((text ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > MaxReasonLength ? cleaned[..MaxReasonLength] : cleaned;
    }

    private sealed record Sent(HttpResponseMessage? Message, string? Error) : IDisposable
    {
        public void Dispose() => Message?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub connection test for project {ProjectId}: writable={Writable}, default branch exists={BranchExists}")]
    private static partial void LogTested(ILogger logger, Guid projectId, bool writable, bool branchExists);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub connection test for project {ProjectId} failed with HTTP {Status}")]
    private static partial void LogTestFailed(ILogger logger, Guid projectId, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Git {Operation} for GitHub project {ProjectId} failed: {Reason}")]
    private static partial void LogGitFailed(ILogger logger, Guid projectId, string operation, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened pull request #{Number} for project {ProjectId}")]
    private static partial void LogPullRequestOpened(ILogger logger, Guid projectId, int? number);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reusing open pull request #{Number} for project {ProjectId}")]
    private static partial void LogPullRequestReused(ILogger logger, Guid projectId, int? number);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Updating the description of pull request #{Number} for project {ProjectId} failed with HTTP {Status}; it is reused as it is")]
    private static partial void LogPullRequestUpdateFailed(ILogger logger, Guid projectId, int number, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Opening the pull request for project {ProjectId} failed with HTTP {Status}")]
    private static partial void LogPullRequestFailed(ILogger logger, Guid projectId, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Merged pull request #{Number} for project {ProjectId} as {Sha}")]
    private static partial void LogMerged(ILogger logger, Guid projectId, int number, string sha);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Merging pull request #{Number} failed with HTTP {Status}")]
    private static partial void LogMergeFailed(ILogger logger, int number, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Draft pull request #{Number} of project {ProjectId} marked ready for review: {Marked}")]
    private static partial void LogMarkedReady(ILogger logger, Guid projectId, int number, bool marked);
}

/// <summary>
/// The Local provider (D12): repositories and working copies live under the workspace root on the data volume, so
/// the flow works offline. The test passes when the root is writable and the git CLI runs. The default branch is
/// created with the local repository, so it always "exists".
/// </summary>
internal sealed partial class LocalGitProvider(IWorkspaceStore workspaces, IGitCli git, IWorkingCopies workingCopies, ILogger<LocalGitProvider> logger) : IGitProvider
{
    public GitProvider Kind => GitProvider.Local;

    public async Task<GitTestResult> TestConnectionAsync(GitConnectionSettings settings, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = workspaces.Root;
        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".aide-write-test-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "ok", cancellationToken);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotWritable(logger, ex, settings.ProjectId, root);
            // The server path stays in the log; the reason shown to the project Owner is generic.
            return GitTestResult.Failed("The workspace folder on the AIDE server isn't writable. Ask an admin to check the Workspaces:Root setting and its volume.");
        }

        if (await git.GetVersionAsync(cancellationToken) is null)
        {
            return GitTestResult.Failed(
                "The git command-line tool isn't installed on the AIDE server, so local repositories can't be created.",
                reachable: true,
                writable: true,
                defaultBranchExists: true);
        }

        return new GitTestResult(true, true, true, true, null);
    }

    /// <summary>
    /// FR-35 (D12): the repository is the working copy itself, on the data volume: created with an empty initial commit on
    /// the default branch, then the project branch is created from it and checked out.
    /// </summary>
    public async Task<GitOutcome> PrepareWorkingCopyAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            await workingCopies.InitAsync(settings.ProjectId, settings.DefaultBranch, cancellationToken);
            await workingCopies.CheckoutBranchAsync(settings.ProjectId, settings.DefaultBranch, branchName, cancellationToken);
            return GitOutcome.Success;
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException or UnauthorizedAccessException)
        {
            LogPrepareFailed(logger, ex, settings.ProjectId);
            // The server path stays in the log; the reason shown to users is generic.
            return GitOutcome.Failed("The local repository could not be prepared on the AIDE server. Ask an admin to check the server log and the Workspaces:Root volume.");
        }
    }

    /// <summary>A Local repository has no remote: the commit is already where it belongs.</summary>
    public Task<GitOutcome> PublishBranchAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken) =>
        Task.FromResult(GitOutcome.Success);

    /// <summary>FR-35 (D12): a Local project has no PR to open; its "PR" is the branch comparison the caller records.</summary>
    public Task<PullRequestOutcome> OpenPullRequestAsync(GitConnectionSettings settings, PullRequestRequest request, string? token, CancellationToken cancellationToken) =>
        Task.FromResult(PullRequestOutcome.Opened(null, null, reused: false));

    public string ComparisonBaseRef(GitConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return $"refs/heads/{settings.DefaultBranch}";
    }

    /// <summary>
    /// FR-42 amended by FR-64, FR-43 (D12): the repository is the working copy, so the release is local Git work: the release
    /// tag already containing the version branch is a no-op; the tag on another commit fails with <c>TAG_EXISTS</c> and
    /// nothing is merged; otherwise merge the branch into the default branch (<c>--no-ff</c>, hooks off; a conflict is listed
    /// and aborted) and create the annotated tag on the merge commit.
    /// </summary>
    public async Task<ReleaseOutcome> ReleaseAsync(GitConnectionSettings settings, ReleaseRequest request, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        var id = settings.ProjectId;
        try
        {
            if (await workingCopies.ResolveCommitAsync(id, $"refs/heads/{request.Branch}", cancellationToken) is not { } head)
            {
                return ReleaseOutcome.Failed(ErrorCodes.GitError, $"The project branch '{request.Branch}' doesn't exist in the local repository.", transient: false);
            }

            // FR-64: reused only when the tag points at the commit that merged the branch (not merely one containing it).
            var found = await workingCopies.TagAsync(id, request.Tag, cancellationToken);
            var existing = found is null
                ? null
                : new ExistingReleaseTag(
                    found.Tag,
                    await workingCopies.FindMergeCommitAsync(id, $"refs/heads/{settings.DefaultBranch}", head, cancellationToken) == found.Commit);
            var decision = ReleaseRules.DecideTag(request.Tag, existing);
            if (decision.IsFailure)
            {
                return ReleaseOutcome.Failed(decision.Error!.Code, decision.Error.Message, transient: false);
            }

            if (decision.Value)
            {
                return ReleaseOutcome.Released(found!.Commit, request.Tag, alreadyTagged: true);
            }

            if (!request.Merge)
            {
                // FR-65 manual (and the merge poll): never merge; a branch someone else merged into the default branch is
                // tagged on the commit that brought it in (first-parent history; the head itself after a fast-forward).
                var mergedBySomeone = await workingCopies.FindMergeCommitAsync(id, $"refs/heads/{settings.DefaultBranch}", head, cancellationToken);
                if (mergedBySomeone is null)
                {
                    return ReleaseOutcome.Awaiting(
                        $"The merge strategy is manual: AIDE never merges. Merge {request.Branch} into {settings.DefaultBranch} in the local repository.", refused: false);
                }

                if (request.OnMerged is { } recordMerge)
                {
                    await recordMerge(mergedBySomeone, cancellationToken);
                }

                await workingCopies.CreateTagAsync(
                    id, request.Tag, mergedBySomeone, ReleaseRules.TagMessage(request.ProjectName, request.Tag, request.Version), cancellationToken);
                return ReleaseOutcome.Released(mergedBySomeone, request.Tag, alreadyTagged: false) with { MergedExternally = true };
            }

            var merged = await workingCopies.MergeIntoDefaultAsync(
                id, settings.DefaultBranch, request.Branch, ReleaseRules.MergeTitle(request.ProjectName, request.Tag), cancellationToken);
            if (!merged.Merged)
            {
                return ReleaseOutcome.Conflicted(
                    merged.Conflicts,
                    $"Merging {request.Branch} into {settings.DefaultBranch} conflicts in {merged.Conflicts.Count} file(s). Resolve them on the project branch, then retry the release.");
            }

            if (request.OnMerged is { } onMerged)
            {
                await onMerged(merged.Sha!, cancellationToken);
            }

            await workingCopies.CreateTagAsync(
                id, request.Tag, merged.Sha!, ReleaseRules.TagMessage(request.ProjectName, request.Tag, request.Version), cancellationToken);
            return ReleaseOutcome.Released(merged.Sha!, request.Tag, alreadyTagged: false);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException or UnauthorizedAccessException)
        {
            LogReleaseFailed(logger, ex, id);
            // The server path stays in the log; the reason shown to users is generic. A git command failure (e.g. a timeout)
            // may pass on a retry; an unusable working copy won't.
            return ReleaseOutcome.Failed(
                ErrorCodes.GitError,
                "The local repository could not be merged or tagged on the AIDE server. Ask an admin to check the server log.",
                transient: ex is GitCommandException or IOException);
        }
    }

    /// <summary>FR-61: the tag in the local repository on the data volume; a project whose repository doesn't exist yet has no tags.</summary>
    public async Task<TagLookup> FindTagAsync(GitConnectionSettings settings, string tag, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            return await workingCopies.TagAsync(settings.ProjectId, tag, cancellationToken) is null ? TagLookup.Missing : TagLookup.Found;
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException or UnauthorizedAccessException)
        {
            LogReleaseFailed(logger, ex, settings.ProjectId);
            return TagLookup.Failed("The local repository could not be read on the AIDE server. Ask an admin to check the server log.", transient: true);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Releasing the local repository of project {ProjectId} failed")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preparing the local repository of project {ProjectId} failed")]
    private static partial void LogPrepareFailed(ILogger logger, Exception exception, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workspace root {Root} is not writable (project {ProjectId})")]
    private static partial void LogNotWritable(ILogger logger, Exception exception, Guid projectId, string root);
}
