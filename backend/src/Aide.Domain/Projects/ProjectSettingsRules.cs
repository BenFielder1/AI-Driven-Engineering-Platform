using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Entities;

namespace Aide.Domain.Projects;

/// <summary>A GitHub repository parsed from <c>https://github.com/OWNER/REPO(.git)</c>.</summary>
public sealed record GitHubRepo(string Owner, string Name);

/// <summary>The saved state of a channel that an update refers to by id (FR-9, FR-47).</summary>
public sealed record SavedChannel(NotificationChannelType Type, string Target, bool HasSecret);

/// <summary>
/// A notification channel in a settings update (FR-9, FR-47): the sent <paramref name="Target"/>, the sent
/// <paramref name="Secret"/> (<c>null</c> when not sent), <paramref name="ClearSecret"/>, and the saved channel with
/// the same id, if any.
/// </summary>
public sealed record ChannelCandidate(
    NotificationChannelType Type,
    string? Target,
    string? Secret = null,
    bool ClearSecret = false,
    SavedChannel? Saved = null);

/// <summary>What happens to a channel's stored secret in an update.</summary>
public enum ChannelSecretChange
{
    /// <summary>The saved (encrypted) secret is kept as is.</summary>
    Keep,

    /// <summary><see cref="ChannelPlan.NewSecret"/> replaces the secret (to be encrypted).</summary>
    Set,

    /// <summary>The channel has no secret.</summary>
    Clear,
}

/// <summary>How a valid <see cref="ChannelCandidate"/> is stored: the plain <see cref="StoredTarget"/> and the
/// secret change. For Slack and Teams the incoming-webhook URL is the secret and the stored target is only its
/// masked display form (<see cref="ProjectSettingsRules.MaskUrl"/>).</summary>
public sealed record ChannelPlan(string StoredTarget, ChannelSecretChange Secret, string? NewSecret);

/// <summary>FR-9, FR-10, FR-22: rules for persisted Git, LLM and notification settings.</summary>
public static partial class ProjectSettingsRules
{
    public const string DefaultBranch = "main";

    // ---------------------------------------------------------------- Git

    /// <summary>
    /// Parses <c>https://github.com/OWNER/REPO</c>, optionally ending in <c>.git</c> or a trailing slash. Owners are
    /// GitHub logins (letters, digits, single hyphens, ≤ 39); repository names are letters, digits, '.', '_' and '-'
    /// (≤ 100, not "." or ".."). No query, fragment, credentials or extra path segments.
    /// </summary>
    public static bool TryParseGitHubRepo(string? url, out GitHubRepo repo)
    {
        repo = new GitHubRepo(string.Empty, string.Empty);
        if (url is null)
        {
            return false;
        }

        var match = GitHubRepoUrl().Match(url);
        if (!match.Success)
        {
            return false;
        }

        var name = match.Groups["repo"].Value;
        if (name is "." or ".." || name.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        repo = new GitHubRepo(match.Groups["owner"].Value, name);
        return true;
    }

    /// <summary>
    /// A safe Git branch name (subset of <c>git check-ref-format</c>): 1–255 characters, no whitespace or control
    /// characters, none of <c>~^:?*[\</c>, no "..", "@{", "//", no leading '-' or '/', no trailing '/', '.' or
    /// ".lock", and no path component starting with '.'.
    /// </summary>
    public static bool IsValidBranchName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255)
        {
            return false;
        }

        if (name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
        {
            return false;
        }

        if (name.Contains("..", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal)
            || name.Contains("//", StringComparison.Ordinal) || name == "@")
        {
            return false;
        }

        if (name[0] is '-' or '/' || name[^1] is '/' or '.' || name.EndsWith(".lock", StringComparison.Ordinal))
        {
            return false;
        }

        return !name.Split('/').Any(part => part.StartsWith('.'));
    }

    /// <summary>
    /// Validates Git settings as they will be stored. A <c>github</c> provider needs a GitHub repository URL; the
    /// default branch must be a safe branch name. <paramref name="prefix"/> is prepended to field names
    /// (e.g. <c>"git."</c> in the wizard).
    /// </summary>
    public static Result ValidateGit(
        GitProvider provider,
        string? repoUrl,
        string? defaultBranch,
        string? tokenSent = null,
        bool clearToken = false,
        string prefix = "")
    {
        var problems = new List<string>();
        if (provider == GitProvider.Github && !TryParseGitHubRepo(repoUrl, out _))
        {
            problems.Add($"{prefix}repoUrl: Use a GitHub repository URL like https://github.com/owner/repo.");
        }

        if (!IsValidBranchName(defaultBranch))
        {
            problems.Add($"{prefix}defaultBranch: Use a valid Git branch name.");
        }

        if (tokenSent is not null && clearToken)
        {
            problems.Add($"{prefix}token: Send either token or clearToken, not both.");
        }
        else if (tokenSent is not null && string.IsNullOrWhiteSpace(tokenSent))
        {
            problems.Add($"{prefix}token: The token can't be blank. Send clearToken to remove it.");
        }

        return ProjectRules.ToResult(problems, "The Git settings are invalid.");
    }

