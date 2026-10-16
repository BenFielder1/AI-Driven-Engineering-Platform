using System.Globalization;
using Aide.Domain.Entities;

namespace Aide.Domain.GitHub;

/// <summary>Whether the AIDE GitHub App is registered (FR-80). Wire values: <c>not_configured</c>, <c>configured</c>.</summary>
public enum GitHubAppStatus
{
    NotConfigured = 0,
    Configured = 1,
}

/// <summary>Where the AIDE GitHub App comes from (FR-80): the manifest flow (<c>database</c>) or <c>GitHub__App__*</c>
/// configuration (<c>configuration</c>), which takes precedence.</summary>
public enum GitHubAppSource
{
    Database = 0,
    Configuration = 1,
}

/// <summary>How "Continue with GitHub" signs in (D-V2-7). Wire values: <c>app</c> (the App's user authorization flow with
/// PKCE), <c>oauth</c> (the v1 OAuth App, only while no App is registered), <c>none</c> (GitHub sign-in unavailable).</summary>
public enum GitHubSignInMode
{
    None = 0,
    Oauth = 1,
    App = 2,
}

/// <summary>Which repositories an App installation covers (FR-81). Wire values: <c>all</c>, <c>selected</c>.</summary>
public enum GitHubRepositorySelection
{
    All = 0,
    Selected = 1,
}

/// <summary>Pure GitHub integration rules (FR-80, FR-81, D-V2-7).</summary>
public static class GitHubRules
{
    /// <summary>GitHub's limit on a user or organisation login.</summary>
    public const int MaxLoginLength = 39;

    /// <summary>GitHub's limit on a repository name.</summary>
    public const int MaxRepoNameLength = 100;

    /// <summary>GitHub's limit on a repository description.</summary>
    public const int MaxRepoDescriptionLength = 350;

    /// <summary>A GitHub login: alphanumerics and single hyphens, not starting or ending with a hyphen.</summary>
    public const string LoginPattern = "^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9]))*$";

    /// <summary>A GitHub repository name: letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, but not <c>.</c> or <c>..</c>.</summary>
    public const string RepoNamePattern = @"^(?!\.{1,2}$)[A-Za-z0-9._-]+$";

    /// <summary>
    /// D-V2-7: once an App is registered, GitHub sign-in uses its user authorization flow; the v1 OAuth App applies only
    /// while no App is registered.
    /// </summary>
    public static GitHubSignInMode SignInMode(bool appConfigured, bool oauthConfigured) =>
        appConfigured ? GitHubSignInMode.App : oauthConfigured ? GitHubSignInMode.Oauth : GitHubSignInMode.None;

    // ---------------------------------------------------------------- FR-80 manifest

    /// <summary>GitHub's limit on a GitHub App name.</summary>
    public const int MaxAppNameLength = 34;

