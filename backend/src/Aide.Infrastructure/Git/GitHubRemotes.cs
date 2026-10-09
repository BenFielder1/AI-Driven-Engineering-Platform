using System.Globalization;
using System.Net;
using System.Text;
using Aide.Domain.Projects;

namespace Aide.Infrastructure.Git;

/// <summary>
/// FR-35: how AIDE reaches a GitHub repository with git. The URL is <c>{GitHub:GitBaseUrl}{owner}/{repo}.git</c> — the host
/// is system configuration, never the project's — and holds no credentials. The token travels only as a per-command
/// <c>http.&lt;base&gt;.extraHeader</c> in the git child process's environment (<c>GIT_CONFIG_KEY_n</c>/<c>VALUE_n</c>), so it is
/// never written to <c>.git/config</c>, the remote URL, the command line, logs, events or error text.
/// </summary>
public static class GitHubRemotes
{
    /// <summary>The user name GitHub expects with an access token over HTTPS basic auth.</summary>
    public const string TokenUser = "x-access-token";

    public static GitRemote Create(GitHubGitOptions options, GitHubRepo repo, string token)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrEmpty(token);

        var baseUrl = options.NormalizedGitBaseUrl;
        var url = $"{baseUrl}{repo.Owner}/{repo.Name}.git";
        var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{TokenUser}:{token}"));
        var header = $"Authorization: Basic {credential}";
        var https = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // The header is scoped to the configured host. Only https may be used, unless tests allow a local bare repository.
        List<(string Key, string Value)> settings =
        [
            (https ? $"http.{baseUrl}.extraHeader" : "http.extraHeader", header),
            ("protocol.allow", "never"),
            ("protocol.https.allow", "always"),
        ];
        if (options.AllowInsecureGitBaseUrl)
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
}

/// <summary>Whether a GitHub or git failure is worth retrying (FR-25-style job retries) or needs a person.</summary>
public static class GitHubFailures
{
    private static readonly string[] PermanentGitErrors =
    [
        "authentication failed",
        "could not read username",
        "could not read password",
        "invalid username or password",
        "permission to",
        "repository not found",
        "not found",
        "returned error: 401",
        "returned error: 403",
        "returned error: 404",
        "[rejected]",
        "rejected",
        "non-fast-forward",
        "fetch first",
        "protected branch",
        "denied",
        "does not appear to be a git repository",
    ];

    /// <summary>
    /// A git clone/fetch/push error a retry can't fix: bad or insufficient credentials, a missing repository, or a push
    /// rejected because the remote branch moved or is protected. Anything else (network, timeouts, 5xx) is transient.
    /// </summary>
    public static bool IsTransientGitError(string? error)
    {
        var text = (error ?? string.Empty).ToLowerInvariant();
        return !PermanentGitErrors.Any(p => text.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// A GitHub REST response a retry can fix: 5xx, 408, 429, or 403 caused by a rate limit (<paramref name="rateLimited"/>:
    /// <c>x-ratelimit-remaining: 0</c> or a <c>retry-after</c> header). 401, other 403s, 404 and 422 are permanent.
    /// </summary>
    public static bool IsTransientStatus(HttpStatusCode status, bool rateLimited) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (status == HttpStatusCode.Forbidden && rateLimited);
}
