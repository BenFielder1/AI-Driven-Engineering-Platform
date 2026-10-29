using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.GitLab;

/// <summary>
/// FR-136, FR-137, FR-141, §4.19: the <c>gitlab</c> provider. All Git work for a project uses the <b>binding Owner's
/// connection</b> (<see cref="GitConnectionSettings.GitConnectionId"/>), whose token is fetched per call from
/// <see cref="IGitConnectionTokens"/> (refreshed before expiry; a 401 refreshes once, then marks the connection invalid,
/// which clears the binding). The token reaches git only as the per-command Basic <c>oauth2:&lt;token&gt;</c>
/// <c>extraHeader</c> (<see cref="GitLabRemotes"/>), never a remote URL, the disk, a log or an LLM. The <c>token</c> argument
/// callers pass is ignored. An unbound project's Git work fails with <c>GIT_NOT_CONNECTED</c> (not retried); a GitLab 429
/// throws <see cref="GitLabRateLimitedException"/> (the job waits for the reset without counting an attempt).
/// Branches stay <c>aide/&lt;slug&gt;/v&lt;version&gt;</c> (chosen by the caller). The merge request is opened as a draft
/// (<c>Draft: </c> title prefix, <c>remove_source_branch: false</c>) and marked ready by removing the prefix
/// (<see cref="MarkMergeRequestReadyAsync"/>). Merge readiness, CI statuses, the merge, the GitLab Release and the merge
/// poll (FR-138–FR-140) are in <c>GitLabGitProvider.Release.cs</c>.
/// </summary>
internal sealed partial class GitLabGitProvider(
    IGitConnectionTokens tokens,
    IGitLabClients clients,
    IGitLabInstances instances,
    IWorkingCopies workingCopies,
    IWorkspaceStore workspaces,
    IOptionsMonitor<GitLabOptions> options,
    GitHostConnector connector,
    TimeProvider time,
    ILogger<GitLabGitProvider> logger) : IGitProvider
{
    private const int MaxReasonLength = 300;
    private const string Host = "GitLab";

    public GitProvider Kind => GitProvider.Gitlab;

    public async Task<GitTestResult> TestConnectionAsync(GitConnectionSettings settings, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            return GitTestResult.Failed(failure.Reason!);
        }

        var project = await ApiAsync(settings, access, (c, t) => c.GetProjectAsync(t, settings.ExternalRepoId!, cancellationToken), cancellationToken);
        if (project.Failure is { } unreadable)
        {
            return GitTestResult.Failed(unreadable.Reason!);
        }

        if (project.Value is not { } found)
        {
            return GitTestResult.Failed($"{settings.RepoFullName} wasn't found on GitLab, or the bound connection can't see it.");
        }

        var writable = GitLabProjectRules.HasDeveloperAccess(found.ProjectAccess, found.GroupAccess);
        var head = await ApiAsync(settings, access, (c, t) => c.GetBranchHeadAsync(t, found.ExternalId, settings.DefaultBranch, cancellationToken), cancellationToken);
        if (head.Failure is { } branchFailure)
        {
            return GitTestResult.Failed(branchFailure.Reason!, reachable: true, writable: writable);
        }

        var branchExists = head.Value is not null;
        var reason = !writable
            ? "The bound GitLab account needs the Developer role (or higher) on the project to push and open merge requests."
            : !branchExists ? $"The default branch '{settings.DefaultBranch}' doesn't exist in {found.PathWithNamespace}." : null;
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
    /// FR-137: reuses the open merge request from the project branch to the default branch (its title and description
    /// brought up to date, staying a draft while it is one); otherwise opens it as a draft (<c>Draft: </c>, the description
    /// linking back to the AIDE project, <c>remove_source_branch: false</c>). A create that races another (409/422) looks
    /// it up again, so one merge request exists per release version. The outcome's number is the <c>iid</c>.
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

        var repoId = settings.ExternalRepoId!;
        var existing = await ApiAsync(settings, access, (c, t) => c.FindOpenMergeRequestAsync(t, repoId, request.HeadBranch, settings.DefaultBranch, cancellationToken), cancellationToken);
        if (existing.Failure is { } lookupFailure)
        {
            return ToPullRequestFailure(lookupFailure);
        }

        if (existing.Value is { } open)
        {
            var updated = await ApiAsync(
                settings,
                access,
                (c, t) => c.UpdateMergeRequestAsync(t, repoId, open.Iid, GitLabProjectRules.ReusedTitle(open.Title, request.Title), request.Body, cancellationToken),
                cancellationToken);
            if (updated.Failure is { Transient: true } transient)
            {
                return ToPullRequestFailure(transient);
            }

            LogMergeRequestReused(logger, settings.ProjectId, open.Iid);
            return PullRequestOutcome.Opened(open.Iid, WebUrl(open.WebUrl), reused: true);
        }

        var created = await ApiAsync(
            settings,
            access,
            (c, t) => c.CreateMergeRequestAsync(t, repoId, request.HeadBranch, settings.DefaultBranch, GitLabProjectRules.DraftTitle(request.Title), request.Body, cancellationToken),
            cancellationToken,
            conflictStatuses: [409, 422]);
        if (created.Conflict)
        {
            var raced = await ApiAsync(settings, access, (c, t) => c.FindOpenMergeRequestAsync(t, repoId, request.HeadBranch, settings.DefaultBranch, cancellationToken), cancellationToken);
            if (raced.Value is { } other)
            {
                return PullRequestOutcome.Opened(other.Iid, WebUrl(other.WebUrl), reused: true);
            }

            return raced.Failure is { } racedFailure
                ? ToPullRequestFailure(racedFailure)
                : PullRequestOutcome.Failed(created.ConflictReason ?? "GitLab refused the merge request.", transient: false);
        }

        if (created.Failure is { } createFailure)
        {
            return ToPullRequestFailure(createFailure);
        }

        LogMergeRequestOpened(logger, settings.ProjectId, created.Value!.Iid);
        return PullRequestOutcome.Opened(created.Value.Iid, WebUrl(created.Value.WebUrl), reused: false);
    }

    /// <summary>
    /// FR-137 (FR-65 on GitLab): marks the draft merge request <paramref name="iid"/> ready by removing the draft marker from
    /// its title (a ready one is left as it is). Used by the release (slice 2b).
    /// </summary>
    public async Task<GitOutcome> MarkMergeRequestReadyAsync(GitConnectionSettings settings, int iid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            return failure;
        }

        var repoId = settings.ExternalRepoId!;
        var current = await ApiAsync(settings, access, (c, t) => c.GetMergeRequestAsync(t, repoId, iid, cancellationToken), cancellationToken);
        if (current.Failure is { } unreadable)
        {
            return unreadable;
        }

        if (current.Value is not { } mr)
        {
            return GitOutcome.Failed($"Merge request !{iid} doesn't exist in {settings.RepoFullName}.", transient: false, ErrorCodes.GitError);
        }

        if (!GitLabProjectRules.IsDraftTitle(mr.Title))
        {
            return GitOutcome.Success;
        }

        var ready = await ApiAsync(settings, access, (c, t) => c.UpdateMergeRequestAsync(t, repoId, iid, GitLabProjectRules.ReadyTitle(mr.Title), null, cancellationToken), cancellationToken);
        return ready.Failure ?? GitOutcome.Success;
    }

    public string ComparisonBaseRef(GitConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return $"refs/remotes/{GitRemote.Name}/{settings.DefaultBranch}";
    }

    /// <summary>FR-61: <c>GET /projects/:id/repository/tags/:tag</c> (404 = no such tag).</summary>
    public async Task<TagLookup> FindTagAsync(GitConnectionSettings settings, string tag, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(tag);
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            return TagLookup.Failed(failure.Reason!, failure.Transient);
        }

        var found = await ApiAsync(settings, access, (c, t) => c.FindTagCommitAsync(t, settings.ExternalRepoId!, tag, cancellationToken), cancellationToken);
        return found.Failure is { } lookupFailure
            ? TagLookup.Failed(lookupFailure.Reason!, lookupFailure.Transient)
            : found.Value is null ? TagLookup.Missing : TagLookup.Found;
    }

    // ---------------------------------------------------------------- §4.19 user-scoped operations

    public Task<Result<GitHostRepoPage>> ListReposAsync(Guid connectionId, string? search, string? cursor, CancellationToken cancellationToken) =>
        UserCallAsync(connectionId, async (c, t) =>
        {
            var page = await c.ListProjectsAsync(t, search, cursor, cancellationToken);
            return Result.Success(new GitHostRepoPage(
                [.. page.Items.Select(p => new GitHostRepo(p.ExternalId, p.PathWithNamespace, p.Name, p.Path, p.DefaultBranch, p.WebUrl, p.NamespaceFullPath, p.Visibility, p.LastActivityAt))],
                page.NextCursor));
        }, cancellationToken);

    public Task<Result<IReadOnlyList<GitHostOwner>>> ListOwnersAsync(Guid connectionId, CancellationToken cancellationToken) =>
        UserCallAsync(connectionId, async (c, t) => Result.Success(await GitLabReaders.OwnersAsync(c, t, cancellationToken)), cancellationToken);

    public Task<Result<GitHostRepo>> CreateRepoAsync(Guid connectionId, GitHostCreateRepo request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var valid = GitLabRules.ValidateNewProject(request.Name, request.Path, request.Description);
        if (valid.IsFailure)
        {
            return Task.FromResult(Result.Failure<GitHostRepo>(valid.Error!));
        }

        return UserCallAsync(connectionId, async (c, t) =>
        {
            var user = await c.GetUserAsync(t, cancellationToken);
            var ownerPath = request.Owner.Trim().Trim('/');
            long? namespaceId = null;
            if (!string.Equals(ownerPath, user.Username, StringComparison.OrdinalIgnoreCase))
            {
                if (await c.GetNamespaceAsync(t, ownerPath, cancellationToken) is not { } ns)
                {
                    return Result.Failure<GitHostRepo>(new DomainError(ErrorCodes.NotFound, "That GitLab group wasn't found, or your account can't see it."));
                }

                namespaceId = ns.Id;
            }

            try
            {
                var p = await c.CreateProjectAsync(
                    t, new GitLabCreateProject(request.Name.Trim(), request.Path?.Trim(), namespaceId, request.Description?.Trim(), request.Private), cancellationToken);
                return Result.Success(new GitHostRepo(p.ExternalId, p.PathWithNamespace, p.Name, p.Path, p.DefaultBranch, p.WebUrl, p.NamespaceFullPath, p.Visibility, p.LastActivityAt));
            }
            catch (GitLabApiException ex) when (ex.Status is 400 or 403 or 409 or 422)
            {
                var code = GitLabRules.CreateProjectErrorCode(ex.Status!.Value, ex.UpstreamMessage);
                return Result.Failure<GitHostRepo>(new DomainError(code, code switch
                {
                    ErrorCodes.RepoNameTaken => "A repository with that name already exists.",
                    ErrorCodes.Forbidden => "GitLab doesn't let your account create projects there.",
                    ErrorCodes.UpstreamRateLimited => "GitLab is rate limiting project creation. Try again in a few minutes.",
                    _ => "GitLab refused the project.",
                }));
            }
        }, cancellationToken);
    }

    /// <summary>FR-141: the repo's default branch, releases and tags, and the suggested released version (tag prefix <c>v</c>).</summary>
    public Task<Result<GitHostReleaseRefs>> FindReleaseRefsAsync(Guid connectionId, string repoId, CancellationToken cancellationToken) =>
        UserCallAsync(connectionId, async (c, t) =>
            await GitLabReaders.ReleaseRefsAsync(c, t, repoId, ReleaseVersion.DefaultTagPrefix, cancellationToken) is { } refs
                ? Result.Success(refs)
                : Result.Failure<GitHostReleaseRefs>(new DomainError(ErrorCodes.NotFound, "The GitLab repository wasn't found, or your account can't see it.")),
            cancellationToken);

    // ---------------------------------------------------------------- helpers

    /// <summary>The binding connection's token and its instance, or why Git work can't run (FR-136).</summary>
    private async Task<Access> AccessAsync(GitConnectionSettings settings, CancellationToken cancellationToken)
    {
        if (settings.InstanceId is not { } instanceId || string.IsNullOrEmpty(settings.ExternalRepoId) || string.IsNullOrEmpty(settings.RepoFullName))
        {
            return Access.Failed(GitOutcome.Failed("The project's GitLab repository isn't recorded. Create the project again with a repository from the GitLab picker.", transient: false, ErrorCodes.GitError));
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

        if (token.Value.InstanceId != instanceId)
        {
            return Access.Failed(GitOutcome.Failed(
                "The bound GitLab connection is for a different instance than the project's repository. An Owner can re-bind their own connection in Settings → Git.",
                transient: false,
                ErrorCodes.GitNotConnected));
        }

        var instance = await instances.GetAsync(instanceId, cancellationToken);
        return new Access(token.Value, instance?.AllowInsecureHttp ?? false, null);
    }

    private static GitOutcome TokenFailure(DomainError error) => error.Code switch
    {
        ErrorCodes.UpstreamError => GitOutcome.Failed(error.Message, transient: true, ErrorCodes.UpstreamError),
        ErrorCodes.GitHostNotAllowed => GitOutcome.Failed(error.Message, transient: false, ErrorCodes.GitHostNotAllowed),
        _ => GitOutcome.Failed(error.Message, transient: false, ErrorCodes.GitNotConnected),
    };

    /// <summary>
    /// Runs a GitLab API call for the project with the binding connection: a 401 refreshes once (then marks the connection
    /// invalid, clearing the binding: <c>GIT_NOT_CONNECTED</c>); a 429 throws <see cref="GitLabRateLimitedException"/>; a
    /// status in <paramref name="conflictStatuses"/> is returned as a conflict; other failures are token-free reasons.
    /// </summary>
    private async Task<Call<T>> ApiAsync<T>(
        GitConnectionSettings settings, Access access, Func<GitLabClient, string, Task<T>> call, CancellationToken cancellationToken, int[]? conflictStatuses = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = access.Token!;
            try
            {
                return new Call<T>(await call(clients.ForInstance(token.InstanceBaseUrl), token.Token), null);
            }
            catch (GitLabApiException ex) when (ex.IsUnauthorized && attempt == 0)
            {
                var refreshed = await tokens.OnUnauthorizedAsync(token, settings.ProjectId, cancellationToken);
                if (refreshed.IsFailure)
                {
                    return Call<T>.Failed(TokenFailure(refreshed.Error!));
                }

                access.Token = refreshed.Value;
            }
            catch (GitLabApiException ex) when (ex.IsRateLimited)
            {
                throw RateLimited(ex.RetryAfter);
            }
            catch (GitLabApiException ex) when (ex.Status is { } status && conflictStatuses?.Contains(status) == true)
            {
                return new Call<T>(default, null) { Conflict = true, ConflictReason = Describe(ex), ConflictStatus = status };
            }
            catch (GitLabApiException ex)
            {
                LogApiFailed(logger, settings.ProjectId, ex.Status);
                var transient = ex.Status is null or >= 500 or 408;
                return Call<T>.Failed(GitOutcome.Failed(Describe(ex), transient, ex.IsUnauthorized ? ErrorCodes.GitNotConnected : ErrorCodes.GitError));
            }
            catch (GitHostBlockedException)
            {
                return Call<T>.Failed(GitOutcome.Failed("The GitLab instance's address isn't allowed on this server.", transient: false, ErrorCodes.GitHostNotAllowed));
            }
        }
    }

    /// <summary>
    /// A git command against the project's repository with the binding connection's token in the per-command header. An
    /// authentication failure refreshes the token once (as the API does); a 429 throws <see cref="GitLabRateLimitedException"/>.
    /// <paramref name="run"/> returns a failure text (or <c>null</c> on success).
    /// </summary>
    private async Task<GitOutcome> GitAsync(GitConnectionSettings settings, string operation, Func<GitRemote, Task<string?>> run, CancellationToken cancellationToken)
    {
        var access = await AccessAsync(settings, cancellationToken);
        if (access.Failure is { } failure)
        {
            LogGitFailed(logger, settings.ProjectId, operation, failure.Reason!);
            return failure;
        }

        // §8: git reaches only addresses the SSRF guard allows (resolved here, pinned for the git process).
        var current = options.CurrentValue;
        IReadOnlyList<System.Net.IPAddress>? pinned = null;
        if (string.IsNullOrWhiteSpace(current.GitBaseUrlOverride))
        {
            try
            {
                pinned = await connector.ResolveAllowedAsync(access.Token!.InstanceBaseUrl.IdnHost, cancellationToken);
            }
            catch (GitHostBlockedException ex)
            {
                LogGitFailed(logger, settings.ProjectId, operation, "blocked by the SSRF guard");
                return GitOutcome.Failed($"The GitLab instance's address isn't allowed on this server. {Clean(ex.Message)}", transient: false, ErrorCodes.GitHostNotAllowed);
            }
            catch (HttpRequestException)
            {
                LogGitFailed(logger, settings.ProjectId, operation, "name resolution failed");
                return GitOutcome.Failed("The GitLab instance's host name couldn't be resolved. Try again later.", transient: true, ErrorCodes.GitError);
            }
        }

        for (var attempt = 0; ; attempt++)
        {
            var token = access.Token!;
            var remote = GitLabRemotes.Create(token.InstanceBaseUrl, access.AllowInsecureHttp, settings.RepoFullName!, token.Token, current, pinned);
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
    private async Task<Result<T>> UserCallAsync<T>(Guid connectionId, Func<GitLabClient, string, Task<Result<T>>> call, CancellationToken cancellationToken)
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
                return await call(clients.ForInstance(token.InstanceBaseUrl), token.Token);
            }
            catch (GitLabApiException ex) when (ex.IsUnauthorized && attempt == 0)
            {
                access = await tokens.OnUnauthorizedAsync(token, null, cancellationToken);
            }
            catch (GitLabApiException ex)
            {
                return Result.Failure<T>(ex.Status switch
                {
                    401 => new DomainError(ErrorCodes.GitNotConnected, "GitLab rejected the connection. Connect GitLab again under Account → Connections → GitLab."),
                    429 => new DomainError(ErrorCodes.UpstreamRateLimited, "GitLab is rate limiting requests. Try again in a few minutes.") { RetryAfter = ex.RetryAfter },
                    403 => new DomainError(ErrorCodes.Forbidden, "GitLab refused: your GitLab account doesn't have permission to do that."),
                    404 => new DomainError(ErrorCodes.NotFound, "GitLab couldn't find that, or your account can't see it."),
                    _ => new DomainError(ErrorCodes.UpstreamError, ex.Status is null ? "GitLab couldn't be reached. Try again." : "GitLab returned an error. Try again later."),
                });
            }
            catch (GitHostBlockedException ex)
            {
                return Result.Failure<T>(new DomainError(ErrorCodes.GitHostNotAllowed, "That address isn't allowed for a GitLab instance.") { Details = [ex.Message] });
            }
        }
    }

    private GitLabRateLimitedException RateLimited(TimeSpan? retryAfter)
    {
        var now = time.GetUtcNow();
        var wait = retryAfter is { } after && after > TimeSpan.Zero ? after : GitHostPollBudget.DefaultRateLimitWait;
        return new GitLabRateLimitedException(now + (wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait));
    }

    private static PullRequestOutcome ToPullRequestFailure(GitOutcome failure) =>
        PullRequestOutcome.Failed(failure.Reason!, failure.Transient) with { ErrorCode = failure.ErrorCode };

    /// <summary>The merge request's web URL when it is an absolute http(s) URL of at most 500 characters.</summary>
    private static string? WebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.OriginalString.Length <= 500
            ? uri.OriginalString
            : null;

    private static string Describe(GitLabApiException ex) =>
        Clean($"GitLab: {ex.UpstreamMessage ?? "request failed"}{(ex.Status is { } s ? $" (HTTP {s})" : string.Empty)}");

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
    private sealed class Access(GitAccessToken? token, bool allowInsecureHttp, GitOutcome? failure)
    {
        public GitAccessToken? Token { get; set; } = token;

        public bool AllowInsecureHttp { get; } = allowInsecureHttp;

        public GitOutcome? Failure { get; } = failure;

        public static Access Failed(GitOutcome failure) => new(null, false, failure);
    }

    private sealed record Call<T>(T? Value, GitOutcome? Failure)
    {
        public bool Conflict { get; init; }

        public string? ConflictReason { get; init; }

        public int? ConflictStatus { get; init; }

        public static Call<T> Failed(GitOutcome failure) => new(default, failure);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Git {Operation} for GitLab project {ProjectId} failed: {Reason}")]
    private static partial void LogGitFailed(ILogger logger, Guid projectId, string operation, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitLab API call for project {ProjectId} failed with HTTP {Status}")]
    private static partial void LogApiFailed(ILogger logger, Guid projectId, int? status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened draft merge request !{Iid} for project {ProjectId}")]
    private static partial void LogMergeRequestOpened(ILogger logger, Guid projectId, int iid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reusing open merge request !{Iid} for project {ProjectId}")]
    private static partial void LogMergeRequestReused(ILogger logger, Guid projectId, int iid);
}

/// <summary>How a git-over-HTTPS failure against GitLab reads (FR-136).</summary>
public static class GitLabGitErrors
{
    /// <summary>GitLab rejected the credentials (401, or git's "Authentication failed").</summary>
    public static bool IsUnauthorized(string? error)
    {
        var text = error ?? string.Empty;
        return text.Contains("returned error: 401", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP Basic: Access denied", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GitLab rate limited the git request (429).</summary>
    public static bool IsRateLimited(string? error) =>
        (error ?? string.Empty).Contains("returned error: 429", StringComparison.OrdinalIgnoreCase);
}
