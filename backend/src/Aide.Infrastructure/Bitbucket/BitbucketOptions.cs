using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Bitbucket;

/// <summary>
/// Bitbucket Cloud settings (section <c>Bitbucket</c>, FR-133, §9):
/// <list type="bullet">
/// <item><c>Bitbucket:OAuth:ClientId</c> / <c>Bitbucket:OAuth:ClientSecret</c>: the OAuth consumer's key and secret from
/// <c>secrets.env</c> or Railway variables (<c>Bitbucket__OAuth__*</c>). When the key is set they take precedence over the
/// consumer an Admin saved in Admin → Integrations → Bitbucket; the secret never reaches the database.</item>
/// <item><c>Bitbucket:RequestTimeout</c> (default 20 s): per Bitbucket API request.</item>
/// </list>
/// The hosts are fixed (<c>https://api.bitbucket.org/2.0</c>, <c>https://bitbucket.org</c>, §4.19) and never configurable.
/// </summary>
public sealed class BitbucketOptions
{
    public const string SectionName = "Bitbucket";

    public BitbucketOAuthSettings OAuth { get; set; } = new();

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Tests only: clone and push go to <c>{GitBaseUrlOverride}{workspace/slug}.git</c> instead of bitbucket.org (a
    /// <c>file://</c> folder of bare repositories). Needs <see cref="AllowInsecureGitBaseUrl"/>. Never set in a deployment.
    /// </summary>
    public string? GitBaseUrlOverride { get; set; }

    /// <summary>Tests only: allows a <c>file://</c> or <c>http://</c> <see cref="GitBaseUrlOverride"/>.</summary>
    public bool AllowInsecureGitBaseUrl { get; set; }
}

/// <summary>The configured OAuth consumer (<c>Bitbucket__OAuth__ClientId</c>, <c>Bitbucket__OAuth__ClientSecret</c>).</summary>
public sealed class BitbucketOAuthSettings
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>The consumer comes from configuration (its key is set).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>Startup checks for <see cref="BitbucketOptions"/>.</summary>
public sealed class BitbucketOptionsValidator : IValidateOptions<BitbucketOptions>
{
    public ValidateOptionsResult Validate(string? name, BitbucketOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add("Bitbucket:RequestTimeout must be positive.");
        }

        if (!string.IsNullOrWhiteSpace(options.GitBaseUrlOverride)
            && (!Uri.TryCreate(options.GitBaseUrlOverride, UriKind.Absolute, out var gitBase)
                || (gitBase.Scheme != Uri.UriSchemeHttps && !options.AllowInsecureGitBaseUrl)
                || !string.IsNullOrEmpty(gitBase.UserInfo) || !string.IsNullOrEmpty(gitBase.Query)))
        {
            failures.Add("Bitbucket:GitBaseUrlOverride must be an absolute https URL without credentials or a query (file:// or http:// only with Bitbucket:AllowInsecureGitBaseUrl).");
        }

        if (options.OAuth.IsConfigured != !string.IsNullOrWhiteSpace(options.OAuth.ClientSecret))
        {
            failures.Add("Bitbucket:OAuth:ClientId and Bitbucket:OAuth:ClientSecret must be set together.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