    /// <summary>
    /// Whether the saved Git token survives an update that doesn't send a new one: not when <c>clearToken</c> is sent,
    /// and not when the provider changes to <c>local</c> (which has no remote, so a Local project never keeps an old
    /// GitHub token).
    /// </summary>
    public static bool KeepsSavedToken(GitProvider previous, GitProvider next, bool clearToken) =>
        !clearToken && !(next == GitProvider.Local && previous != GitProvider.Local);

    /// <summary>The URL stored for a provider: trimmed for <c>github</c>; the Local provider has no remote URL.</summary>
    public static string? NormalizeRepoUrl(GitProvider provider, string? repoUrl) =>
        provider == GitProvider.Github && !string.IsNullOrWhiteSpace(repoUrl) ? repoUrl.Trim() : null;

    // ---------------------------------------------------------------- LLM

    /// <summary>Anthropic and OpenAI need an API key; Ollama and Fake don't.</summary>
    public static bool RequiresApiKey(LlmProvider provider) => provider is LlmProvider.Anthropic or LlmProvider.Openai;

    /// <summary>
    /// "Missing credentials never block": Fake and Ollama are always usable; Anthropic and OpenAI are usable when a
    /// system key is configured or the project has its own key for that provider.
    /// </summary>
    public static bool IsConfigured(LlmProvider provider, bool hasSystemKey, bool hasProjectKey) =>
        !RequiresApiKey(provider) || hasSystemKey || hasProjectKey;

    /// <summary>The provider for a new project: the configured default if it is usable without a project key,
    /// otherwise <see cref="LlmProvider.Fake"/>.</summary>
    public static LlmProvider DefaultProviderForNewProject(LlmProvider configuredDefault, bool defaultHasSystemKey) =>
        IsConfigured(configuredDefault, defaultHasSystemKey, hasProjectKey: false) ? configuredDefault : LlmProvider.Fake;