    /// <summary>
    /// §4.14: the App's repository permissions in the manifest: Contents, Pull requests, Workflows and Administration
    /// write (Administration is for "Create repository"; the Admin may untick it), Metadata, Checks and Commit statuses
    /// read. User permission: email addresses read (sign-in needs a verified email).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ManifestPermissions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["contents"] = "write",
        ["pull_requests"] = "write",
        ["workflows"] = "write",
        ["administration"] = "write",
        ["metadata"] = "read",
        ["checks"] = "read",
        ["statuses"] = "read",
        ["emails"] = "read",
    };

    /// <summary>FR-80: the App's name in the manifest, <c>AIDE (&lt;host&gt;)</c>, cut to GitHub's 34-character limit.</summary>
    public static string ManifestAppName(string host)
    {
        var name = $"AIDE ({host})";
        return name.Length <= MaxAppNameLength ? name : name[..(MaxAppNameLength - 1)] + ")";
    }

    /// <summary>FR-80: the App's public install page, which also lets the user pick (more) repositories.</summary>
    public static string InstallUrl(string slug) => $"https://github.com/apps/{Uri.EscapeDataString(slug)}/installations/new";

    /// <summary>FR-80: the App's public page.</summary>
    public static string AppHtmlUrl(string slug) => $"https://github.com/apps/{Uri.EscapeDataString(slug)}";

    /// <summary>
    /// FR-81: where an installation's repositories are managed: the user's or the organisation's installation settings.
    /// </summary>
    public static string ManageInstallationUrl(long installationId, string accountLogin, GitHubOwnerType accountType) =>
        accountType == GitHubOwnerType.Organization
            ? $"https://github.com/organizations/{Uri.EscapeDataString(accountLogin)}/settings/installations/{installationId.ToString(CultureInfo.InvariantCulture)}"
            : $"https://github.com/settings/installations/{installationId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>§4.14: "Create repository" needs the App's Administration write permission.</summary>
    public static bool CanCreateRepositories(IReadOnlyDictionary<string, string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return permissions.TryGetValue("administration", out var level) && level == "write";
    }

    /// <summary>GitHub's account type (<c>User</c>, <c>Organization</c>; <c>Bot</c> and others count as users).</summary>
    public static GitHubOwnerType OwnerTypeOf(string? githubType) =>
        string.Equals(githubType, "Organization", StringComparison.OrdinalIgnoreCase) ? GitHubOwnerType.Organization : GitHubOwnerType.User;

    // ---------------------------------------------------------------- FR-82 tokens and commits

    /// <summary>§4.14: installation tokens are cached for at most 55 minutes (GitHub issues them for an hour).</summary>
    public static readonly TimeSpan InstallationTokenCacheLimit = TimeSpan.FromMinutes(55);

    /// <summary>The App JWT is back-dated by a minute (clock drift) and lives under GitHub's 10-minute maximum.</summary>
    public static readonly TimeSpan JwtBackdate = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(9);

    /// <summary>A user token is refreshed when it expires within this margin (FR-81: before its 8-hour expiry).</summary>
    public static readonly TimeSpan UserTokenRefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// §4.14: until when a freshly minted installation token is used: at most 55 minutes after minting, and never within
    /// a minute of GitHub's own expiry.
    /// </summary>
    public static DateTimeOffset InstallationTokenUsableUntil(DateTimeOffset mintedAt, DateTimeOffset? expiresAt)
    {
        var limit = mintedAt + InstallationTokenCacheLimit;
        return expiresAt is { } expires && expires - TimeSpan.FromMinutes(1) < limit ? expires - TimeSpan.FromMinutes(1) : limit;
    }

    /// <summary>FR-81: whether a user token must be refreshed now (<c>null</c> expiry: it doesn't expire).</summary>
    public static bool NeedsRefresh(DateTimeOffset? accessExpiresAt, DateTimeOffset now) =>
        accessExpiresAt is { } expires && expires - now <= UserTokenRefreshMargin;

    /// <summary>FR-82: the App bot's login (<c>&lt;slug&gt;[bot]</c>), which commits are attributed to.</summary>
    public static string BotLogin(string slug) => $"{slug}[bot]";

    /// <summary>FR-82: the App bot's commit email, GitHub's noreply address for the bot user.</summary>
    public static string BotEmail(long botUserId, string slug) =>
        $"{botUserId.ToString(CultureInfo.InvariantCulture)}+{BotLogin(slug)}@users.noreply.github.com";

    // ---------------------------------------------------------------- §4.14 rate limits

    /// <summary>
    /// §4.14: when a GitHub response is a rate limit, the time it resets; <c>null</c> when it isn't one. A rate limit is a
    /// 403 or 429 with <c>x-ratelimit-remaining: 0</c> (reset at <c>x-ratelimit-reset</c>, epoch seconds) or with a
    /// <c>retry-after</c> (secondary limits; seconds). A 429 without either waits a minute.
    /// </summary>
    public static DateTimeOffset? RateLimitResetAt(int status, string? remaining, string? reset, string? retryAfter, DateTimeOffset now)
    {
        if (status is not (403 or 429))
        {
            return null;
        }

        if (long.TryParse(retryAfter?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return now + TimeSpan.FromSeconds(Math.Min(seconds, 86_400));
        }

        if (remaining?.Trim() == "0")
        {
            return long.TryParse(reset?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
                && DateTimeOffset.FromUnixTimeSeconds(Math.Min(epoch, 253_402_300_799)) is var at && at > now
                    ? at
                    : now + TimeSpan.FromMinutes(1);
        }

        return status == 429 ? now + TimeSpan.FromMinutes(1) : null;
    }

    /// <summary>Whole seconds until <paramref name="resetAt"/>, at least 1 (the <c>Retry-After</c> value).</summary>
    public static int RetryAfterSeconds(DateTimeOffset resetAt, DateTimeOffset now) =>
        (int)Math.Clamp(Math.Ceiling((resetAt - now).TotalSeconds), 1, int.MaxValue);

    // ---------------------------------------------------------------- FR-81, FR-86 repositories

    /// <summary>The files GitHub can add when it creates a repository (README, licence, .gitignore).</summary>
    private static readonly string[] StarterFiles = ["README", "README.md", "LICENSE", "LICENSE.md", "LICENSE.txt", ".gitignore"];

    /// <summary>
    /// FR-86: whether a repository "has no code": no commits, or only the files GitHub adds when it creates one
    /// (<c>auto_init</c>: README, licence, .gitignore). <paramref name="rootEntries"/> are the names at the root of the
    /// default branch (<c>null</c>: the repository has no commits).
    /// </summary>
    public static bool IsEmptyRepository(IReadOnlyCollection<string>? rootEntries) =>
        rootEntries is null || rootEntries.All(name => StarterFiles.Contains(name, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// FR-73, FR-86: the semantic version a tag names: the tag without <paramref name="tagPrefix"/> (default <c>v</c>) or a
    /// leading <c>v</c>; <c>null</c> when it isn't a semantic version.
    /// </summary>
    public static string? TagSemver(string tag, string tagPrefix = "v")
    {
        ArgumentNullException.ThrowIfNull(tag);
        var text = !string.IsNullOrEmpty(tagPrefix) && tag.StartsWith(tagPrefix, StringComparison.Ordinal) ? tag[tagPrefix.Length..] : tag;
        return Releases.SemVer.TryParse(text, out var version) ? version.ToString() : null;
    }

    /// <summary>
    /// FR-81: the owners offered for "Create repository": the caller first, then every organisation the caller belongs to
    /// or where the App is installed (by login, case-insensitively). <c>canCreate</c> needs the App installed on the owner
    /// and its Administration write permission.
    /// </summary>
    public static IReadOnlyList<GitHubOwnerOption> Owners(
        string userLogin,
        IReadOnlyCollection<GitHubInstallationRef> installations,
        IReadOnlyCollection<string> organizations,
        bool appCanCreateRepositories)
    {
        ArgumentNullException.ThrowIfNull(userLogin);
        ArgumentNullException.ThrowIfNull(installations);
        ArgumentNullException.ThrowIfNull(organizations);

        GitHubOwnerOption Option(string login, GitHubOwnerType type)
        {
            var installation = installations.FirstOrDefault(i => i.AccountType == type && string.Equals(i.AccountLogin, login, StringComparison.OrdinalIgnoreCase));
            return new GitHubOwnerOption(login, type, installation?.Id, installation is not null, installation is not null && appCanCreateRepositories);
        }

        var owners = new List<GitHubOwnerOption> { Option(userLogin, GitHubOwnerType.User) };
        var orgs = installations.Where(i => i.AccountType == GitHubOwnerType.Organization).Select(i => i.AccountLogin)
            .Concat(organizations)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        owners.AddRange(orgs.Select(org => Option(org, GitHubOwnerType.Organization)));
        return owners;
    }
}

/// <summary>An App installation the caller can access: its id and the account it is installed on.</summary>
public sealed record GitHubInstallationRef(long Id, string AccountLogin, GitHubOwnerType AccountType);

/// <summary>FR-81: a possible owner for "Create repository".</summary>
public sealed record GitHubOwnerOption(string Login, GitHubOwnerType Type, long? InstallationId, bool AppInstalled, bool CanCreate);
