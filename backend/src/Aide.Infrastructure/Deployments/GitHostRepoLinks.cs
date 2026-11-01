using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Bitbucket;
using Aide.Infrastructure.GitLab;
using Aide.Infrastructure.Platforms;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// §4.19 "Netlify link support", §8, D-V4-16: a GitLab or Bitbucket repository's deploy keys and push webhook, through the
/// project's Git binding. Failures throw <see cref="DeploymentProviderException"/> for the Netlify target, and are about the
/// Git connection, never the Netlify one: a rejected Git token is <see cref="DeploymentFailure.GitNotConnected"/>, not
/// <see cref="DeploymentFailure.CredentialRejected"/> (which would mark the Netlify connection invalid).
/// </summary>
public interface IGitHostRepoLinks
{
    Task<IReadOnlyList<RepoDeployKey>> ListDeployKeysAsync(Guid projectId, DeploymentRepoRef repo, CancellationToken cancellationToken);

    /// <summary>§8: adds <paramref name="publicKey"/> read-only. The same key added meanwhile by another attempt is fine.</summary>
    Task AddReadOnlyDeployKeyAsync(Guid projectId, DeploymentRepoRef repo, string title, string publicKey, CancellationToken cancellationToken);

    /// <summary>§8: makes sure one webhook on exactly <paramref name="url"/> fires on pushes: adds it, or turns on the existing one.</summary>
    Task EnsurePushWebhookAsync(Guid projectId, DeploymentRepoRef repo, string url, string description, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IGitHostRepoLinks"/> through the existing clients: <see cref="IGitLabClients"/> (bound to the instance, behind the
/// §8 SSRF guard) and <see cref="IBitbucketClients"/> (the fixed API host, the connection's budget). The token comes from the
/// binding connection per call and must be for the repository's host and instance. A 401 refreshes once, then marks the Git
/// connection invalid (<see cref="IGitConnectionTokens.OnUnauthorizedAsync"/>); a 403 is
/// <see cref="DeploymentFailure.PermissionMissing"/>; a 429 is <see cref="DeploymentFailure.RateLimited"/> with its wait.
/// </summary>
public sealed class GitHostRepoLinks(IGitConnectionTokens tokens, IGitLabClients gitlab, IBitbucketClients bitbucket, TimeProvider time) : IGitHostRepoLinks
{
    private const DeploymentProvider Netlify = DeploymentProvider.Netlify;

    public Task<IReadOnlyList<RepoDeployKey>> ListDeployKeysAsync(Guid projectId, DeploymentRepoRef repo, CancellationToken cancellationToken) =>
        CallAsync<IReadOnlyList<RepoDeployKey>>(
            projectId,
            repo,
            async (client, token) => [.. (await client.ListDeployKeysAsync(token.Token, repo.ExternalId!, cancellationToken)).Select(k => new RepoDeployKey(k.Key, !k.CanPush))],
            async (client, auth) => [.. (await client.ListDeployKeysAsync(auth, repo.FullName!, cancellationToken)).Select(k => new RepoDeployKey(k, true))],
            cancellationToken);

    public async Task AddReadOnlyDeployKeyAsync(Guid projectId, DeploymentRepoRef repo, string title, string publicKey, CancellationToken cancellationToken)
    {
        try
        {
            await CallAsync(
                projectId,
                repo,
                async (client, token) =>
                {
                    await client.AddReadOnlyDeployKeyAsync(token.Token, repo.ExternalId!, title, publicKey, cancellationToken);
                    return true;
                },
                async (client, auth) =>
                {
                    await client.AddDeployKeyAsync(auth, repo.FullName!, title, publicKey, cancellationToken);
                    return true;
                },
                cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Status is 400 or 409 or 422)
        {
            // Another attempt may have added it; anything else stays the refusal it was.
            var keys = await ListDeployKeysAsync(projectId, repo, cancellationToken);
            if (!keys.Any(k => k.ReadOnly && NetlifyRules.SamePublicKey(k.PublicKey, publicKey)))
            {
                throw;
            }
        }
    }

    public Task EnsurePushWebhookAsync(Guid projectId, DeploymentRepoRef repo, string url, string description, CancellationToken cancellationToken) =>
        CallAsync(
            projectId,
            repo,
            async (client, token) =>
            {
                var hooks = await client.ListHooksAsync(token.Token, repo.ExternalId!, cancellationToken);
                var found = hooks.FirstOrDefault(h => string.Equals(h.Url, url, StringComparison.Ordinal));
                if (found is null)
                {
                    await client.AddPushHookAsync(token.Token, repo.ExternalId!, url, cancellationToken);
                }
                else if (!found.PushEvents)
                {
                    await client.EnablePushHookAsync(token.Token, repo.ExternalId!, found.Id, url, cancellationToken);
                }

                return true;
            },
            async (client, auth) =>
            {
                var hooks = await client.ListHooksAsync(auth, repo.FullName!, cancellationToken);
                var found = hooks.FirstOrDefault(h => string.Equals(h.Url, url, StringComparison.Ordinal));
                if (found is null)
                {
                    await client.AddPushHookAsync(auth, repo.FullName!, description, url, cancellationToken);
                }
                else if (!found.Active || !found.Events.Contains("repo:push", StringComparer.Ordinal))
                {
                    await client.EnablePushHookAsync(auth, repo.FullName!, found.Uuid!, description, url, cancellationToken);
                }

                return true;
            },
            cancellationToken);

    private async Task<T> CallAsync<T>(
        Guid projectId,
        DeploymentRepoRef repo,
        Func<GitLabClient, GitAccessToken, Task<T>> onGitLab,
        Func<BitbucketClient, BitbucketAuth, Task<T>> onBitbucket,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var isGitLab = repo.Host == "gitlab";
        var host = isGitLab ? "GitLab" : "Bitbucket";
        var token = await BindingTokenAsync(repo, isGitLab ? GitHostKind.Gitlab : GitHostKind.Bitbucket, host, cancellationToken);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return isGitLab
                    ? await onGitLab(gitlab.ForInstance(token.InstanceBaseUrl), token)
                    : await onBitbucket(
                        bitbucket.Create(token.ConnectionId),
                        token.Email is { } email ? BitbucketAuth.ApiToken(email, token.Token) : BitbucketAuth.OAuth(token.Token));
            }
            catch (Exception ex) when (ex is GitLabApiException { IsUnauthorized: true } or BitbucketApiException { IsUnauthorized: true })
            {
                // OAuth refreshes once; a token that can't refresh, or a second 401, marks the Git connection invalid.
                var refreshed = await tokens.OnUnauthorizedAsync(token, projectId, cancellationToken);
                if (refreshed.IsFailure)
                {
                    throw TokenFailure(refreshed.Error!, repo.Host);
                }

                if (attempt > 0)
                {
                    throw NotConnected($"{host} rejected the project's Git connection.", repo.Host);
                }

                token = refreshed.Value;
            }
            catch (GitLabApiException ex)
            {
                throw Failure(host, ex.Status, ex.UpstreamMessage, ex.RetryAfter, GitHostPollBudget.DefaultRateLimitWait, PermissionHelp(isGitLab, false));
            }
            catch (BitbucketApiException ex)
            {
                throw Failure(host, ex.Status, ex.UpstreamMessage, ex.RetryAfter, GitHostPollBudget.BitbucketRateLimitWait, PermissionHelp(isGitLab, ex.ScopeError));
            }
            catch (GitHostBlockedException)
            {
                throw new DeploymentProviderException(DeploymentFailure.Rejected, null, "The GitLab instance's address isn't allowed on this server.", platform: Netlify);
            }
        }
    }

    /// <summary>FR-136: the binding connection's token, which must be for the repository's host and instance.</summary>
    private async Task<GitAccessToken> BindingTokenAsync(DeploymentRepoRef repo, GitHostKind kind, string host, CancellationToken cancellationToken)
    {
        if (repo.InstanceId is not { } instanceId || string.IsNullOrEmpty(repo.ExternalId) || string.IsNullOrEmpty(repo.FullName))
        {
            throw new DeploymentProviderException(DeploymentFailure.Rejected, null, $"The project's {host} repository isn't recorded.", platform: Netlify);
        }

        if (repo.GitConnectionId is not { } connectionId)
        {
            throw NotConnected(GitBindingRules.CanRunGitJob(null, instanceEnabled: true, host).Error!.Message, repo.Host);
        }

        var token = await tokens.GetAccessTokenAsync(connectionId, cancellationToken);
        if (token.IsFailure)
        {
            throw TokenFailure(token.Error!, repo.Host);
        }

        // §8: a token stays with the instance that issued it.
        return token.Value.Provider == kind && token.Value.InstanceId == instanceId
            ? token.Value
            : throw NotConnected($"The bound connection isn't a {host} connection for the project's repository. An Owner can re-bind their own connection in Settings → Git.", repo.Host);
    }

    private static DeploymentProviderException TokenFailure(DomainError error, string? host) => error.Code switch
    {
        ErrorCodes.UpstreamError => new DeploymentProviderException(DeploymentFailure.Upstream, null, error.Message, platform: Netlify),
        ErrorCodes.GitHostNotAllowed => new DeploymentProviderException(DeploymentFailure.Rejected, null, error.Message, platform: Netlify),
        _ => NotConnected(error.Message, host),
    };

    private static DeploymentProviderException NotConnected(string message, string? host) =>
        new(DeploymentFailure.GitNotConnected, 409, message, DeploymentPlatforms.GitConnectUrl(Netlify, host), platform: Netlify);

    private static string PermissionHelp(bool isGitLab, bool scopeError) => isGitLab
        ? "Linking Netlify needs the bound GitLab account to be a Maintainer of the project, with the api scope."
        : scopeError
            ? "Linking Netlify needs the bound Bitbucket connection to manage deploy keys and webhooks: an API token with admin:repository:bitbucket, read:webhook:bitbucket and write:webhook:bitbucket, or an OAuth consumer with repository:admin and webhook."
            : "Linking Netlify needs the bound Bitbucket account to be an admin of the repository.";

    private DeploymentProviderException Failure(string host, int? status, string? upstream, TimeSpan? retryAfter, TimeSpan defaultWait, string permissionHelp)
    {
        var message = $"{host}: {PlatformHttp.Clean(upstream ?? "request failed")}{(status is { } s ? $" (HTTP {s})" : string.Empty)}";
        return status switch
        {
            429 => new DeploymentProviderException(DeploymentFailure.RateLimited, 429, message, retryAt: time.GetUtcNow() + Wait(retryAfter, defaultWait), platform: Netlify),
            403 => new DeploymentProviderException(DeploymentFailure.PermissionMissing, 403, $"{permissionHelp} ({message})", platform: Netlify),
            404 => new DeploymentProviderException(DeploymentFailure.NotFound, 404, message, platform: Netlify),
            >= 400 and < 500 and not 408 => new DeploymentProviderException(DeploymentFailure.Rejected, status, message, platform: Netlify),
            _ => new DeploymentProviderException(DeploymentFailure.Upstream, status, message, platform: Netlify),
        };
    }

    private static TimeSpan Wait(TimeSpan? retryAfter, TimeSpan defaultWait)
    {
        var wait = retryAfter is { } after && after > TimeSpan.Zero ? after : defaultWait;
        return wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait;
    }
}
