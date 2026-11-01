using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Aide.Domain.GitHosts;

namespace Aide.Infrastructure.Git;

/// <summary>The tests-only redirect of a Git host's clone/push URLs to a <c>file://</c> folder of bare repositories.</summary>
/// <param name="GitBaseUrlOverride">Clone and push go to <c>{override}{fullPath}.git</c> instead of the host.</param>
/// <param name="AllowInsecureGitBaseUrl">Allows a <c>file://</c> or <c>http://</c> override.</param>
public sealed record GitTransportOverride(string? GitBaseUrlOverride, bool AllowInsecureGitBaseUrl)
{
    public static GitTransportOverride None { get; } = new(null, false);

    public bool IsSet => !string.IsNullOrWhiteSpace(GitBaseUrlOverride);
}

/// <summary>
/// FR-136: how AIDE reaches a GitLab or Bitbucket repository with git. The URL is <c>{host base}/{full path}.git</c> (the
/// host is a fixed or Admin-registered one, never a project field) and holds no credentials. The connection's token travels
/// only as a per-command <c>http.&lt;base&gt;.extraHeader</c> Basic <c>&lt;user&gt;:&lt;token&gt;</c>
/// (<see cref="GitTokenRules.GitBasicUser"/>: <c>oauth2</c>, <c>x-token-auth</c>, <c>x-bitbucket-api-token-auth</c>) in the
/// git child process's environment, so it is never written to <c>.git/config</c>, the remote URL, the command line, logs or
/// events. Redirects are never followed (<c>http.followRedirects=false</c>) and only https is allowed (plus http for a lab
/// GitLab instance). With <c>pinnedAddresses</c> (§8 SSRF guard for GitLab instances) git connects only to those addresses
/// (<c>http.curloptResolve</c>) and proxies are off.
/// </summary>
public static class GitHostRemotes
{
    /// <param name="hostBaseUrl">The host's base URL (GitLab instance, or <c>https://bitbucket.org</c>).</param>
    /// <param name="fullPath">The repository's <c>group/sub/project</c> or <c>workspace/slug</c>.</param>
    /// <param name="credential">How the token authenticates (the Basic user name).</param>
    /// <param name="token">The connection's access token.</param>
    /// <param name="transport">The tests-only override (<see cref="GitTransportOverride.None"/> in a deployment).</param>
    /// <param name="allowInsecureHttp">The host allows http (a lab GitLab instance, FR-130).</param>
    /// <param name="pinnedAddresses">The addresses the SSRF guard allowed for the host (GitLab instances); <c>null</c> for a
    /// fixed public host.</param>
    public static GitRemote Create(
        Uri hostBaseUrl,
        string fullPath,
        GitCredentialKind credential,
        string token,
        GitTransportOverride transport,
        bool allowInsecureHttp = false,
        IReadOnlyList<IPAddress>? pinnedAddresses = null)
    {
        ArgumentNullException.ThrowIfNull(hostBaseUrl);
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(transport);

        var overridden = transport.IsSet;
        var baseUrl = overridden ? transport.GitBaseUrlOverride!.Trim() : hostBaseUrl.AbsoluteUri;
        baseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        var url = $"{baseUrl}{fullPath.Trim('/')}.git";
        var credentialText = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{GitTokenRules.GitBasicUser(credential)}:{token}"));
        var header = $"Authorization: Basic {credentialText}";
        var http = baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // The header is scoped to the host's base URL; only https (or http for a lab instance that allows it).
        List<(string Key, string Value)> settings =
        [
            (http ? $"http.{baseUrl}.extraHeader" : "http.extraHeader", header),
            ("protocol.allow", "never"),
            ("protocol.https.allow", "always"),
            ("http.followRedirects", "false"),
        ];
        if (!overridden && pinnedAddresses is { Count: > 0 })
        {
            settings.Add(("http.curloptResolve", CurlResolve(hostBaseUrl, pinnedAddresses)));
            settings.Add(("http.proxy", string.Empty));
        }

        if (allowInsecureHttp && !overridden)
        {
            settings.Add(("protocol.http.allow", "always"));
        }

        if (overridden && transport.AllowInsecureGitBaseUrl)
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

        return new GitRemote(url, environment, [header, credentialText, token]);
    }

    /// <summary>
    /// The <c>CURLOPT_RESOLVE</c> entry pinning the host and port to the vetted addresses: <c>host:port:addr[,addr]</c>,
    /// IPv6 addresses in brackets.
    /// </summary>
    public static string CurlResolve(Uri hostBaseUrl, IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(hostBaseUrl);
        ArgumentNullException.ThrowIfNull(addresses);
        var list = string.Join(',', addresses.Select(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]" : a.ToString()));
        return $"{hostBaseUrl.IdnHost}:{hostBaseUrl.Port.ToString(CultureInfo.InvariantCulture)}:{list}";
    }
}
