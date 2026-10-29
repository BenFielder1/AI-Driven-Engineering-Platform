using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(options);

        var overridden = !string.IsNullOrWhiteSpace(options.GitBaseUrlOverride);
        var baseUrl = overridden ? options.GitBaseUrlOverride!.Trim() : instanceBaseUrl.AbsoluteUri;
        baseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        var url = $"{baseUrl}{fullPath.Trim('/')}.git";
        var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{GitTokenRules.GitBasicUser(GitCredentialKind.Gitlab)}:{token}"));
        var header = $"Authorization: Basic {credential}";
        var http = baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // The header is scoped to the instance's base URL; only https (or http for a lab instance that allows it).
        List<(string Key, string Value)> settings =
        [
            (http ? $"http.{baseUrl}.extraHeader" : "http.extraHeader", header),
            ("protocol.allow", "never"),
            ("protocol.https.allow", "always"),
            ("http.followRedirects", "false"),
        ];
        if (!overridden)
        {
            if (pinnedAddresses is not { Count: > 0 })
            {
                throw new ArgumentException("The instance's vetted addresses are required.", nameof(pinnedAddresses));
            }

            settings.Add(("http.curloptResolve", CurlResolve(instanceBaseUrl, pinnedAddresses)));
            settings.Add(("http.proxy", string.Empty));
        }
        if (allowInsecureHttp && !overridden)
        {
            settings.Add(("protocol.http.allow", "always"));
        }

        if (overridden && options.AllowInsecureGitBaseUrl)
        {
            settings.Add(("protocol.file.allow", "always"));
            settings.Add(("protocol.http.allow", "always"));
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_COUNT"] = settings.Count.ToString(CultureInfo.InvariantCulture),
        };
        for (var i = 0; i < settings.Count; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            environment[$"GIT_CONFIG_KEY_{n}"] = settings[i].Key;
            environment[$"GIT_CONFIG_VALUE_{n}"] = settings[i].Value;
        }

        return new GitRemote(url, environment, [header, credential, token]);
    }

    /// <summary>
    /// The <c>CURLOPT_RESOLVE</c> entry pinning the instance's host and port to the vetted addresses:
    /// <c>host:port:addr[,addr]</c>, IPv6 addresses in brackets.
    /// </summary>
    public static string CurlResolve(Uri instanceBaseUrl, IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(instanceBaseUrl);
        ArgumentNullException.ThrowIfNull(addresses);
        var list = string.Join(',', addresses.Select(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]" : a.ToString()));
        return $"{instanceBaseUrl.IdnHost}:{instanceBaseUrl.Port.ToString(CultureInfo.InvariantCulture)}:{list}";
    }
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
