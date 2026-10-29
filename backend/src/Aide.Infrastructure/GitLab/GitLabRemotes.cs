using System.Net;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Git;

namespace Aide.Infrastructure.GitLab;

/// <summary>
/// FR-136: how AIDE reaches a GitLab repository with git. The URL is <c>{instance base}/{group/sub/project}.git</c> (the
/// host is the Admin-registered instance, never a project field) and holds no credentials. The binding connection's token
/// travels only as a per-command <c>http.&lt;base&gt;.extraHeader</c> Basic <c>oauth2:&lt;token&gt;</c> in the git child
/// process's environment, so it is never written to <c>.git/config</c>, the remote URL, the command line, logs or events.
/// §8 SSRF guard: git connects only to the addresses the guard vetted (the <c>pinnedAddresses</c> of
/// <see cref="Create"/>, pinned with <c>http.curloptResolve</c> so git never resolves the name itself) and never follows a
/// redirect (<c>http.followRedirects=false</c>); proxies are off (<c>http.proxy</c> empty) so none can bypass the check.
/// </summary>
public static class GitLabRemotes
{
    /// <param name="instanceBaseUrl">The instance's base URL.</param>
    /// <param name="allowInsecureHttp">The instance allows http (a lab host, FR-130).</param>
    /// <param name="fullPath">The repository's <c>group/sub/project</c>.</param>
    /// <param name="token">The binding connection's access token.</param>
    /// <param name="options">GitLab settings (the tests-only override).</param>
    /// <param name="pinnedAddresses">The addresses the SSRF guard allowed for the instance's host (required unless the
    /// tests-only override is set).</param>
    public static GitRemote Create(
        Uri instanceBaseUrl, bool allowInsecureHttp, string fullPath, string token, GitLabOptions options, IReadOnlyList<IPAddress>? pinnedAddresses = null)
    {
        ArgumentNullException.ThrowIfNull(instanceBaseUrl);
        ArgumentNullException.ThrowIfNull(options);
        var transport = new GitTransportOverride(options.GitBaseUrlOverride, options.AllowInsecureGitBaseUrl);
        if (!transport.IsSet && pinnedAddresses is not { Count: > 0 })
        {
            throw new ArgumentException("The instance's vetted addresses are required.", nameof(pinnedAddresses));
        }

        return GitHostRemotes.Create(instanceBaseUrl, fullPath, GitCredentialKind.Gitlab, token, transport, allowInsecureHttp, pinnedAddresses);
    }

    /// <summary>
    /// The <c>CURLOPT_RESOLVE</c> entry pinning the instance's host and port to the vetted addresses:
    /// <c>host:port:addr[,addr]</c>, IPv6 addresses in brackets.
    /// </summary>
    public static string CurlResolve(Uri instanceBaseUrl, IReadOnlyList<IPAddress> addresses) => GitHostRemotes.CurlResolve(instanceBaseUrl, addresses);
}

/// <summary>
/// FR-136, §4.19: GitLab rate limited a background job (429) until <see cref="ResetAt"/>. The job worker re-queues the job
/// for then without counting the attempt (<c>GITLAB_RATE_LIMITED</c>); request paths answer 429 <c>UPSTREAM_RATE_LIMITED</c>.
/// </summary>
public sealed class GitLabRateLimitedException : Exception
{
    public GitLabRateLimitedException()
        : this(DateTimeOffset.UtcNow.AddMinutes(1))
    {
    }

    public GitLabRateLimitedException(string message)
        : base(message)
    {
        ResetAt = DateTimeOffset.UtcNow.AddMinutes(1);
    }

    public GitLabRateLimitedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ResetAt = DateTimeOffset.UtcNow.AddMinutes(1);
    }

    public GitLabRateLimitedException(DateTimeOffset resetAt)
        : base($"GitLab is rate limiting requests until {resetAt:u}.")
    {
        ResetAt = resetAt;
    }

    public DateTimeOffset ResetAt { get; }
}
