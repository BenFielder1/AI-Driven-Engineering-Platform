using System.Net;

namespace Aide.Cli.Config;

internal static class EnvironmentVariables
{
    public const string Config = "AIDE_CONFIG";
    public const string ApiUrl = "AIDE_API_URL";
    public const string Token = "AIDE_TOKEN";
    public const string GitToken = "AIDE_GIT_TOKEN";
}

/// <summary>Where an effective setting came from (shown by `config get`).</summary>
internal enum SettingSource
{
    Default,
    File,
    Environment,
    Flag,
}

/// <summary>
/// Effective API URL and token. API URL precedence: --api-url flag &gt; AIDE_API_URL &gt; config file &gt; default.
/// Token: AIDE_TOKEN (explicit, sent to whatever URL is in effect) &gt; the saved token, which is used only when the effective
/// API URL is the one it was saved for (<see cref="CliConfig.TokenApiUrl"/>; compared after normalising scheme, host, port
/// and path). A saved token is never sent to another server just because --api-url or AIDE_API_URL points elsewhere.
/// </summary>
internal sealed record CliSettings(string ApiUrl, SettingSource ApiUrlSource, string? Token, SettingSource TokenSource)
{
    public const string DefaultApiUrl = "http://localhost:8080";

    /// <summary>The URL the saved token belongs to, when a saved token exists but is not used for <see cref="ApiUrl"/>.</summary>
    public string? SavedTokenApiUrl { get; init; }

    /// <summary>The saved token exists but belongs to another API URL (so it is withheld).</summary>
    public bool SavedTokenWithheld => SavedTokenApiUrl is not null;

    public static CliSettings Resolve(string? apiUrlFlag, Func<string, string?> getEnvironmentVariable, CliConfig config)
    {
        var (apiUrl, apiUrlSource) = Pick(apiUrlFlag, getEnvironmentVariable(EnvironmentVariables.ApiUrl), config.ApiUrl);
        var effectiveUrl = apiUrl ?? DefaultApiUrl;

        var environmentToken = getEnvironmentVariable(EnvironmentVariables.Token);
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            return new CliSettings(effectiveUrl, apiUrlSource, environmentToken.Trim(), SettingSource.Environment);
        }

        if (string.IsNullOrWhiteSpace(config.Token))
        {
            return new CliSettings(effectiveUrl, apiUrlSource, null, SettingSource.Default);
        }

        // A file written before tokens were bound to a URL: the token belongs to the file's own API URL (or the default).
        var boundTo = config.TokenApiUrl ?? config.ApiUrl ?? DefaultApiUrl;
        return SameApi(boundTo, effectiveUrl)
            ? new CliSettings(effectiveUrl, apiUrlSource, config.Token.Trim(), SettingSource.File)
            : new CliSettings(effectiveUrl, apiUrlSource, null, SettingSource.Default) { SavedTokenApiUrl = boundTo };
    }

    /// <summary>True when both are valid API URLs with the same scheme, host, port and path (after normalisation).</summary>
    public static bool SameApi(string a, string b) =>
        TryNormalizeApiUrl(a, out var left) && TryNormalizeApiUrl(b, out var right) && Key(left) == Key(right);

    /// <summary>The canonical form stored with a saved token: scheme://host:port/path/ (lower-case scheme and host, explicit port).</summary>
    public static string Canonical(Uri baseUri) => Key(baseUri);

    private static string Key(Uri uri) => $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}{uri.AbsolutePath}";

    /// <summary>
    /// Plain http is allowed only to loopback (localhost, 127.0.0.0/8, ::1) so a token never crosses a network in clear text.
    /// </summary>
    public static bool IsAllowedForToken(Uri baseUri) =>
        baseUri.Scheme == Uri.UriSchemeHttps
        || string.Equals(baseUri.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(baseUri.IdnHost.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private static (string? Value, SettingSource Source) Pick(string? flag, string? environment, string? file)
    {
        if (!string.IsNullOrWhiteSpace(flag))
        {
            return (flag.Trim(), SettingSource.Flag);
        }

        if (!string.IsNullOrWhiteSpace(environment))
        {
            return (environment.Trim(), SettingSource.Environment);
        }

        if (!string.IsNullOrWhiteSpace(file))
        {
            return (file.Trim(), SettingSource.File);
        }

        return (null, SettingSource.Default);
    }

    /// <summary>Parses an API base URL (http/https, absolute). A trailing `/api` is accepted and dropped.</summary>
    public static bool TryNormalizeApiUrl(string value, out Uri baseUri)
    {
        baseUri = null!;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        var path = parsed.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        baseUri = new UriBuilder(parsed) { Path = path + "/" }.Uri;
        return true;
    }
}

/// <summary>Shows enough of a token to recognise it, never enough to use it.</summary>
internal static class TokenMask
{
    private const string Prefix = "aide_pat_";

    public static string Mask(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "(not set)";
        }

        if (token.Length < 16)
        {
            return "********";
        }

        var prefix = token.StartsWith(Prefix, StringComparison.Ordinal) ? Prefix : string.Empty;
        return $"{prefix}********{token[^4..]}";
    }
}