    /// <summary>Validates LLM settings as they will be stored (after a partial update).</summary>
    public static Result ValidateLlm(
        LlmProvider provider,
        string? defaultModel,
        IReadOnlyDictionary<string, string?>? modelsByAgent,
        bool apiKeySent,
        bool apiKeySentBlank,
        bool clearApiKey,
        bool hasSystemKey,
        bool hasProjectKeyAfter)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(defaultModel))
        {
            problems.Add("defaultModel: A default model is required.");
        }

        foreach (var (agent, model) in modelsByAgent ?? new Dictionary<string, string?>())
        {
            if (model is not null && string.IsNullOrWhiteSpace(model))
            {
                problems.Add($"modelsByAgent.{agent}: A model can't be blank; omit it to use the default model.");
            }
        }

        if (apiKeySent && clearApiKey)
        {
            problems.Add("apiKey: Send either apiKey or clearApiKey, not both.");
        }
        else if (apiKeySentBlank)
        {
            problems.Add("apiKey: The API key can't be blank. Send clearApiKey to remove it.");
        }
        else if (apiKeySent && !RequiresApiKey(provider))
        {
            problems.Add($"apiKey: {provider.ToWire()} doesn't use an API key.");
        }

        if (problems.Count == 0 && !IsConfigured(provider, hasSystemKey, hasProjectKeyAfter))
        {
            problems.Add($"provider: {provider.ToWire()} is not configured. Add a project API key, or ask an admin to set the system key.");
        }

        return ProjectRules.ToResult(problems, "The LLM settings are invalid.");
    }

    // ---------------------------------------------------------------- Notifications

    /// <summary>
    /// Validates notification channels (FR-9, FR-47):
    /// <list type="bullet">
    /// <item>email: the target is an email address; no secret.</item>
    /// <item>webhook: the target is an absolute <c>https</c> URL without credentials; the secret is an optional HMAC
    /// signing key.</item>
    /// <item>slack / teams: the incoming-webhook URL is a bearer credential, so it is the channel's <em>secret</em>
    /// (an <c>https</c> URL). It is sent in <c>secret</c>, or, for older clients, as an absolute URL in
    /// <c>target</c>; otherwise the saved URL of the same channel (same id and type) is kept. A channel without a URL
    /// is invalid, so <c>clearSecret</c> alone isn't allowed.</item>
    /// </list>
    /// A sent secret can't be blank or combined with <c>clearSecret</c>.
    /// </summary>
    public static Result ValidateChannels(IReadOnlyList<ChannelCandidate> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var problems = new List<string>();
        for (var i = 0; i < channels.Count; i++)
        {
            ValidateChannel(channels[i], $"channels[{i}]", problems);
        }

        return ProjectRules.ToResult(problems, "The notification settings are invalid.");
    }

    /// <summary>How a channel that passed <see cref="ValidateChannels"/> is stored.</summary>
    public static ChannelPlan PlanChannel(ChannelCandidate channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var target = channel.Target?.Trim() ?? string.Empty;
        var keepable = KeepableSecret(channel);
        switch (channel.Type)
        {
            case NotificationChannelType.Email:
                return new ChannelPlan(target, ChannelSecretChange.Clear, null);

            case NotificationChannelType.Webhook:
                if (channel.Secret is { } hmac)
                {
                    return new ChannelPlan(target, ChannelSecretChange.Set, hmac);
                }

                return new ChannelPlan(target, keepable ? ChannelSecretChange.Keep : ChannelSecretChange.Clear, null);

            default:
                if (SentWebhookUrl(channel) is { } url)
                {
                    return new ChannelPlan(MaskUrl(url), ChannelSecretChange.Set, url);
                }

                if (keepable)
                {
                    return new ChannelPlan(channel.Saved!.Target, ChannelSecretChange.Keep, null);
                }

                throw new InvalidOperationException("The channel has no incoming-webhook URL; validate it first.");
        }
    }

    /// <summary>
    /// The display form of a URL that is a secret: <c>host/…</c> (e.g. <c>hooks.slack.com/…</c>), never the path or
    /// query that carry the credential. A value that isn't an absolute URL is returned unchanged (it is already masked).
    /// </summary>
    public static string MaskUrl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? $"{uri.Host}/…"
            : value;
    }

    /// <summary>Slack and Teams channels keep their incoming-webhook URL as an encrypted secret.</summary>
    public static bool UrlIsSecret(NotificationChannelType type) =>
        type is NotificationChannelType.Slack or NotificationChannelType.Teams;

    private static void ValidateChannel(ChannelCandidate channel, string field, List<string> problems)
    {
        var target = channel.Target?.Trim() ?? string.Empty;
        var secretSent = channel.Secret is not null;
        if (secretSent && channel.ClearSecret)
        {
            problems.Add($"{field}.secret: Send either secret or clearSecret, not both.");
            return;
        }

        if (secretSent && string.IsNullOrWhiteSpace(channel.Secret))
        {
            problems.Add($"{field}.secret: The secret can't be blank. Send clearSecret to remove it.");
            return;
        }

        switch (channel.Type)
        {
            case NotificationChannelType.Email:
                if (!IsEmail(target))
                {
                    problems.Add($"{field}.target: Use an email address.");
                }

                if (secretSent)
                {
                    problems.Add($"{field}.secret: Email channels have no secret.");
                }

                break;

            case NotificationChannelType.Webhook:
                if (!IsHttpsUrl(target))
                {
                    problems.Add($"{field}.target: Use an https:// URL.");
                }

                break;

            default:
                var label = channel.Type == NotificationChannelType.Slack ? "Slack" : "Teams";
                if (secretSent)
                {
                    if (!IsHttpsUrl(channel.Secret!.Trim()))
                    {
                        problems.Add($"{field}.secret: Use the https:// incoming-webhook URL from {label}.");
                    }
                }
                else if (Uri.TryCreate(target, UriKind.Absolute, out _))
                {
                    // An older client sending the URL as the target.
                    if (!IsHttpsUrl(target))
                    {
                        problems.Add($"{field}.target: Use an https:// URL.");
                    }
                }
                else if (!KeepableSecret(channel))
                {
                    problems.Add($"{field}.secret: Enter the {label} incoming-webhook URL.");
                }

                break;
        }
    }

    /// <summary>The saved secret can be kept: same channel type, not cleared, and one is saved.</summary>
    private static bool KeepableSecret(ChannelCandidate channel) =>
        !channel.ClearSecret && channel.Saved is { HasSecret: true } saved && saved.Type == channel.Type;

    /// <summary>A Slack/Teams URL sent in this update: <c>secret</c>, else an absolute URL in <c>target</c>.</summary>
    private static string? SentWebhookUrl(ChannelCandidate channel)
    {
        if (channel.Secret is { } secret)
        {
            return secret.Trim();
        }

        var target = channel.Target?.Trim();
        return Uri.TryCreate(target, UriKind.Absolute, out _) ? target : null;
    }

    public static bool IsEmail(string? value) => value is not null && value.Length <= 256 && EmailPattern().IsMatch(value);

    public static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && !string.IsNullOrEmpty(uri.Host);

    [GeneratedRegex(@"^https://github\.com/(?<owner>[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38})/(?<repo>[A-Za-z0-9._-]{1,100}?)(?:\.git)?/?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex GitHubRepoUrl();

    [GeneratedRegex(@"^[^@\s<>()\[\],;:""]+@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex EmailPattern();
}
