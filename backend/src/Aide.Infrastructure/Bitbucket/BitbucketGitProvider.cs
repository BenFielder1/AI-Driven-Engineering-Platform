using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.GitLab;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Bitbucket;

/// <summary>
/// FR-136, FR-137, FR-141, §4.19: the <c>bitbucket</c> provider. All Git work for a project uses the <b>binding Owner's
/// connection</b> (<see cref="GitConnectionSettings.GitConnectionId"/>), whose token is fetched per call from
/// <see cref="IGitConnectionTokens"/> (an OAuth token refreshed before expiry; a 401 refreshes once, then marks the
/// connection invalid, which clears the binding). Every API request takes from the connection's budget (900 an hour,
/// <see cref="BitbucketRateLimiter"/>); a 429 (Bitbucket's or the budget's) throws <see cref="BitbucketRateLimitedException"/>
/// (the job waits without counting an attempt). The token reaches git only as the per-command Basic
/// <c>x-token-auth:&lt;token&gt;</c> (OAuth) or <c>x-bitbucket-api-token-auth:&lt;token&gt;</c> (API token)
/// <c>http.extraHeader</c> (<see cref="GitHostRemotes"/>) on the fixed host <c>https://bitbucket.org</c>, never a remote URL,
/// the disk, a log or an LLM. The <c>token</c> argument callers pass is ignored. An unbound project's Git work fails with
/// <c>GIT_NOT_CONNECTED</c> (not retried). The pull request is opened with <c>draft: true</c> and marked ready with
/// <c>draft: false</c>. Merge readiness, CI statuses, the merge, the annotated tag and the merge poll (FR-138–FR-140) are in
/// <c>BitbucketGitProvider.Release.cs</c>.
/// </summary>
internal sealed partial class BitbucketGitProvider(
    IGitConnectionTokens tokens,
    IBitbucketClients clients,
    IWorkingCopies workingCopies,
    IWorkspaceStore workspaces,
    IOptionsMonitor<BitbucketOptions> options,
    TimeProvider time,
    ILogger<BitbucketGitProvider> logger) : IGitProvider
{
    private const int MaxReasonLength = 300;
    private const string Host = "Bitbucket";

    public GitProvider Kind => GitProvider.Bitbucket;

    public async Task<GitTestResult> TestConnectionAsync(GitConnectionSettings settings, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            return GitTestResult.Failed(failure.Reason!);
        }

        var (workspace, _) = BitbucketProjectRules.SplitFullName(settings.RepoFullName!);
        var repo = await ApiAsync(settings, access, (c, a) => c.GetRepositoryAsync(a, workspace, settings.ExternalRepoId!, cancellationToken), cancellationToken);
        if (repo.Failure is { } unreadable)
        {
            return GitTestResult.Failed(unreadable.Reason!);
        }

        if (repo.Value is null)
        {
            return GitTestResult.Failed($"{settings.RepoFullName} wasn't found on Bitbucket, or the bound connection can't see it.");
        }

        var permission = await ApiAsync(settings, access, (c, a) => c.GetRepositoryPermissionAsync(a, settings.ExternalRepoId!, cancellationToken), cancellationToken);
        if (permission.Failure is { } permissionFailure)
        {
            return GitTestResult.Failed(permissionFailure.Reason!, reachable: true);
        }

        var writable = GitBindingRules.BitbucketHasWriteAccess(permission.Value);
        var head = await ApiAsync(settings, access, (c, a) => c.GetBranchHeadAsync(a, settings.RepoFullName!, settings.DefaultBranch, cancellationToken), cancellationToken);
        if (head.Failure is { } branchFailure)
        {
            return GitTestResult.Failed(branchFailure.Reason!, reachable: true, writable: writable);
        }

        var branchExists = head.Value is not null;
        var reason = !writable
            ? "The bound Bitbucket account needs write access to the repository to push and open pull requests."
            : !branchExists ? $"The default branch '{settings.DefaultBranch}' doesn't exist in {settings.RepoFullName}." : null;
        return new GitTestResult(reason is null, Reachable: true, writable, branchExists, reason);
    }

    /// <summary>FR-35, FR-136: clones (or fetches) with the binding connection's token, then checks out the project branch.</summary>
    public async Task<GitOutcome> PrepareWorkingCopyAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return await GitAsync(settings, "prepare", async remote =>
        {
            await workingCopies.SyncFromRemoteAsync(settings.ProjectId, remote, cancellationToken);
            await workingCopies.CheckoutRemoteBranchAsync(settings.ProjectId, settings.DefaultBranch, branchName, cancellationToken);
            return null;
        }, cancellationToken);
    }

    /// <summary>FR-34, FR-136: pushes the project branch (fast-forward only) with the binding connection's token.</summary>
    public async Task<GitOutcome> PublishBranchAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return await GitAsync(settings, "push", async remote =>
        {
            var result = await workingCopies.PushBranchAsync(settings.ProjectId, branchName, remote, cancellationToken);
            return result.Succeeded ? null : $"git push failed (exit {result.ExitCode}): {result.StandardError.Trim()}";
        }, cancellationToken);
    }

    /// <summary>
    /// FR-137: reuses the open pull request from the project branch (its title and description brought up to date; its draft
    /// state untouched); otherwise opens it as a draft (<c>draft: true</c>, <c>close_source_branch: false</c>, the
    /// description linking back to the AIDE project) to the default branch. A create that races another (400/409) looks it up
    /// again, so one pull request exists per release version. The outcome's number is the pull request id.
    /// </summary>
    public async Task<PullRequestOutcome> OpenPullRequestAsync(GitConnectionSettings settings, PullRequestRequest request, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            return ToPullRequestFailure(failure);
        }

        var repo = settings.RepoFullName!;
        var existing = await ApiAsync(settings, access, (c, a) => c.FindOpenPullRequestAsync(a, repo, request.HeadBranch, cancellationToken), cancellationToken);
        if (existing.Failure is { } lookupFailure)
        {
            return ToPullRequestFailure(lookupFailure);
        }

        if (existing.Value is { } open)
        {
            var updated = await ApiAsync(settings, access, (c, a) => c.UpdatePullRequestAsync(a, repo, open.Id, request.Title, request.Body, null, cancellationToken), cancellationToken);
            if (updated.Failure is { Transient: true } transient)
            {
                return ToPullRequestFailure(transient);
            }

            LogPullRequestReused(logger, settings.ProjectId, open.Id);
            return PullRequestOutcome.Opened(open.Id, WebUrl(open.WebUrl) ?? PullRequestUrl(settings, open.Id), reused: true);
        }

        var created = await ApiAsync(
            settings,
            access,
            (c, a) => c.CreatePullRequestAsync(a, repo, request.Title, request.Body, request.HeadBranch, settings.DefaultBranch, cancellationToken),
            cancellationToken,
            conflictStatuses: [400, 409]);
        if (created.Conflict)
        {
            var raced = await ApiAsync(settings, access, (c, a) => c.FindOpenPullRequestAsync(a, repo, request.HeadBranch, cancellationToken), cancellationToken);
            if (raced.Value is { } other)
            {
                return PullRequestOutcome.Opened(other.Id, WebUrl(other.WebUrl) ?? PullRequestUrl(settings, other.Id), reused: true);
            }

            return raced.Failure is { } racedFailure
                ? ToPullRequestFailure(racedFailure)
                : PullRequestOutcome.Failed(created.ConflictReason ?? "Bitbucket refused the pull request.", transient: false);
        }

        if (created.Failure is { } createFailure)
        {
            return ToPullRequestFailure(createFailure);
        }

        LogPullRequestOpened(logger, settings.ProjectId, created.Value!.Id);
        return PullRequestOutcome.Opened(created.Value.Id, WebUrl(created.Value.WebUrl) ?? PullRequestUrl(settings, created.Value.Id), reused: false);
    }

    public string ComparisonBaseRef(GitConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return $"refs/remotes/{GitRemote.Name}/{settings.DefaultBranch}";
    }

    /// <summary>FR-61: <c>GET …/refs/tags/{tag}</c> (404 = no such tag).</summary>
    public async Task<TagLookup> FindTagAsync(GitConnectionSettings settings, string tag, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(tag);
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            return TagLookup.Failed(failure.Reason!, failure.Transient);
        }

        var found = await ApiAsync(settings, access, (c, a) => c.GetTagAsync(a, settings.RepoFullName!, tag, cancellationToken), cancellationToken);
        return found.Failure is { } lookupFailure
            ? TagLookup.Failed(lookupFailure.Reason!, lookupFailure.Transient)
            : found.Value is null ? TagLookup.Missing : TagLookup.Found;
    }

    // ---------------------------------------------------------------- §4.19 user-scoped operations

    /// <summary>
    /// FR-141: the repository (by UUID) with the caller's connection: its default branch (<c>mainbranch.name</c>) and head,
    /// whether it is empty (no commits, or only starter files: FR-86), its semver tags (<c>GET …/refs/tags</c>; Bitbucket has
    /// no Releases) and the suggested released version (the highest semver tag) and version to develop (its next minor).
    /// </summary>
    public Task<Result<GitHostReleaseRefs>> FindReleaseRefsAsync(Guid connectionId, string repoId, CancellationToken cancellationToken) =>
        UserCallAsync(connectionId, async (c, a) =>
        {
            var repo = await c.GetRepositoryAsync(a, string.Empty, repoId, cancellationToken);
            if (repo is null)
            {
                return Result.Failure<GitHostReleaseRefs>(new DomainError(ErrorCodes.NotFound, "The Bitbucket repository wasn't found, or your account can't see it."));
            }

            var fullName = repo.FullName;
            var defaultBranch = repo.MainBranch ?? Domain.Projects.ProjectSettingsRules.DefaultBranch;
            var head = repo.MainBranch is null ? null : await c.GetBranchHeadAsync(a, fullName, defaultBranch, cancellationToken);
            if (head is null)
            {
                return Result.Success(new GitHostReleaseRefs(fullName, defaultBranch, null, true, [], [], GitLabProjectRules.SuggestRelease([], ReleaseVersion.DefaultTagPrefix)));
            }

            var root = await c.ListRootEntriesAsync(a, fullName, head, cancellationToken);
            var tags = await c.ListTagsAsync(a, fullName, cancellationToken);
            var prefix = ReleaseVersion.DefaultTagPrefix;
            return Result.Success(new GitHostReleaseRefs(
                fullName,
                defaultBranch,
                head,
                Domain.GitHub.GitHubRules.IsEmptyRepository(root),
                [],
                [.. tags.Where(t => t.Hash is not null).Select(t => new GitHostTag(t.Name, t.Hash!, Domain.GitHub.GitHubRules.TagSemver(t.Name, prefix)))],
                GitLabProjectRules.SuggestRelease(tags.Select(t => new ReleaseRefCandidate(t.Name, false, false)), prefix)));
        }, cancellationToken);

    // ---------------------------------------------------------------- helpers

    /// <summary>The binding connection's token, or why Git work can't run (FR-136).</summary>
    private async Task<Access> AccessAsync(GitConnectionSettings settings, CancellationToken cancellationToken)
    {
        if (settings.InstanceId is not { } instanceId || string.IsNullOrEmpty(settings.ExternalRepoId) || !BitbucketProjectRules.IsValidFullName(settings.RepoFullName))
        {
            return Access.Failed(GitOutcome.Failed("The project's Bitbucket repository isn't recorded. Create the project again with a repository from the Bitbucket picker.", transient: false, ErrorCodes.GitError));
        }

        if (settings.GitConnectionId is not { } connectionId)
        {
            var unbound = GitBindingRules.CanRunGitJob(null, instanceEnabled: true, Host);
            return Access.Failed(GitOutcome.Failed(unbound.Error!.Message, transient: false, ErrorCodes.GitNotConnected));
        }

        var token = await tokens.GetAccessTokenAsync(connectionId, cancellationToken);
        if (token.IsFailure)
        {
            return Access.Failed(TokenFailure(token.Error!));
        }

        if (token.Value.Provider != GitHostKind.Bitbucket || token.Value.InstanceId != instanceId)
        {
            return Access.Failed(GitOutcome.Failed(
                "The bound connection isn't a Bitbucket connection for the project's repository. An Owner can re-bind their own connection in Settings → Git.",
                transient: false,
                ErrorCodes.GitNotConnected));
        }

        return new Access(token.Value, null);
    }

    private static GitOutcome TokenFailure(DomainError error) => error.Code switch
    {
        ErrorCodes.UpstreamError => GitOutcome.Failed(error.Message, transient: true, ErrorCodes.UpstreamError),
        _ => GitOutcome.Failed(error.Message, transient: false, ErrorCodes.GitNotConnected),
    };

    private static BitbucketAuth AuthOf(GitAccessToken token) =>
        token.Email is { } email ? BitbucketAuth.ApiToken(email, token.Token) : BitbucketAuth.OAuth(token.Token);

    /// <summary>
    /// Runs a Bitbucket API call for the project with the binding connection: a 401 refreshes once (then marks the
    /// connection invalid, clearing the binding: <c>GIT_NOT_CONNECTED</c>); a 429 (Bitbucket's or the budget's) throws
    /// <see cref="BitbucketRateLimitedException"/>; a status in <paramref name="conflictStatuses"/> is returned as a conflict;
    /// other failures are token-free reasons (5xx, 555 and network failures transient).
    /// </summary>
    private async Task<Call<T>> ApiAsync<T>(
        GitConnectionSettings settings, Access access, Func<BitbucketClient, BitbucketAuth, Task<T>> call, CancellationToken cancellationToken, int[]? conflictStatuses = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = access.Token!;
            try
            {
                return new Call<T>(await call(clients.Create(token.ConnectionId), AuthOf(token)), null);
            }
            catch (BitbucketApiException ex) when (ex.IsUnauthorized && attempt == 0)
            {
                var refreshed = await tokens.OnUnauthorizedAsync(token, settings.ProjectId, cancellationToken);
                if (refreshed.IsFailure)
                {
                    return Call<T>.Failed(TokenFailure(refreshed.Error!));
                }

                access.Token = refreshed.Value;
            }
            catch (BitbucketApiException ex) when (ex.IsRateLimited)
            {
                throw RateLimited(ex.RetryAfter);
            }
            catch (BitbucketApiException ex) when (ex.Status is { } status && conflictStatuses?.Contains(status) == true)
            {
                return new Call<T>(default, null) { Conflict = true, ConflictReason = Describe(ex), ConflictStatus = status };
            }
            catch (BitbucketApiException ex)
            {
                LogApiFailed(logger, settings.ProjectId, ex.Status);
                var transient = ex.Status is null or >= 500 or 408;
                return Call<T>.Failed(GitOutcome.Failed(Describe(ex), transient, ex.IsUnauthorized ? ErrorCodes.GitNotConnected : ErrorCodes.GitError)) with { Status = ex.Status };
            }
        }
    }

    /// <summary>
    /// A git command against the project's repository on bitbucket.org with the binding connection's token in the
    /// per-command header. An authentication failure refreshes the token once (as the API does); a 429 throws
    /// <see cref="BitbucketRateLimitedException"/>. <paramref name="run"/> returns a failure text (or <c>null</c> on success).
    /// </summary>
    private async Task<GitOutcome> GitAsync(GitConnectionSettings settings, string operation, Func<GitRemote, Task<string?>> run, CancellationToken cancellationToken)
    {
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            LogGitFailed(logger, settings.ProjectId, operation, failure.Reason!);
            return failure;
        }

        var current = options.CurrentValue;
        var transport = new GitTransportOverride(current.GitBaseUrlOverride, current.AllowInsecureGitBaseUrl);
        for (var attempt = 0; ; attempt++)
        {
            var token = access.Token!;
            var remote = GitHostRemotes.Create(new Uri(BitbucketRules.WebBaseUrl, UriKind.Absolute), settings.RepoFullName!, token.CredentialKind, token.Token, transport);
            string? error;
            var permanent = false;
            try
            {
                error = await run(remote);
            }
            catch (GitCommandException ex)
            {
                error = ex.Message;
            }
            catch (WorkingCopyException ex)
            {
                error = ex.Message;
                permanent = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogGitFailed(logger, settings.ProjectId, operation, ex.GetType().Name);
                return GitOutcome.Failed("The working copy could not be written on the AIDE server. Ask an admin to check the server log and the Workspaces:Root volume.", transient: true, ErrorCodes.GitError);
            }

            if (error is null)
            {
                return GitOutcome.Success;
            }

            var reason = Scrub(remote, settings.ProjectId, error);
            if (GitLabGitErrors.IsRateLimited(reason))
            {
                throw RateLimited(null);
            }

            if (!permanent && attempt == 0 && GitLabGitErrors.IsUnauthorized(reason))
            {
                var refreshed = await tokens.OnUnauthorizedAsync(token, settings.ProjectId, cancellationToken);
                if (refreshed.IsFailure)
                {
                    var notConnected = TokenFailure(refreshed.Error!);
                    LogGitFailed(logger, settings.ProjectId, operation, notConnected.Reason!);
                    return notConnected;
                }

                access.Token = refreshed.Value;
                continue;
            }

            LogGitFailed(logger, settings.ProjectId, operation, reason);
            return GitOutcome.Failed(reason, !permanent && GitHubFailures.IsTransientGitError(reason), ErrorCodes.GitError);
        }
    }

    /// <summary>A request-path call with the caller's connection (already checked to be theirs): errors as AIDE codes.</summary>
    private async Task<Result<T>> UserCallAsync<T>(Guid connectionId, Func<BitbucketClient, BitbucketAuth, Task<Result<T>>> call, CancellationToken cancellationToken)
    {
        var access = await tokens.GetAccessTokenAsync(connectionId, cancellationToken);
        for (var attempt = 0; ; attempt++)
        {
            if (access.IsFailure)
            {
                return Result.Failure<T>(access.Error!);
            }

            var token = access.Value;
            try
            {
                return await call(clients.Create(token.ConnectionId), AuthOf(token));
            }
            catch (BitbucketApiException ex) when (ex.IsUnauthorized && attempt == 0)
            {
                access = await tokens.OnUnauthorizedAsync(token, null, cancellationToken);
            }
            catch (BitbucketApiException ex)
            {
                return Result.Failure<T>(ex.Status switch
                {
                    401 => new DomainError(ErrorCodes.GitNotConnected, "Bitbucket rejected the connection. Connect Bitbucket again under Account → Connections → Bitbucket."),
                    429 => new DomainError(ErrorCodes.UpstreamRateLimited, "Bitbucket is rate limiting requests. Try again in a few minutes.") { RetryAfter = ex.RetryAfter },
                    403 => new DomainError(ErrorCodes.Forbidden, "Bitbucket refused: your Bitbucket account doesn't have permission to do that."),
                    404 => new DomainError(ErrorCodes.NotFound, "Bitbucket couldn't find that, or your account can't see it."),
                    _ => new DomainError(ErrorCodes.UpstreamError, ex.Status is null ? "Bitbucket couldn't be reached. Try again." : "Bitbucket returned an error. Try again later."),
                });
            }
        }
    }

    private BitbucketRateLimitedException RateLimited(TimeSpan? retryAfter)
    {
        var now = time.GetUtcNow();
        var wait = retryAfter is { } after && after > TimeSpan.Zero ? after : GitHostPollBudget.BitbucketRateLimitWait;
        return new BitbucketRateLimitedException(now + (wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait));
    }

    private static PullRequestOutcome ToPullRequestFailure(GitOutcome failure) =>
        PullRequestOutcome.Failed(failure.Reason!, failure.Transient) with { ErrorCode = failure.ErrorCode };

    /// <summary>The pull request's web URL when it is an absolute http(s) URL of at most 500 characters.</summary>
    private static string? WebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.OriginalString.Length <= 500
            ? uri.OriginalString
            : null;

    /// <summary>The pull request's page on bitbucket.org when Bitbucket sent no link.</summary>
    private static string PullRequestUrl(GitConnectionSettings settings, int id) =>
        $"{BitbucketProjectRules.RepoWebUrl(settings.RepoFullName!)}/pull-requests/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string Describe(BitbucketApiException ex) =>
        Clean($"Bitbucket: {ex.UpstreamMessage ?? "request failed"}{(ex.Status is { } s ? $" (HTTP {s})" : string.Empty)}");

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

    private static string Clean(string? text)
    {
        var cleaned = new string((text ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > MaxReasonLength ? cleaned[..MaxReasonLength] : cleaned;
    }

    /// <summary>The binding connection's current token (replaced after a refresh) or why there is none.</summary>
    private sealed class Access(GitAccessToken? token, GitOutcome? failure)
    {
        public GitAccessToken? Token { get; set; } = token;

        public GitOutcome? Failure { get; } = failure;

        public static Access Failed(GitOutcome failure) => new(null, failure);
    }

    private sealed record Call<T>(T? Value, GitOutcome? Failure)
    {
        public bool Conflict { get; init; }

        public string? ConflictReason { get; init; }

        public int? ConflictStatus { get; init; }

        /// <summary>Bitbucket's status for a failure (<c>null</c>: unreachable).</summary>
        public int? Status { get; init; }

        public static Call<T> Failed(GitOutcome failure) => new(default, failure);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Git {Operation} for Bitbucket project {ProjectId} failed: {Reason}")]
    private static partial void LogGitFailed(ILogger logger, Guid projectId, string operation, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bitbucket API call for project {ProjectId} failed with HTTP {Status}")]
    private static partial void LogApiFailed(ILogger logger, Guid projectId, int? status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened draft pull request #{Id} for project {ProjectId}")]
    private static partial void LogPullRequestOpened(ILogger logger, Guid projectId, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reusing open pull request #{Id} for project {ProjectId}")]
    private static partial void LogPullRequestReused(ILogger logger, Guid projectId, int id);
}
